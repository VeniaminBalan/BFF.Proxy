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
                IOptions<KeycloakOptions> keycloakOptions) =>
            {
                var ticketStore = httpContext.RequestServices.GetRequiredService<RedisTicketStore>();

                // 1. Extract logout_token from form body
                if (!httpContext.Request.HasFormContentType || !httpContext.Request.Form.TryGetValue("logout_token", out var logoutTokenValues))
                {
                    return Results.BadRequest("Missing logout_token parameter.");
                }

                string logoutToken = logoutTokenValues.ToString();
                var clientId = keycloakOptions.Value.Resource;
                var authority = keycloakOptions.Value.Authority;

                try
                {
                    // 2. Fetch JWKS keys from Keycloak
                    var metadataAddress = authority.TrimEnd('/') + "/.well-known/openid-configuration";
                    var configManager = new ConfigurationManager<OpenIdConnectConfiguration>(
                        metadataAddress,
                        new OpenIdConnectConfigurationRetriever(),
                        new HttpDocumentRetriever { RequireHttps = !httpContext.Request.IsHttps });

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
                        return Results.BadRequest("Logout token must not contain a nonce.");
                    }

                    // Must contain logout event claim
                    var eventsClaim = principal.FindFirst("events")?.Value;
                    if (string.IsNullOrEmpty(eventsClaim) || !eventsClaim.Contains("back-channel-logout"))
                    {
                        return Results.BadRequest("Invalid logout event claim.");
                    }

                    // 4. Extract Keycloak Session ID (sid)
                    var sid = principal.FindFirst("sid")?.Value;
                    if (!string.IsNullOrEmpty(sid))
                    {
                        // Remove matching session from Redis
                        await ticketStore.RemoveBySidAsync(sid);
                        return Results.Ok();
                    }

                    return Results.BadRequest("Missing sid claim in logout token.");
                }
                catch (Exception ex)
                {
                    return Results.BadRequest($"Token validation failed: {ex.Message}");
                }
            })
            .AllowAnonymous()
            .DisableAntiforgery(); // Keycloak sends raw POST without CSRF token
    }
}
