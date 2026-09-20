using System.IdentityModel.Tokens.Jwt;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
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
                    // 2. Fetch JWKS keys from Keycloak. RequireHttps must follow the *authority* scheme, not the inbound
                    // request: Keycloak posts to this endpoint over plain HTTP in dev, and requiring HTTPS for an
                    // http:// authority makes every metadata fetch throw, so the logout is rejected with a 400.
                    var metadataAddress = authority.TrimEnd('/') + "/.well-known/openid-configuration";
                    var configManager = new ConfigurationManager<OpenIdConnectConfiguration>(
                        metadataAddress,
                        new OpenIdConnectConfigurationRetriever(),
                        new HttpDocumentRetriever { RequireHttps = authority.StartsWith("https://", StringComparison.OrdinalIgnoreCase) });

                    var oidcConfig = await configManager.GetConfigurationAsync(httpContext.RequestAborted);

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

                    var handler = new JwtSecurityTokenHandler();
                    var principal = handler.ValidateToken(logoutToken, validationParameters, out var validatedToken);

                    // 3. OIDC Spec Rules Validation
                    // Must NOT contain a nonce
                    if (principal.HasClaim(c => c.Type == "nonce"))
                    {
                        logger.LogWarning("Backchannel logout rejected: logout token contains a nonce.");
                        return Results.BadRequest("Logout token must not contain a nonce.");
                    }

                    // Must contain logout event claim
                    var eventsClaim = principal.FindFirst("events")?.Value;
                    if (string.IsNullOrEmpty(eventsClaim) || !eventsClaim.Contains("http://schemas.openid.net/event/backchannel-logout"))
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
                    return Results.BadRequest($"Token validation failed: {ex.Message}");
                }
            })
            .AllowAnonymous()
            .DisableAntiforgery(); // Keycloak sends raw POST without CSRF token
    }
}
