namespace Bff.Proxy;

// Guards every post-login/-logout redirect the BFF performs on behalf of a caller-supplied `redirectUri`.
// Without it /bff/login and /bff/logout are open redirects: `?redirectUri=https://evil.example` would bounce the
// browser off this (trusted) host to any site, before or after a real Keycloak login.
//
// Allowed: a local path ("/", "/orders?x=1"), or an absolute http(s) URL whose origin is this BFF's own origin
// or one of Cors:AllowedOrigins (the split-origin SPA passes its own window.location.href). Anything else
// falls back to "/".
public sealed class RedirectUriValidator
{
    private readonly HashSet<string> _allowedOrigins;

    public RedirectUriValidator(IConfiguration configuration)
    {
        _allowedOrigins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var origin in configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [])
        {
            if (Uri.TryCreate(origin, UriKind.Absolute, out var parsed))
                _allowedOrigins.Add(OriginOf(parsed));
        }
    }

    public string Sanitize(HttpContext context, string? redirectUri) =>
        IsAllowed(context, redirectUri) ? redirectUri! : "/";

    public bool IsAllowed(HttpContext context, string? redirectUri)
    {
        if (string.IsNullOrEmpty(redirectUri))
            return false;

        // Browsers strip tabs/newlines from URLs, so "/\t/evil.example" would become "//evil.example".
        if (redirectUri.Any(char.IsControl))
            return false;

        if (redirectUri[0] == '/')
        {
            // "//host" and "/\host" are protocol-relative (browsers treat '\' as '/'), i.e. another site.
            return redirectUri.Length == 1 || (redirectUri[1] != '/' && redirectUri[1] != '\\');
        }

        if (!Uri.TryCreate(redirectUri, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            || !string.IsNullOrEmpty(uri.UserInfo))
            return false;

        var origin = OriginOf(uri);
        var ownOrigin = $"{context.Request.Scheme}://{context.Request.Host}";
        return _allowedOrigins.Contains(origin)
               || (Uri.TryCreate(ownOrigin, UriKind.Absolute, out var own)
                   && string.Equals(origin, OriginOf(own), StringComparison.OrdinalIgnoreCase));
    }

    // scheme://host[:port], with the default port dropped, so "https://a.com:443" == "https://a.com".
    private static string OriginOf(Uri uri) => uri.GetLeftPart(UriPartial.Authority);
}
