namespace Bff.Proxy;

// Guards every post-login/-logout redirect the BFF performs on behalf of a caller-supplied `redirectUri`.
// Without it /bff/login and /bff/logout are open redirects: `?redirectUri=https://evil.example` would bounce the
// browser off this (trusted) host to any site, before or after a real Keycloak login.
//
// Allowed: a local path ("/", "/orders?x=1"), or an absolute http(s) URL whose origin is this BFF's own origin
// or one of Cors:AllowedOrigins (the split-origin SPA passes its own window.location.href).
//
// Where a caller supplies the value (/bff/login, /bff/logout) a disallowed one is rejected with a 400 before any
// Keycloak round trip (RejectedRedirect), as OAuth does for an invalid redirect_uri: a silent fallback would hide
// a misconfigured Cors:AllowedOrigins and, in a split-origin deployment, strand the user on the BFF's own "/".
// Values read back from the data-protected OIDC state were already checked on the way in; Sanitize() re-checks
// them as a backstop and falls back to "/".
public sealed class RedirectUriValidator
{
    private const int MaxLoggedLength = 200;

    private readonly HashSet<string> _allowedOrigins;
    private readonly ILogger<RedirectUriValidator> _logger;

    public RedirectUriValidator(IConfiguration configuration, ILogger<RedirectUriValidator> logger)
    {
        _logger = logger;
        _allowedOrigins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var origin in configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [])
        {
            if (Uri.TryCreate(origin, UriKind.Absolute, out var parsed))
                _allowedOrigins.Add(OriginOf(parsed));
        }
    }

    // A missing/empty redirectUri means "/". Returns false (and logs) for a disallowed one; safeRedirectUri is then "/".
    public bool TryGetSafeRedirect(HttpContext context, string? redirectUri, out string safeRedirectUri)
    {
        if (string.IsNullOrEmpty(redirectUri))
        {
            safeRedirectUri = "/";
            return true;
        }

        if (IsAllowed(context, redirectUri))
        {
            safeRedirectUri = redirectUri;
            return true;
        }

        _logger.LogWarning(
            "Rejected redirectUri {RedirectUri} on {Path}: not a local path or an allowed origin (Cors:AllowedOrigins)",
            redirectUri.Length > MaxLoggedLength ? redirectUri[..MaxLoggedLength] + "..." : redirectUri,
            context.Request.Path);
        safeRedirectUri = "/";
        return false;
    }

    // Backstop for values that were already validated on entry (e.g. carried in the OIDC state): never fails the request.
    public string Sanitize(HttpContext context, string? redirectUri)
    {
        TryGetSafeRedirect(context, redirectUri, out var safeRedirectUri);
        return safeRedirectUri;
    }

    // The 400 for a rejected caller-supplied redirectUri. Deliberately neither echoes the value (reflected content)
    // nor lists the allowed origins.
    public static IResult RejectedRedirect() => Results.Problem(
        statusCode: StatusCodes.Status400BadRequest,
        title: "Invalid redirectUri",
        detail: "redirectUri must be a local path or an absolute URL on an origin allowed by this BFF.");

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
