using System.Security.Cryptography;
using System.Text;

namespace Lidarr.AIImport.Companion.Security;

public static class CompanionAuthentication
{
    public const string HeaderName = "X-Companion-Api-Key";

    public static bool IsAuthorized(string? suppliedToken, string expectedToken)
    {
        if (string.IsNullOrEmpty(suppliedToken) || string.IsNullOrEmpty(expectedToken)) return false;
        var supplied = Encoding.UTF8.GetBytes(suppliedToken);
        var expected = Encoding.UTF8.GetBytes(expectedToken);
        return supplied.Length == expected.Length && CryptographicOperations.FixedTimeEquals(supplied, expected);
    }
}