using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Lidarr.Plugin.AIImport.Approval;
using Lidarr.Plugin.AIImport.Configuration;
using Lidarr.Plugin.AIImport.Lidarr;
using Lidarr.Plugin.AIImport.Llm;
using Lidarr.Plugin.AIImport.Matching;
using Lidarr.Plugin.AIImport.Workflows;

namespace Lidarr.Plugin.AIImport.Discovery;

public sealed class FailedImportDiscoveryWorkflow : IAiImportDiscoverWorkflow
{
    public const string CompatibleLidarrVersion = "3.1.2.4913";
    public const int MaxUntrackedFoldersPerScan = 25;

    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".flac", ".mp3", ".wav", ".m4a", ".aac", ".ogg", ".opus", ".aif", ".aiff", ".ape", ".wv", ".alac"
    };

    private readonly IInProcessLidarrGateway _gateway;
    private readonly DeterministicMatcher _matcher;
    private readonly ILlmMatchService? _llm;
    private readonly StandaloneAnalysisStore? _analysisStore;
    private readonly IYoloLibraryAdder? _yolo;

    public FailedImportDiscoveryWorkflow(
        IInProcessLidarrGateway gateway,
        DeterministicMatcher matcher,
        ILlmMatchService? llm)
    {
        _gateway = gateway;
        _matcher = matcher;
        _llm = llm;
    }

    public FailedImportDiscoveryWorkflow(
        IInProcessLidarrGateway gateway,
        DeterministicMatcher matcher,
        ILlmMatchService? llm,
        StandaloneAnalysisStore analysisStore)
        : this(gateway, matcher, llm)
    {
        _analysisStore = analysisStore;
    }

    public FailedImportDiscoveryWorkflow(
        IInProcessLidarrGateway gateway,
        DeterministicMatcher matcher,
        ILlmMatchService? llm,
        StandaloneAnalysisStore analysisStore,
        IYoloLibraryAdder yoloLibraryAdder)
        : this(gateway, matcher, llm, analysisStore)
    {
        _yolo = yoloLibraryAdder;
    }

    public AiImportDiscoverResult Discover(AiImportSettings settings)
    {
        if (!string.Equals(_gateway.Version, CompatibleLidarrVersion, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Lidarr {CompatibleLidarrVersion} is required; running version is {_gateway.Version}.");
        }

        var failed = _gateway.GetTrackedDownloads()
            .Where(x => x.State is TrackedImportState.ImportBlocked or TrackedImportState.ImportFailed)
            .GroupBy(x => x.DownloadId, StringComparer.Ordinal)
            .Select(x => x.OrderBy(item => item.Id).First())
            .OrderBy(x => x.Id)
            .ThenBy(x => x.DownloadId, StringComparer.Ordinal)
            .ToArray();

        var analyses = new List<AiImportAnalysisDto>();
        var diagnostics = new List<AiImportDiscoveryDiagnostic>();
        foreach (var item in failed)
        {
            try
            {
                var scan = _gateway.GetManualImportScan(item.DownloadId);
                var analysis = AnalyzeScan(
                    scan, item.DownloadId, null, AiImportSourceKinds.Queue, item.Title, item.AlbumId, settings,
                    out var diagnostic);
                if (diagnostic is not null) diagnostics.Add(diagnostic);
                if (analysis is not null) analyses.Add(analysis);
            }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
            {
                // A download may disappear between the tracked-download snapshot and its manual-import scan.
                // Skip only that stale item; never let it hide other independently reviewable candidates.
            }
        }

        var folderAnalyses = ScanUntrackedFolders(settings, diagnostics);
        analyses.AddRange(folderAnalyses);

        if (settings.YoloMode)
        {
            ApplyYoloMode(analyses, settings, diagnostics);
        }

        _analysisStore?.Replace(analyses.Select(x =>
            new StoredStandaloneAnalysis(x.AnalysisId, x.DownloadId, x.Proposal)
            {
                FolderPath = x.FolderPath
            }).ToArray());
        return new AiImportDiscoverResult(analyses.ToArray(), diagnostics.ToArray(), settings.YoloMode);
    }

    private List<AiImportAnalysisDto> ScanUntrackedFolders(
        AiImportSettings settings,
        List<AiImportDiscoveryDiagnostic> diagnostics)
    {
        var roots = AiImportSettingsValidator.SplitScanRoots(settings.ScanRoots);
        var analyses = new List<AiImportAnalysisDto>();
        foreach (var root in roots)
        {
            if (!Path.IsPathFullyQualified(root))
            {
                diagnostics.Add(new AiImportDiscoveryDiagnostic(root, 0, 0, "Skipped: untracked scan folder must be an absolute path."));
                continue;
            }
            if (!Directory.Exists(root))
            {
                diagnostics.Add(new AiImportDiscoveryDiagnostic(root, 0, 0, "Skipped: untracked scan folder does not exist."));
                continue;
            }

            var candidates = EnumerateCandidateAlbumFolders(root)
                .ToArray();
            var capped = candidates
                .OrderByDescending(x => SafeLastWriteTimeUtc(x))
                .Take(MaxUntrackedFoldersPerScan)
                .ToArray();
            if (capped.Length < candidates.Length)
            {
                diagnostics.Add(new AiImportDiscoveryDiagnostic(root, 0, 0,
                    $"Scanned the {MaxUntrackedFoldersPerScan} newest album folders; {candidates.Length - capped.Length} older folders were skipped. Move finished albums out of the scan folder to import them."));
            }

            foreach (var folder in capped)
            {
                try
                {
                    var scan = _gateway.GetUntrackedManualImportScan(folder);
                    var analysis = AnalyzeScan(
                        scan, string.Empty, folder, AiImportSourceKinds.Folder,
                        Path.GetFileName(folder.TrimEnd('/', '\\')), null, settings, out var diagnostic);
                    if (diagnostic is not null) diagnostics.Add(diagnostic);
                    if (analysis is not null) analyses.Add(analysis);
                }
                catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException or InvalidDataException)
                {
                    diagnostics.Add(new AiImportDiscoveryDiagnostic(folder, 0, 0,
                        $"Skipped {Path.GetFileName(folder)}: {exception.Message}"));
                }
            }
        }
        return analyses;
    }

    internal static IEnumerable<string> EnumerateCandidateAlbumFolders(string root)
    {
        var folders = new List<string>();
        foreach (var sub in SafeEnumerateDirectories(root))
        {
            folders.Add(sub);
            foreach (var nested in SafeEnumerateDirectories(sub))
            {
                folders.Add(nested);
            }
        }
        return folders
            .Where(ContainsTopLevelAudioFile)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal);
    }

    private static bool ContainsTopLevelAudioFile(string folder)
    {
        if (!Directory.Exists(folder)) return false;
        var files = SafeEnumerateFiles(folder);
        var inspected = 0;
        foreach (var file in files)
        {
            if (Path.GetExtension(file).Length > 0 && AudioExtensions.Contains(Path.GetExtension(file)))
                return true;
            if (++inspected >= 500) break;
        }
        return false;
    }

    private static DateTime SafeLastWriteTimeUtc(string path)
    {
        try
        {
            return Directory.Exists(path) ? Directory.GetLastWriteTimeUtc(path) : DateTime.MinValue;
        }
        catch (Exception)
        {
            return DateTime.MinValue;
        }
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string path)
    {
        try
        {
            return Directory.EnumerateDirectories(path);
        }
        catch (Exception)
        {
            return [];
        }
    }

    private static IEnumerable<string> SafeEnumerateFiles(string path)
    {
        try
        {
            return Directory.EnumerateFiles(path);
        }
        catch (Exception)
        {
            return [];
        }
    }

    private AiImportAnalysisDto? AnalyzeScan(
        ManualImportScanResult scan,
        string downloadId,
        string? folderPath,
        string sourceKind,
        string title,
        int? queueAlbumId,
        AiImportSettings settings,
        out AiImportDiscoveryDiagnostic? diagnostic)
    {
        var files = scan.Files;
        var sourceId = string.IsNullOrWhiteSpace(downloadId) ? folderPath ?? string.Empty : downloadId;
        diagnostic = scan.AlreadyImportedCount == 0 && scan.IndeterminateCount == 0
            ? null
            : new AiImportDiscoveryDiagnostic(
                sourceId,
                scan.AlreadyImportedCount,
                scan.IndeterminateCount,
                scan.IndeterminateCount == 0
                    ? "Files already imported as verified hardlinks were excluded."
                    : "Files with contradictory import evidence were excluded and require Lidarr reconciliation.");
        if (files.Count == 0) return null;
        var albumIds = GetCandidateAlbumIds(files, queueAlbumId);
        var candidates = albumIds.SelectMany(_gateway.GetReleaseCandidates)
            .GroupBy(x => x.Id)
            .Select(x => x.First())
            .OrderBy(x => x.Id)
            .ToArray();

        var deterministic = _matcher.Match(files, candidates);
        var proposal = ToProposal(deterministic, candidates);
        var usedLlm = false;
        if (deterministic.Status == DeterministicStatus.NeedsLlm && _llm is not null)
        {
            usedLlm = true;
            try
            {
                proposal = _llm.Match(settings, files, candidates, deterministic.RankedReleaseIds);
            }
            catch (Exception exception) when (exception is ProposalValidationException or HttpRequestException or LlmProtocolException or TaskCanceledException)
            {
                proposal = ManualReview($"Local-model ranking failed safely: {exception.Message}");
            }
        }

        try
        {
            proposal = StrictProposalValidator.Validate(proposal, files, candidates);
        }
        catch (ProposalValidationException exception)
        {
            proposal = ManualReview($"Proposal failed strict validation: {exception.Message}");
        }

        proposal = PromoteYoloProposal(proposal, deterministic, files, candidates, settings);

        var safeFiles = files.Select(x => new SourceFileDto(
            x.Id, x.Name, x.Artist, x.Album, x.Title, x.DiscNumber, x.TrackNumbers.ToArray(), x.DurationMs)).ToArray();
        var safeCandidates = candidates.Select(x => new ReleaseCandidateDto(
            x.AlbumId, x.Id, x.ArtistId, x.Artist, x.Title, x.ForeignReleaseId,
            x.Tracks.Select(track => new ReleaseTrackDto(
                track.Id, track.Title, track.DiscNumber, track.TrackNumber, track.DurationMs, track.HasFile)).ToArray())).ToArray();
        var libraryAddition = candidates.Length == 0 ? GetLibraryAddition(files) : null;
        return new AiImportAnalysisDto(
            ComputeAnalysisId(sourceId, sourceKind, queueAlbumId, files, candidates, proposal),
            downloadId,
            title,
            proposal,
            safeFiles,
            safeCandidates,
            usedLlm,
            libraryAddition,
            sourceKind,
            folderPath);
    }

    internal static LibraryAdditionDto? GetLibraryAddition(IReadOnlyList<SourceFileCandidate> files)
    {
        if (files.Count == 0) return null;
        var artistIds = files.Select(x => x.ArtistMusicBrainzId).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var albumIds = files.Select(x => x.AlbumMusicBrainzId).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var releaseIds = files.Select(x => x.ReleaseMusicBrainzId).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (artistIds.Length != 1 || albumIds.Length != 1 || releaseIds.Length != 1 ||
            !Guid.TryParse(artistIds[0], out var artistId) || !Guid.TryParse(albumIds[0], out var albumId) ||
            !Guid.TryParse(releaseIds[0], out var releaseId) || artistId == Guid.Empty || albumId == Guid.Empty ||
            releaseId == Guid.Empty) return null;
        if (files.Any(x => string.IsNullOrWhiteSpace(x.Artist) || string.IsNullOrWhiteSpace(x.Album))) return null;
        var artists = files.Select(x => x.Artist.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var albums = files.Select(x => x.Album.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (artists.Length != 1 || albums.Length != 1) return null;
        return new LibraryAdditionDto(artistId.ToString(), albumId.ToString(), releaseId.ToString(), artists[0], albums[0]);
    }

    internal static int[] GetCandidateAlbumIds(
        IReadOnlyList<SourceFileCandidate> files,
        int? queueAlbumId) =>
        files.Select(x => x.AlbumId)
            .Append(queueAlbumId)
            .Where(x => x is > 0)
            .Select(x => x!.Value)
            .Distinct()
            .OrderBy(x => x)
            .ToArray();

    private static MatchProposal ToProposal(DeterministicResult result, IReadOnlyList<ReleaseCandidate> candidates)
    {
        if (result.Status == DeterministicStatus.Proposed && result.ReleaseId.HasValue)
        {
            var release = candidates.Single(x => x.Id == result.ReleaseId.Value);
            if (release.ArtistId.HasValue)
            {
                return new MatchProposal(
                    1, ProposalStatus.Proposed, release.ArtistId, release.AlbumId, release.Id,
                    result.Confidence, result.Explanation, result.Tracks);
            }
        }

        return ManualReview(result.Explanation, result.Confidence);
    }

    private static MatchProposal ManualReview(string explanation, decimal confidence = 0m) =>
        new(1, ProposalStatus.ManualReview, null, null, null, confidence, explanation, Array.Empty<TrackProposal>());

    private static MatchProposal PromoteYoloProposal(
        MatchProposal proposal,
        DeterministicResult deterministic,
        IReadOnlyList<SourceFileCandidate> files,
        IReadOnlyList<ReleaseCandidate> candidates,
        AiImportSettings settings)
    {
        if (!settings.YoloMode || proposal.Status != ProposalStatus.ManualReview ||
            deterministic.BestReleaseId is not > 0 ||
            deterministic.BestMappings is not { Count: > 0 } ||
            deterministic.BestMappings.Count != files.Count)
        {
            return proposal;
        }

        var bestRelease = candidates.FirstOrDefault(x => x.Id == deterministic.BestReleaseId.Value);
        if (bestRelease is null || bestRelease.ArtistId is not > 0)
        {
            return proposal;
        }

        var promoted = new MatchProposal(
            1,
            ProposalStatus.Proposed,
            bestRelease.ArtistId,
            bestRelease.AlbumId,
            bestRelease.Id,
            deterministic.Confidence,
            $"YOLO mode: the best verified release was promoted to a proposed mapping (confidence {deterministic.Confidence.ToString("P0", CultureInfo.InvariantCulture)}). Review the mapping, then approve and execute.",
            deterministic.BestMappings);
        try
        {
            return StrictProposalValidator.Validate(promoted, files, candidates);
        }
        catch (ProposalValidationException)
        {
            // YOLO never weakens validation; an unsafe mapping stays in manual review.
            return proposal;
        }
    }

    private void ApplyYoloMode(
        List<AiImportAnalysisDto> analyses,
        AiImportSettings settings,
        List<AiImportDiscoveryDiagnostic> diagnostics)
    {
        if (_yolo is null) return;

        for (var i = 0; i < analyses.Count; i++)
        {
            var analysis = analyses[i];
            var addition = analysis.LibraryAddition;
            if (addition is null) continue;

            var sourceId = string.IsNullOrWhiteSpace(analysis.DownloadId) ? analysis.FolderPath ?? string.Empty : analysis.DownloadId;

            LibraryAdditionCreatedDto created;
            try
            {
                created = _yolo.TryAddTaggedAlbum(addition);
            }
            catch (Exception exception)
            {
                diagnostics.Add(new AiImportDiscoveryDiagnostic(
                    sourceId, 0, 0,
                    $"YOLO mode: automatic library addition failed ({exception.Message}). Use the manual add flow on the analysis card."));
                continue;
            }

            if (created.State != "complete")
            {
                diagnostics.Add(new AiImportDiscoveryDiagnostic(
                    sourceId, 0, 0,
                    $"YOLO mode: {created.Outcome ?? "automatic library addition did not complete"}. Use the manual add flow on the analysis card."));
                continue;
            }

            ManualImportScanResult rescan;
            int? queueAlbumId = null;
            try
            {
                if (analysis.FolderPath is not null)
                {
                    rescan = _gateway.GetUntrackedManualImportScan(analysis.FolderPath);
                }
                else
                {
                    queueAlbumId = _gateway.GetTrackedDownloads()
                        .FirstOrDefault(x =>
                            string.Equals(x.DownloadId, analysis.DownloadId, StringComparison.Ordinal) &&
                            x.State is TrackedImportState.ImportBlocked or TrackedImportState.ImportFailed)?.AlbumId;
                    rescan = _gateway.GetManualImportScan(analysis.DownloadId);
                }
            }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException or InvalidDataException)
            {
                diagnostics.Add(new AiImportDiscoveryDiagnostic(
                    sourceId, 0, 0,
                    $"YOLO mode: library addition completed but the source became unavailable ({exception.Message}). Run Discover again."));
                continue;
            }

            var reanalyzed = AnalyzeScan(
                rescan, analysis.DownloadId, analysis.FolderPath, analysis.SourceKind, analysis.Title,
                queueAlbumId, settings, out var reDiagnostic);
            if (reDiagnostic is not null) diagnostics.Add(reDiagnostic);
            if (reanalyzed is not null) analyses[i] = reanalyzed;
        }
    }

    public static string ComputeAnalysisId(
        string sourceId,
        string sourceKind,
        int? queueAlbumId,
        IReadOnlyList<SourceFileCandidate> files,
        IReadOnlyList<ReleaseCandidate> candidates,
        MatchProposal proposal)
    {
        var snapshot = new
        {
            version = 4,
            sourceId,
            sourceKind,
            queueAlbumId,
            files = files.OrderBy(x => x.Id, StringComparer.Ordinal).Select(x => new
            {
                x.Id, x.Artist, x.Album, x.Title, x.DiscNumber, x.TrackNumbers, x.DurationMs,
                x.TrackMusicBrainzId, x.ArtistMusicBrainzId, x.AlbumMusicBrainzId,
                x.ReleaseMusicBrainzId, x.RecordingMusicBrainzId,
                x.ArtistId, x.AlbumId, x.SourcePath,
                rejections = x.Rejections.OrderBy(value => value, StringComparer.Ordinal)
            }),
            candidates = candidates.OrderBy(x => x.Id),
            proposal
        };
        var hash = SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(snapshot));
        return Convert.ToHexString(hash).ToLowerInvariant()[..24];
    }
}
