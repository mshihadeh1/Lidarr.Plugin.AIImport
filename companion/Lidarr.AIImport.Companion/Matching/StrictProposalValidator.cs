using System.Text.Json;
using Lidarr.AIImport.Companion.Models;

namespace Lidarr.AIImport.Companion.Matching;

public sealed class ProposalValidationException : Exception
{
    public ProposalValidationException(string message) : base(message) { }
}

public static class StrictProposalValidator
{
    private static readonly HashSet<string> RootFields = new(StringComparer.Ordinal)
    {
        "schemaVersion", "status", "artistId", "albumId", "releaseId", "confidence", "explanation", "tracks"
    };
    private static readonly HashSet<string> TrackFields = new(StringComparer.Ordinal)
    {
        "sourceFileId", "trackId", "confidence"
    };

    public static MatchProposal ParseAndValidate(
        string output,
        IReadOnlyList<SourceFileCandidate> files,
        IReadOnlyList<ReleaseCandidate> releases)
    {
        try
        {
            var json = RemoveFence(output);
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            RequireObject(root, "root");
            RequireExactFields(root, RootFields, "root");
            if (RequireInt(root, "schemaVersion") != 1) throw new ProposalValidationException("schemaVersion must be 1.");
            var statusText = RequireString(root, "status");
            var status = statusText switch
            {
                "proposed" => ProposalStatus.Proposed,
                "manualReview" => ProposalStatus.ManualReview,
                _ => throw new ProposalValidationException("Unknown status.")
            };
            var artistId = OptionalPositiveInt(root, "artistId");
            var albumId = OptionalPositiveInt(root, "albumId");
            var releaseId = OptionalPositiveInt(root, "releaseId");
            var confidence = Confidence(root, "confidence");
            var explanation = RequireString(root, "explanation");
            if (string.IsNullOrWhiteSpace(explanation)) throw new ProposalValidationException("explanation is required.");

            var tracksElement = root.GetProperty("tracks");
            if (tracksElement.ValueKind != JsonValueKind.Array) throw new ProposalValidationException("tracks must be an array.");
            var tracks = new List<TrackProposal>();
            var sources = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in tracksElement.EnumerateArray())
            {
                RequireObject(item, "track");
                RequireExactFields(item, TrackFields, "track");
                var sourceId = RequireString(item, "sourceFileId");
                if (!sources.Add(sourceId)) throw new ProposalValidationException("sourceFileId values must be unique.");
                tracks.Add(new TrackProposal(sourceId, RequirePositiveInt(item, "trackId"), Confidence(item, "confidence")));
            }

            return Validate(new MatchProposal(1, status, artistId, albumId, releaseId, confidence, explanation, tracks), files, releases);
        }
        catch (ProposalValidationException) { throw; }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            throw new ProposalValidationException("The local model returned invalid proposal JSON.");
        }
    }

    public static MatchProposal Validate(
        MatchProposal proposal,
        IReadOnlyList<SourceFileCandidate> files,
        IReadOnlyList<ReleaseCandidate> releases)
    {
        if (proposal.SchemaVersion != 1) throw new ProposalValidationException("schemaVersion must be 1.");
        if (proposal.Confidence is < 0m or > 1m) throw new ProposalValidationException("confidence must be between 0 and 1.");
        if (string.IsNullOrWhiteSpace(proposal.Explanation)) throw new ProposalValidationException("explanation is required.");
        if (proposal.Status == ProposalStatus.ManualReview)
        {
            if (proposal.ArtistId is not null || proposal.AlbumId is not null || proposal.ReleaseId is not null || proposal.Tracks.Count != 0)
                throw new ProposalValidationException("manualReview cannot contain identifiers or track mappings.");
            return proposal;
        }

        if (proposal.ArtistId is null or <= 0 || proposal.AlbumId is null or <= 0 || proposal.ReleaseId is null or <= 0 || proposal.Tracks.Count == 0)
            throw new ProposalValidationException("proposed requires positive identifiers and mappings.");
        var release = releases.SingleOrDefault(x => x.Id == proposal.ReleaseId.Value)
            ?? throw new ProposalValidationException("releaseId was not supplied as a verified candidate.");
        if (release.AlbumId != proposal.AlbumId.Value) throw new ProposalValidationException("albumId does not own the selected release.");
        if (!release.ArtistId.HasValue || release.ArtistId.Value != proposal.ArtistId.Value) throw new ProposalValidationException("artistId was not verified as the owner of the selected release.");
        if (files.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != files.Count)
            throw new ProposalValidationException("Supplied sourceFileId values must be unique.");
        var sources = proposal.Tracks.Select(x => x.SourceFileId).ToArray();
        if (sources.Any(string.IsNullOrWhiteSpace) || sources.Distinct(StringComparer.Ordinal).Count() != sources.Length)
            throw new ProposalValidationException("sourceFileId values must be non-empty and unique.");
        var validSources = files.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
        if (!sources.ToHashSet(StringComparer.Ordinal).SetEquals(validSources)) throw new ProposalValidationException("Every and only supplied sourceFileId must be mapped.");
        if (proposal.Tracks.Any(x => x.TrackId <= 0 || x.Confidence is < 0m or > 1m))
            throw new ProposalValidationException("Track identifiers and confidence values are invalid.");
        var validTracks = release.Tracks.Where(x => !x.HasFile && x.Id > 0).Select(x => x.Id).ToHashSet();
        if (proposal.Tracks.Any(x => !validTracks.Contains(x.TrackId)) || proposal.Tracks.Select(x => x.TrackId).Distinct().Count() != proposal.Tracks.Count)
            throw new ProposalValidationException("A trackId was not supplied for the selected release, already has a file, or was duplicated.");
        return proposal;
    }

    private static string RemoveFence(string value)
    {
        var trimmed = value.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal)) return trimmed;
        var firstNewline = trimmed.IndexOf('\n');
        var closing = trimmed.LastIndexOf("```", StringComparison.Ordinal);
        if (firstNewline < 0 || closing <= firstNewline || closing + 3 != trimmed.Length)
            throw new ProposalValidationException("Malformed JSON fence.");
        return trimmed[(firstNewline + 1)..closing].Trim();
    }

    private static void RequireExactFields(JsonElement element, HashSet<string> required, string name)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!required.Contains(property.Name)) throw new ProposalValidationException($"Unknown {name} field: {property.Name}.");
            if (!seen.Add(property.Name)) throw new ProposalValidationException($"Duplicate {name} field: {property.Name}.");
        }
        if (!seen.SetEquals(required)) throw new ProposalValidationException($"Missing required {name} fields.");
    }

    private static void RequireObject(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new ProposalValidationException($"{name} must be an object.");
    }
    private static string RequireString(JsonElement element, string name) => element.GetProperty(name).ValueKind == JsonValueKind.String ? element.GetProperty(name).GetString()! : throw new ProposalValidationException($"{name} must be a string.");
    private static int RequireInt(JsonElement element, string name) => element.GetProperty(name).TryGetInt32(out var value) ? value : throw new ProposalValidationException($"{name} must be an integer.");
    private static int RequirePositiveInt(JsonElement element, string name) { var value = RequireInt(element, name); return value > 0 ? value : throw new ProposalValidationException($"{name} must be positive."); }
    private static int? OptionalPositiveInt(JsonElement element, string name) => element.GetProperty(name).ValueKind == JsonValueKind.Null ? null : RequirePositiveInt(element, name);
    private static decimal Confidence(JsonElement element, string name)
    {
        if (!element.GetProperty(name).TryGetDecimal(out var value) || value is < 0m or > 1m) throw new ProposalValidationException($"{name} must be between 0 and 1.");
        return value;
    }
}
