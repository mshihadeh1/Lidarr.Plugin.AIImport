using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Lidarr.AIImport.Companion.Models;

namespace Lidarr.AIImport.Companion.Matching;

public enum DeterministicStatus
{
    Proposed,
    NeedsLlm,
    ManualReview
}

public sealed record DeterministicResult(
    DeterministicStatus Status,
    int? ReleaseId,
    decimal Confidence,
    IReadOnlyList<TrackProposal> Tracks,
    string Explanation,
    IReadOnlyList<int> RankedReleaseIds);

public sealed class DeterministicMatcher
{
    private const decimal AutomaticThreshold = 0.92m;
    private const decimal MinimumTrackScore = 0.72m;
    private const decimal RequiredMargin = 0.08m;

    public DeterministicResult Match(IReadOnlyList<SourceFileCandidate> files, IReadOnlyList<ReleaseCandidate> releases)
    {
        if (files.Count == 0 || releases.Count == 0)
        {
            return Manual("No source files or verified Lidarr releases were available.");
        }

        var scored = releases.Select(release => ScoreRelease(files, release))
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Release.Id)
            .ToArray();
        var best = scored[0];
        if (best.Mappings.Count != files.Count || best.Score < 0.55m)
        {
            return Manual("Verified candidates could not map every source file without using an existing library track.", scored.Select(x => x.Release.Id));
        }

        var margin = scored.Length > 1 ? best.Score - scored[1].Score : 1m;
        if (best.Score >= AutomaticThreshold && margin >= RequiredMargin)
        {
            return new DeterministicResult(
                DeterministicStatus.Proposed,
                best.Release.Id,
                best.Score,
                best.Mappings.Select(x => new TrackProposal(x.File.Id, x.Track.Id, x.Score)).ToArray(),
                "Deterministic metadata, numbering, title, and duration checks selected one verified Lidarr release.",
                scored.Select(x => x.Release.Id).ToArray());
        }

