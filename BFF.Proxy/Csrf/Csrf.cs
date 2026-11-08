using System.Security.Cryptography;
using System.Text;

namespace Bff.Proxy.Csrf;

public static class CsrfConstants
{
    public const string CookieName = "bff-csrf";
    public const string HeaderName = "X-CSRF-Token";

    // Form field accepted instead of the header on endpoints marked with RequireCsrfFormToken().
    public const string FormFieldName = "csrfToken";

    public static bool TokensMatch(string a, string b)
    {
        var aBytes = Encoding.UTF8.GetBytes(a);
        var bBytes = Encoding.UTF8.GetBytes(b);
        return aBytes.Length == bBytes.Length && CryptographicOperations.FixedTimeEquals(aBytes, bBytes);
    }
}

// Endpoint metadata for endpoints the SPA reaches with a top-level <form> POST (a navigation, so the browser can
// follow the redirect chain), which cannot carry a custom header. On such endpoints the CSRF token may arrive as
// the "csrfToken" form field, and it is enforced even without an authenticated session - a cross-site POST never
// carries the SameSite=Lax cookies, so it would otherwise look anonymous and skip the check.
public sealed class CsrfFormTokenMetadata;

public static class CsrfEndpointExtensions
{
    public static TBuilder RequireCsrfFormToken<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new CsrfFormTokenMetadata());
}
