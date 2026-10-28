using System.Diagnostics;
using System.Text.Json;

namespace Lidarr.AIImport.Companion.Filesystem;

public sealed record FileIdentity(
    ulong Device,
    ulong Inode,
    long Size,
    long MtimeSeconds,
    long MtimeNanoseconds,
    long CtimeSeconds,
    long CtimeNanoseconds,
    uint Mode,
    uint Uid,
    uint Gid,
    ulong Links);

public interface IFileIdentityProvider
{
    Task<FileIdentity> InspectAsync(string path, CancellationToken cancellationToken);
}

public sealed class NativeFileIdentityProvider : IFileIdentityProvider
{
    private static readonly HashSet<string> RequiredProperties = new(StringComparer.Ordinal)
    {
        "device", "inode", "size", "mtimeSeconds", "mtimeNanoseconds", "ctimeSeconds", "ctimeNanoseconds", "mode", "uid", "gid", "links"
    };
    private readonly string _helperPath;
    private readonly TimeSpan _timeout;
    private readonly NativeHelperAttestor _attestor;

    public NativeFileIdentityProvider(string helperPath, TimeSpan timeout, NativeHelperAttestor? attestor = null)
    {
        if (!Path.IsPathFullyQualified(helperPath)) throw new ArgumentException("Native helper path must be absolute.", nameof(helperPath));
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(1)) throw new ArgumentOutOfRangeException(nameof(timeout));
        _helperPath = helperPath;
        _timeout = timeout;
        _attestor = attestor ?? new NativeHelperAttestor(helperPath);
    }

    public async Task<FileIdentity> InspectAsync(string path, CancellationToken cancellationToken)
    {
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("Inspection path must be absolute.", nameof(path));
        var start = new ProcessStartInfo
        {
            FileName = _helperPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("--inspect");
        start.ArgumentList.Add(path);
        var result = await _attestor.RunAsync(
            start, ReadOnlyMemory<byte>.Empty, 4096, _timeout, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw new InvalidOperationException(
                $"Native identity lookup failed with exit code {result.ExitCode}: {result.StandardError.Trim()}");
        return Parse(result.StandardOutput);
    }

    public static FileIdentity Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow
            });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("File identity must be an object.");
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
            {
                if (!RequiredProperties.Contains(property.Name) || !names.Add(property.Name))
                    throw new InvalidDataException("File identity contains an unknown or duplicate property.");
            }
            if (!names.SetEquals(RequiredProperties)) throw new InvalidDataException("File identity is incomplete.");
            var identity = new FileIdentity(
                Unsigned64(root, "device"),
                Unsigned64(root, "inode"),
                Signed64(root, "size"),
                Signed64(root, "mtimeSeconds"),
                Signed64(root, "mtimeNanoseconds"),
                Signed64(root, "ctimeSeconds"),
                Signed64(root, "ctimeNanoseconds"),
                Unsigned32(root, "mode"),
                Unsigned32(root, "uid"),
                Unsigned32(root, "gid"),
                Unsigned64(root, "links"));
            if (identity.Inode == 0 || identity.Size < 0 ||
                identity.MtimeNanoseconds is < 0 or > 999_999_999 ||
                identity.CtimeNanoseconds is < 0 or > 999_999_999 || identity.Links == 0)
                throw new InvalidDataException("File identity contains invalid values.");
            return identity;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("File identity is malformed JSON.", exception);
        }
    }

    private static ulong Unsigned64(JsonElement root, string name) =>
        root.GetProperty(name).ValueKind == JsonValueKind.Number && root.GetProperty(name).TryGetUInt64(out var value)
            ? value : throw new InvalidDataException($"File identity {name} is invalid.");
    private static long Signed64(JsonElement root, string name) =>
        root.GetProperty(name).ValueKind == JsonValueKind.Number && root.GetProperty(name).TryGetInt64(out var value)
            ? value : throw new InvalidDataException($"File identity {name} is invalid.");
    private static uint Unsigned32(JsonElement root, string name) =>
        root.GetProperty(name).ValueKind == JsonValueKind.Number && root.GetProperty(name).TryGetUInt32(out var value)
            ? value : throw new InvalidDataException($"File identity {name} is invalid.");
}
