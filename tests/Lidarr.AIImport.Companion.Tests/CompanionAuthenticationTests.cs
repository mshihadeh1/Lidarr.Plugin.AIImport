using Lidarr.AIImport.Companion.Security;

namespace Lidarr.AIImport.Companion.Tests;

[TestFixture]
public sealed class CompanionAuthenticationTests
{
    [TestCase(null, false)]
    [TestCase("", false)]
    [TestCase("wrong-token", false)]
    [TestCase("companion-secret", true)]
    public void Requires_exact_companion_api_token(string? suppliedToken, bool expected)
    {
        Assert.That(CompanionAuthentication.IsAuthorized(suppliedToken, "companion-secret"), Is.EqualTo(expected));
    }
}