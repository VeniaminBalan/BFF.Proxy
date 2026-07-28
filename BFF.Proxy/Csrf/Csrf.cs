using System.Security.Cryptography;
using System.Text;

namespace Bff.Proxy.Csrf;

public static class CsrfConstants
{
    public const string CookieName = "bff-csrf";
    public const string HeaderName = "X-CSRF-Token";

    public static bool TokensMatch(string a, string b)
    {
        var aBytes = Encoding.UTF8.GetBytes(a);
        var bBytes = Encoding.UTF8.GetBytes(b);
        return aBytes.Length == bBytes.Length && CryptographicOperations.FixedTimeEquals(aBytes, bBytes);
    }
}