        return new DeterministicResult(
            DeterministicStatus.NeedsLlm,
            null,
            best.Score,
            Array.Empty<TrackProposal>(),
            "Multiple verified releases remain plausible; local-model ranking is required.",
            scored.Where(x => x.Score >= Math.Max(0.5m, best.Score - 0.20m)).Take(5).Select(x => x.Release.Id).ToArray());
    }

    private static ScoredRelease ScoreRelease(IReadOnlyList<SourceFileCandidate> files, ReleaseCandidate release)
    {
        if (files.Any(file => file.AlbumId.HasValue && file.AlbumId.Value != release.AlbumId) ||
            files.Any(file => file.ArtistId.HasValue && file.ArtistId.Value != release.ArtistId))
        {
            return new ScoredRelease(release, 0m, Array.Empty<(SourceFileCandidate, TrackCandidate, decimal)>());
        }

        var available = release.Tracks.Where(x => !x.HasFile).OrderBy(x => x.Id).ToArray();
        var mappings = OptimalMappings(files.OrderBy(x => x.Id, StringComparer.Ordinal).ToArray(), available);

        if (mappings.Count == 0)
        {
            return new ScoredRelease(release, 0m, mappings);
        }

        var average = mappings.Average(x => x.Score);
        var sourceCoverage = (decimal)mappings.Count / files.Count;
        var albumSimilarities = MetadataSimilarities(files.Select(x => x.Album), release.AlbumTitle);
        var artistSimilarities = MetadataSimilarities(files.Select(x => x.Artist), release.Artist);
        var albumSimilarity = albumSimilarities.Length == 0 ? 0m : albumSimilarities.Average();
        var artistSimilarity = artistSimilarities.Length == 0 ? 0m : artistSimilarities.Average();
        var releaseScore = decimal.Clamp((average * 0.80m + albumSimilarity * 0.12m + artistSimilarity * 0.08m) * sourceCoverage, 0m, 1m);
        if (albumSimilarities.Any(x => x < 0.80m) || artistSimilarities.Any(x => x < 0.80m))
        {
            releaseScore = Math.Min(releaseScore, AutomaticThreshold - 0.01m);
        }
        return new ScoredRelease(release, releaseScore, mappings);
    }

    private static decimal[] MetadataSimilarities(IEnumerable<string> sourceValues, string candidate) =>
        sourceValues.Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .Select(x => Similarity(x, candidate))
            .ToArray();

    private static IReadOnlyList<(SourceFileCandidate File, TrackCandidate Track, decimal Score)> OptimalMappings(
        IReadOnlyList<SourceFileCandidate> files,
        IReadOnlyList<TrackCandidate> tracks)
    {
        if (files.Count == 0 || tracks.Count == 0) return Array.Empty<(SourceFileCandidate, TrackCandidate, decimal)>();

        // Hungarian assignment with expensive dummy/invalid edges: maximize valid
        // mapping count first, then maximize the total deterministic score.
        var rows = files.Count;
        var columns = Math.Max(files.Count, tracks.Count);
        var costs = new decimal[rows + 1, columns + 1];
        var scores = new decimal[rows, tracks.Count];
        for (var i = 1; i <= rows; i++)
        {
            for (var j = 1; j <= columns; j++)
            {
                if (j <= tracks.Count)
                {
                    var trackScore = ScoreTrack(files[i - 1], tracks[j - 1]);
                    scores[i - 1, j - 1] = trackScore;
                    costs[i, j] = trackScore >= MinimumTrackScore ? -trackScore : 1000m;
                }
                else
                {
                    costs[i, j] = 1000m;
                }
            }
        }

        var rowPotential = new decimal[rows + 1];
        var columnPotential = new decimal[columns + 1];
        var matchedRow = new int[columns + 1];
        var previousColumn = new int[columns + 1];
        for (var row = 1; row <= rows; row++)
        {
            matchedRow[0] = row;
            var currentColumn = 0;
            var minimum = Enumerable.Repeat(decimal.MaxValue, columns + 1).ToArray();
            var used = new bool[columns + 1];
            do
            {
                used[currentColumn] = true;
                var currentRow = matchedRow[currentColumn];
                var delta = decimal.MaxValue;
                var nextColumn = 0;
                for (var column = 1; column <= columns; column++)
                {
                    if (used[column]) continue;
                    var reducedCost = costs[currentRow, column] - rowPotential[currentRow] - columnPotential[column];
                    if (reducedCost < minimum[column])
                    {
                        minimum[column] = reducedCost;
                        previousColumn[column] = currentColumn;
                    }
                    if (minimum[column] < delta)
                    {
                        delta = minimum[column];
                        nextColumn = column;
                    }
                }
                for (var column = 0; column <= columns; column++)
                {
                    if (used[column])
                    {
                        rowPotential[matchedRow[column]] += delta;
                        columnPotential[column] -= delta;
                    }
                    else
                    {
                        minimum[column] -= delta;
                    }
                }
                currentColumn = nextColumn;
            }
            while (matchedRow[currentColumn] != 0);

            do
            {
                var previous = previousColumn[currentColumn];
                matchedRow[currentColumn] = matchedRow[previous];
                currentColumn = previous;
            }
            while (currentColumn != 0);
        }

        var mappings = new List<(SourceFileCandidate File, TrackCandidate Track, decimal Score)>();
        for (var column = 1; column <= tracks.Count; column++)
        {
            var row = matchedRow[column];
            if (row > 0 && scores[row - 1, column - 1] >= MinimumTrackScore)
            {
                mappings.Add((files[row - 1], tracks[column - 1], scores[row - 1, column - 1]));
            }
        }
        return mappings.OrderBy(x => x.File.Id, StringComparer.Ordinal).ToArray();
    }

    private static decimal ScoreTrack(SourceFileCandidate file, TrackCandidate track)
    {
        if (!string.IsNullOrWhiteSpace(file.TrackMusicBrainzId) &&
            string.Equals(file.TrackMusicBrainzId, track.ForeignTrackId, StringComparison.OrdinalIgnoreCase))
        {
            return 1m;
        }

        var score = 0m;
        var candidateNumber = ParseTrackNumber(track.TrackNumber);
        if (file.TrackNumbers.Contains(candidateNumber) && candidateNumber > 0) score += 0.42m;
        if (file.DiscNumber <= 0 || track.DiscNumber <= 0 || file.DiscNumber == track.DiscNumber) score += 0.13m;
        score += Similarity(file.Title, track.Title) * 0.35m;
        if (file.DurationMs > 0 && track.DurationMs > 0)
        {
            var delta = Math.Abs(file.DurationMs - track.DurationMs);
            score += delta <= 3_000 ? 0.10m : delta <= 10_000 ? 0.04m : 0m;
        }
        return decimal.Clamp(score, 0m, 1m);
    }

    private static int ParseTrackNumber(string value)
    {
        var digits = new string(value.TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, out var result) ? result : 0;
    }

    internal static decimal Similarity(string left, string right)
    {
        var a = Tokens(left);
        var b = Tokens(right);
        if (a.Count == 0 || b.Count == 0) return 0m;
        if (a.SetEquals(b)) return 1m;
        var intersection = a.Intersect(b).Count();
        var union = a.Union(b).Count();
        return union == 0 ? 0m : (decimal)intersection / union;
    }

    private static HashSet<string> Tokens(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);
        foreach (var character in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark) continue;
            builder.Append(char.IsLetterOrDigit(character) ? char.ToLowerInvariant(character) : ' ');
        }
        var editionNoise = new HashSet<string>(new[] { "deluxe", "expanded", "edition", "remastered", "remaster" }, StringComparer.Ordinal);
        return Regex.Split(builder.ToString(), "\\s+").Where(x => x.Length > 0 && !editionNoise.Contains(x)).ToHashSet(StringComparer.Ordinal);
    }

    private static DeterministicResult Manual(string explanation, IEnumerable<int>? ranked = null) =>
        new(DeterministicStatus.ManualReview, null, 0m, Array.Empty<TrackProposal>(), explanation, ranked?.ToArray() ?? Array.Empty<int>());

    private sealed record ScoredRelease(
        ReleaseCandidate Release,
        decimal Score,
        IReadOnlyList<(SourceFileCandidate File, TrackCandidate Track, decimal Score)> Mappings);
}
