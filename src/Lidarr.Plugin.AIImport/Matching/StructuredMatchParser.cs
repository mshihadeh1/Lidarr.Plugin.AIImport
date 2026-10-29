using System.Text.Json;

namespace Lidarr.Plugin.AIImport.Matching;

public static class StructuredMatchParser
{
    private static readonly HashSet<string> RootProperties = new(StringComparer.Ordinal)
    {
        "schemaVersion", "status", "artistId", "albumId", "releaseId", "confidence", "explanation", "tracks"
    };

    private static readonly HashSet<string> TrackProperties = new(StringComparer.Ordinal)
    {
        "sourceFileId", "trackId", "confidence"
    };

    public static MatchProposal ParseAndValidate(string modelOutput)
    {
        try
        {
            var json = RemoveMarkdownFence(modelOutput);
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            RequireObject(root, "root");
            RejectUnknownProperties(root, RootProperties, "root");
            RequireExactProperties(root, RootProperties, "root");

            var schemaVersion = RequireInt(root, "schemaVersion");
            if (schemaVersion != 1)
            {
                throw new MatchSchemaException("schemaVersion must be 1.");
            }

            var status = RequireString(root, "status") switch
            {
                "proposed" => ProposalStatus.Proposed,
                "manualReview" => ProposalStatus.ManualReview,
                _ => throw new MatchSchemaException("status must be proposed or manualReview.")
            };

            var artistId = OptionalPositiveInt(root, "artistId");
            var albumId = OptionalPositiveInt(root, "albumId");
            var releaseId = OptionalPositiveInt(root, "releaseId");
            var confidence = RequireConfidence(root, "confidence");
            var explanation = RequireString(root, "explanation");
            if (string.IsNullOrWhiteSpace(explanation))
            {
                throw new MatchSchemaException("explanation cannot be empty.");
            }

            var tracksElement = root.GetProperty("tracks");
            if (tracksElement.ValueKind != JsonValueKind.Array)
            {
                throw new MatchSchemaException("tracks must be an array.");
            }

            var tracks = new List<TrackProposal>();
            var sourceIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var track in tracksElement.EnumerateArray())
            {
                RequireObject(track, "track");
                RejectUnknownProperties(track, TrackProperties, "track");
                RequireExactProperties(track, TrackProperties, "track");
                var sourceFileId = RequireString(track, "sourceFileId");
                if (string.IsNullOrWhiteSpace(sourceFileId) || !sourceIds.Add(sourceFileId))
                {
                    throw new MatchSchemaException("Each sourceFileId must be non-empty and unique.");
                }

                tracks.Add(new TrackProposal(sourceFileId, RequirePositiveInt(track, "trackId"), RequireConfidence(track, "confidence")));
            }

            if (status == ProposalStatus.Proposed && (artistId is null || albumId is null || releaseId is null || tracks.Count == 0))
            {
                throw new MatchSchemaException("A proposed match requires verified candidate IDs and at least one track.");
            }

            if (status == ProposalStatus.ManualReview && tracks.Count > 0)
            {
                throw new MatchSchemaException("A manualReview response cannot contain track mappings.");
            }

            return new MatchProposal(schemaVersion, status, artistId, albumId, releaseId, confidence, explanation, tracks);
        }
        catch (MatchSchemaException)
        {
            throw;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            throw new MatchSchemaException("The model output is not valid match-proposal JSON.");
        }
    }

    private static string RemoveMarkdownFence(string value)
    {
        var trimmed = value.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            return trimmed;
        }

        var firstNewline = trimmed.IndexOf('\n');
        var closingFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
        if (firstNewline < 0 || closingFence <= firstNewline || closingFence + 3 != trimmed.Length)
        {
            throw new MatchSchemaException("Malformed markdown JSON fence.");
        }

        return trimmed[(firstNewline + 1)..closingFence].Trim();
    }

    private static void RequireObject(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new MatchSchemaException($"{name} must be an object.");
        }
    }

    private static void RejectUnknownProperties(JsonElement element, HashSet<string> allowed, string name)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!allowed.Contains(property.Name))
            {
                throw new MatchSchemaException($"Unknown {name} property: {property.Name}.");
            }

            if (!seen.Add(property.Name))
            {
                throw new MatchSchemaException($"Duplicate {name} property: {property.Name}.");
            }
        }
    }

    private static void RequireExactProperties(JsonElement element, HashSet<string> required, string name)
    {
        foreach (var property in required)
        {
            if (!element.TryGetProperty(property, out _))
            {
                throw new MatchSchemaException($"Missing {name} property: {property}.");
            }
        }
    }

    private static string RequireString(JsonElement element, string property)
    {
        var value = element.GetProperty(property);
        return value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw new MatchSchemaException($"{property} must be a string.");
    }

    private static int RequireInt(JsonElement element, string property)
    {
        var value = element.GetProperty(property);
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var result)
            ? result
            : throw new MatchSchemaException($"{property} must be an integer.");
    }

    private static int RequirePositiveInt(JsonElement element, string property)
    {
        var value = RequireInt(element, property);
        return value > 0 ? value : throw new MatchSchemaException($"{property} must be positive.");
    }

    private static int? OptionalPositiveInt(JsonElement element, string property)
    {
        var value = element.GetProperty(property);
        return value.ValueKind == JsonValueKind.Null ? null : RequirePositiveInt(element, property);
    }

    private static decimal RequireConfidence(JsonElement element, string property)
    {
        var value = element.GetProperty(property);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDecimal(out var confidence) || confidence < 0m || confidence > 1m)
        {
            throw new MatchSchemaException($"{property} must be between 0 and 1.");
        }

        return confidence;
    }
}
