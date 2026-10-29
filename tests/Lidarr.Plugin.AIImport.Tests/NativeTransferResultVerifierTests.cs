using Lidarr.Plugin.AIImport.Authorization;

namespace Lidarr.Plugin.AIImport.Tests;

[TestFixture]
public sealed class NativeTransferResultVerifierTests
{
    [Test]
    public void Parses_exact_verified_result()
    {
        const string id = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        var result = NativeTransferResultVerifier.Parse(
            $"{{\"authorizationId\":\"{id}\",\"outcome\":1,\"sourceDevice\":7,\"sourceInode\":9}}",
            id);

        Assert.Multiple(() =>
        {
            Assert.That(result.AuthorizationId, Is.EqualTo(id));
            Assert.That(result.Outcome, Is.EqualTo(TransferResultOutcome.Linked));
            Assert.That(result.SourceDevice, Is.EqualTo(7));
            Assert.That(result.SourceInode, Is.EqualTo(9));
        });
    }

    [TestCase("{\"authorizationId\":\"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef\",\"outcome\":3,\"sourceDevice\":7,\"sourceInode\":9}")]
    [TestCase("{\"authorizationId\":\"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef\",\"outcome\":1,\"sourceDevice\":7,\"sourceInode\":9,\"extra\":1}")]
    [TestCase("{\"authorizationId\":\"ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff\",\"outcome\":1,\"sourceDevice\":7,\"sourceInode\":9}")]
    public void Rejects_failed_unknown_or_mismatched_results(string json)
    {
        const string id = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        Assert.Throws<InvalidDataException>(() => NativeTransferResultVerifier.Parse(json, id));
    }
}
