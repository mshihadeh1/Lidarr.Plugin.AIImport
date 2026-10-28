using System.Security.Cryptography;
using System.Text.Json;
using Lidarr.AIImport.Companion.Configuration;
using Lidarr.AIImport.Companion.Lidarr;

namespace Lidarr.AIImport.Companion.Safety;

public sealed record ImportSafetySnapshot(string Fingerprint, string NativeHelperSha256);

public interface IImportSafetySnapshotProvider
{
    Task<ImportSafetySnapshot> CaptureAsync(CancellationToken cancellationToken);
}

public sealed class ImportSafetySnapshotProvider : IImportSafetySnapshotProvider
{
    private readonly ILidarrClient _lidarr;
    private readonly ImportSafetyService _safety;
    private readonly CompanionOptions _options;

    public ImportSafetySnapshotProvider(
        ILidarrClient lidarr,
        ImportSafetyService safety,
        CompanionOptions options)
    {
        _lidarr = lidarr;
        _safety = safety;
        _options = options;
    }

    public async Task<ImportSafetySnapshot> CaptureAsync(CancellationToken cancellationToken)
    {
        var status = await _lidarr.GetSystemStatusAsync(cancellationToken).ConfigureAwait(false);
        var configuration = await _lidarr.GetImportSafetyConfigurationAsync(cancellationToken).ConfigureAwait(false);
        var assessment = _safety.Assess(status, configuration);
        if (!assessment.ConfigurationPrerequisitesSatisfied)
            throw new InvalidOperationException("Import safety prerequisites are not satisfied: " + string.Join(" ", assessment.Blockers));
        var helper = new FileInfo(_options.NativeHelperPath);
        if (!helper.Exists || helper.LinkTarget is not null)
            throw new InvalidOperationException("The companion native helper is missing or is a symbolic link.");
        string helperHash;
        await using (var stream = new FileStream(helper.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            helperHash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
        }
        var canonical = JsonSerializer.SerializeToUtf8Bytes(new
        {
            SchemaVersion = 1,
            status.Version,
            status.PackageVersion,
            status.Branch,
            Configuration = configuration,
            ExpectedScriptPath = _options.HardlinkScriptPath,
            NativeHelperSha256 = helperHash
        });
        var fingerprint = Convert.ToHexString(SHA256.HashData(canonical)).ToLowerInvariant();
        return new ImportSafetySnapshot(fingerprint, helperHash);
    }
}
