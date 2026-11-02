using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

try
{
    return Verify(args);
}
catch (Exception ex)
{
    Console.Error.WriteLine("Assembly verification failed: " + ex.Message);
    return 1;
}

static int Verify(string[] args)
{
    if (args.Length != 4)
    {
        Console.Error.WriteLine("usage: AssemblyReferenceVerifier <dll> <informational-version> <assembly-version> <lidarr-version>");
        return 2;
    }

    var expectedInformational = args[1];
    var expectedPlugin = Version.Parse(args[2]);
    var expectedLidarr = Version.Parse(args[3]);
    using var stream = File.OpenRead(args[0]);
    using var pe = new PEReader(stream);
    if (!pe.HasMetadata)
        throw new InvalidDataException("Plugin DLL has no managed metadata.");
    var metadata = pe.GetMetadataReader();
    var definition = metadata.GetAssemblyDefinition();
    if (definition.Version != expectedPlugin)
        throw new InvalidDataException($"Plugin assembly version is {definition.Version}, expected {expectedPlugin}.");
    var versionInfo = FileVersionInfo.GetVersionInfo(args[0]);
    if (!string.Equals(versionInfo.FileVersion, expectedPlugin.ToString(), StringComparison.Ordinal) ||
        !string.Equals(versionInfo.ProductVersion, expectedInformational, StringComparison.Ordinal))
        throw new InvalidDataException(
            $"Plugin file/informational versions are {versionInfo.FileVersion}/{versionInfo.ProductVersion}, expected {expectedPlugin}/{expectedInformational}.");

    var required = new HashSet<string>(StringComparer.Ordinal)
    {
        "Lidarr.Common", "Lidarr.Core", "Lidarr.Http"
    };
    foreach (var handle in metadata.AssemblyReferences)
    {
        var reference = metadata.GetAssemblyReference(handle);
        var name = metadata.GetString(reference.Name);
        if (!required.Remove(name)) continue;
        if (reference.Version != expectedLidarr)
            throw new InvalidDataException($"{name} reference is {reference.Version}, expected {expectedLidarr}.");
    }
    if (required.Count != 0)
        throw new InvalidDataException("Plugin is missing required Lidarr references: " + string.Join(", ", required));

    Console.WriteLine($"Verified plugin {expectedInformational} / assembly {expectedPlugin} and Lidarr references {expectedLidarr}.");
    return 0;
}
