using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Bff.Proxy.Endpoints;

public static class BackchannelLogoutEndpoint
{
    public static void MapBackchannelLogoutEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost("/bff/logout/backchannel", async (
                HttpContext httpContext,
                IOptions<KeycloakOptions> keycloakOptions,
                IOptionsMonitor<OpenIdConnectOptions> oidcOptions,
                SigningKeyRefresher signingKeyRefresher,
                ILoggerFactory loggerFactory) =>
            {
                var logger = loggerFactory.CreateLogger("Bff.Proxy.BackchannelLogout");
                var ticketStore = httpContext.RequestServices.GetRequiredService<RedisTicketStore>();

                logger.LogInformation(
                    "Backchannel logout received from {RemoteIp} (client={Client}, contentType={ContentType})",
                    httpContext.Connection.RemoteIpAddress, keycloakOptions.Value.Resource, httpContext.Request.ContentType);

                // 1. Extract logout_token from form body
                if (!httpContext.Request.HasFormContentType || !httpContext.Request.Form.TryGetValue("logout_token", out var logoutTokenValues))
                {
                    logger.LogWarning("Backchannel logout rejected: missing logout_token parameter.");
                    return Results.BadRequest("Missing logout_token parameter.");
                }

                string logoutToken = logoutTokenValues.ToString();
                var clientId = keycloakOptions.Value.Resource;
                var authority = keycloakOptions.Value.Authority;

                try
                {
                    // 2. Validate against Keycloak's signing keys. Reuse the OIDC handler's ConfigurationManager: it is a
                    // singleton that caches the discovery document and JWKS, so this anonymous endpoint doesn't fetch
                    // from Keycloak on every POST (which anyone could use to hammer Keycloak through the BFF).
                    var openIdConnectOptions = oidcOptions.Get(OpenIdConnectDefaults.AuthenticationScheme);
                    var configManager = openIdConnectOptions.ConfigurationManager
                                        ?? throw new InvalidOperationException("OIDC ConfigurationManager is not configured.");

                    ClaimsPrincipal principal;
                    try
                    {
                        var cachedConfig = await configManager.GetConfigurationAsync(httpContext.RequestAborted);
                        principal = ValidateLogoutToken(cachedConfig, logoutToken, authority, clientId);
                    }
                    catch (SecurityTokenSignatureKeyNotFoundException)
                    {
                        // Keycloak rotated its keys since the JWKS was cached. ConfigurationManager.RequestRefresh only
                        // refreshes in the background, so retrying against it would still see the old keys and this
                        // logout (which Keycloak does not resend) would be lost. Fetch the keys now instead - at most
                        // once per SigningKeyRefresher interval, so a flood of bogus tokens can't hammer Keycloak.
                        var freshConfig = await signingKeyRefresher.GetFreshConfigurationAsync(openIdConnectOptions, httpContext.RequestAborted);
                        principal = ValidateLogoutToken(freshConfig, logoutToken, authority, clientId);
                    }

                    // 3. OIDC Spec Rules Validation
                    // Must NOT contain a nonce
                    if (principal.HasClaim(c => c.Type == "nonce"))
                    {
                        logger.LogWarning("Backchannel logout rejected: logout token contains a nonce.");
                        return Results.BadRequest("Logout token must not contain a nonce.");
                    }

                    // Must contain logout event claim
                    var eventsClaim = principal.FindFirst("events")?.Value;
                    if (!HasBackchannelLogoutEvent(eventsClaim))
                    {
                        logger.LogWarning("Backchannel logout rejected: missing backchannel-logout event claim (http://schemas.openid.net/event/backchannel-logout).");
                        return Results.BadRequest("Invalid logout event claim.");
                    }

                    // 4. Extract Keycloak Session ID (sid)
                    var sid = principal.FindFirst("sid")?.Value;
                    if (!string.IsNullOrEmpty(sid))
                    {
                        // Remove matching session from Redis
                        var removed = await ticketStore.RemoveBySidAsync(sid);
                        logger.LogInformation(
                            "Backchannel logout validated for sid {Sid}: {Outcome}",
                            sid, removed ? "session removed" : "no session found for this sid on this instance");
                        return Results.Ok();
                    }

                    logger.LogWarning("Backchannel logout rejected: logout token has no sid claim.");
                    return Results.BadRequest("Missing sid claim in logout token.");
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Backchannel logout rejected: token validation failed: {Message}", ex.Message);
                    return Results.BadRequest("Invalid logout token.");
                }
            })
            .AllowAnonymous()
            .DisableAntiforgery(); // Keycloak sends raw POST without CSRF token
    }

    private static ClaimsPrincipal ValidateLogoutToken(
        OpenIdConnectConfiguration oidcConfig,
        string logoutToken,
        string authority,
        string clientId)
    {
        var validationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = authority,
            ValidateAudience = true,
            ValidAudience = clientId,
            ValidateIssuerSigningKey = true,
            IssuerSigningKeys = oidcConfig.SigningKeys,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(2)
        };

        return new JwtSecurityTokenHandler().ValidateToken(logoutToken, validationParameters, out _);
    }

    // The "events" claim is a JSON object keyed by event URI; check for the key, not a substring.
    private static bool HasBackchannelLogoutEvent(string? eventsClaim)
    {
        if (string.IsNullOrEmpty(eventsClaim)) return false;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(eventsClaim);
            return doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                   && doc.RootElement.TryGetProperty("http://schemas.openid.net/event/backchannel-logout", out _);
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }
}

// Fetches Keycloak's current signing keys on demand (after a key rotation), rate-limited per BFF instance.
// Within the interval it hands back the OIDC handler's cached configuration, which the RequestRefresh below
// has meanwhile updated in the background.
public sealed class SigningKeyRefresher
{
    private static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(30);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _lastFetch = DateTimeOffset.MinValue;

    public async Task<OpenIdConnectConfiguration> GetFreshConfigurationAsync(OpenIdConnectOptions options, CancellationToken cancellationToken)
    {
        var configManager = options.ConfigurationManager
                            ?? throw new InvalidOperationException("OIDC ConfigurationManager is not configured.");

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (DateTimeOffset.UtcNow - _lastFetch < MinInterval)
                return await configManager.GetConfigurationAsync(cancellationToken);

            _lastFetch = DateTimeOffset.UtcNow;
            configManager.RequestRefresh(); // also update the cache the OIDC handler uses
            var retriever = new Microsoft.IdentityModel.Protocols.HttpDocumentRetriever(options.Backchannel)
            {
                RequireHttps = options.RequireHttpsMetadata
            };
            return await OpenIdConnectConfigurationRetriever.GetAsync(options.MetadataAddress, retriever, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }
}
