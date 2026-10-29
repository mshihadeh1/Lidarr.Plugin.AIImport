using System.Diagnostics;
using System.Text.Json;
using Lidarr.Plugin.AIImport.Filesystem;
using Lidarr.Plugin.AIImport.Workflows;
using NzbDrone.Common.EnvironmentInfo;

namespace Lidarr.Plugin.AIImport.Authorization;

public enum TransferResultOutcome
{
    Linked = 1,
    Reconciled = 2
}

public sealed record VerifiedTransferResult(
    string AuthorizationId,
    TransferResultOutcome Outcome,
    ulong SourceDevice,
    ulong SourceInode);

public interface ITransferResultVerifier
{
    Task<VerifiedTransferResult> VerifyAsync(
        string authorizationId,
        string destinationPath,
        CancellationToken cancellationToken);
}

public sealed class NativeTransferResultVerifier : ITransferResultVerifier
{
    private static readonly HashSet<string> RequiredProperties = new(StringComparer.Ordinal)
    {
        "authorizationId", "outcome", "sourceDevice", "sourceInode"
    };

    private readonly string _helperPath;
    private readonly string _authorizationDirectory;
    private readonly TimeSpan _timeout;
    private NativeHelperAttestor? _attestor;

    public NativeTransferResultVerifier(IAppFolderInfo appFolderInfo)
        : this(
            SealedNativeHelper.ExecutionPath,
            new StandaloneStatePaths(appFolderInfo).AuthorizationDirectory,
            TimeSpan.FromSeconds(10))
    {
        new StandaloneStatePaths(appFolderInfo).ConfigureAuthorizationEnvironment();
    }

    public NativeTransferResultVerifier(
        string helperPath,
        string authorizationDirectory,
        TimeSpan timeout,
        NativeHelperAttestor? attestor = null)
    {
        if (!Path.IsPathFullyQualified(helperPath))
            throw new ArgumentException("Native helper path must be absolute.", nameof(helperPath));
        if (!Path.IsPathFullyQualified(authorizationDirectory))
            throw new ArgumentException("Authorization directory must be absolute.", nameof(authorizationDirectory));
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(1))
            throw new ArgumentOutOfRangeException(nameof(timeout));
        _helperPath = helperPath;
        _authorizationDirectory = authorizationDirectory;
        _timeout = timeout;
        _attestor = attestor;
    }

    public async Task<VerifiedTransferResult> VerifyAsync(
        string authorizationId,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        ValidateAuthorizationId(authorizationId);
        if (!Path.IsPathFullyQualified(destinationPath))
            throw new ArgumentException("Destination path must be absolute.", nameof(destinationPath));
        var start = new ProcessStartInfo
        {
            FileName = _helperPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("--verify-result");
        start.ArgumentList.Add(_authorizationDirectory);
        start.ArgumentList.Add(authorizationId);
        start.ArgumentList.Add(destinationPath);
        var result = await (_attestor ??= new NativeHelperAttestor(_helperPath)).RunAsync(
            start, ReadOnlyMemory<byte>.Empty, 4096, _timeout, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw new InvalidOperationException(
                $"Native transfer-result verification failed with exit code {result.ExitCode}: {result.StandardError.Trim()}");
        return Parse(result.StandardOutput, authorizationId);
    }

    public static VerifiedTransferResult Parse(string json, string expectedAuthorizationId)
    {
        ValidateAuthorizationId(expectedAuthorizationId);
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow
            });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Transfer result must be an object.");
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
            {
                if (!RequiredProperties.Contains(property.Name) || !names.Add(property.Name))
                    throw new InvalidDataException("Transfer result contains an unknown or duplicate property.");
            }
            if (!names.SetEquals(RequiredProperties))
                throw new InvalidDataException("Transfer result is incomplete.");
            var authorizationId = RequiredString(root, "authorizationId");
            if (!string.Equals(authorizationId, expectedAuthorizationId, StringComparison.Ordinal))
                throw new InvalidDataException("Transfer result authorization ID does not match the expected ID.");
            var outcomeValue = RequiredInt(root, "outcome");
            if (outcomeValue is not ((int)TransferResultOutcome.Linked) and not ((int)TransferResultOutcome.Reconciled))
                throw new InvalidDataException("Transfer result is not successful.");
            var inode = RequiredUnsigned64(root, "sourceInode");
            if (inode == 0) throw new InvalidDataException("Transfer result source inode is invalid.");
            return new VerifiedTransferResult(
                authorizationId,
                (TransferResultOutcome)outcomeValue,
                RequiredUnsigned64(root, "sourceDevice"),
                inode);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Transfer result is malformed JSON.", exception);
        }
    }

    private static void ValidateAuthorizationId(string authorizationId)
    {
        if (authorizationId.Length != 64 || authorizationId.Any(character =>
                character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new ArgumentException(
                "Authorization ID must be 64 lowercase hexadecimal characters.",
                nameof(authorizationId));
    }

    private static string RequiredString(JsonElement root, string name) =>
        root.GetProperty(name).ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(root.GetProperty(name).GetString())
            ? root.GetProperty(name).GetString()!
            : throw new InvalidDataException($"Transfer result {name} is invalid.");

    private static int RequiredInt(JsonElement root, string name) =>
        root.GetProperty(name).ValueKind == JsonValueKind.Number && root.GetProperty(name).TryGetInt32(out var value)
            ? value
            : throw new InvalidDataException($"Transfer result {name} is invalid.");

    private static ulong RequiredUnsigned64(JsonElement root, string name) =>
        root.GetProperty(name).ValueKind == JsonValueKind.Number && root.GetProperty(name).TryGetUInt64(out var value)
            ? value
            : throw new InvalidDataException($"Transfer result {name} is invalid.");
}
