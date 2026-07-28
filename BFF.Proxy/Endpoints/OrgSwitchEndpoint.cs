using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Options;

namespace Bff.Proxy.Endpoints;

public record SwitchOrgRequest(string OrganizationId);

public static class OrgSwitchEndpoint
{
    public static void MapOrgSwitchEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPut("/bff/orgs/switch", async (
                HttpContext context,
                SwitchOrgRequest request,
                IHttpClientFactory clientFactory,
                IOptions<KeycloakOptions> keycloakOptions) =>
            {
                var accessToken = await context.GetTokenAsync("access_token");
                var refreshToken = await context.GetTokenAsync("refresh_token");

                if (string.IsNullOrEmpty(accessToken) || string.IsNullOrEmpty(refreshToken))
                    return Results.Unauthorized();

                var client = clientFactory.CreateClient("KeycloakClient");
                client.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", accessToken);

                // 1. Tell Phase Two to switch the user's active organization
                var switchResponse = await client.PutAsJsonAsync("users/switch-organization", new
                {
                    id = request.OrganizationId
                });

                if (!switchResponse.IsSuccessStatusCode)
                {
                    return Results.Problem(
                        detail: "Failed to switch organization in Keycloak",
                        statusCode: (int)switchResponse.StatusCode);
                }

                // 2. Perform OIDC Token Refresh to get a new access token with the updated org claim
                var tokenEndpoint = $"{keycloakOptions.Value.Authority}/protocol/openid-connect/token";
                var tokenParams = new Dictionary<string, string>
                {
                    ["grant_type"] = "refresh_token",
                    ["client_id"] = keycloakOptions.Value.Resource,
                    ["client_secret"] = keycloakOptions.Value.Credentials.Secret,
                    ["refresh_token"] = refreshToken
                };

                var refreshResponse = await client.PostAsync(tokenEndpoint, new FormUrlEncodedContent(tokenParams));
                if (!refreshResponse.IsSuccessStatusCode)
                {
                    return Results.Problem("Failed to refresh session token after switching organization.", statusCode: 500);
                }

                var tokenJson = await refreshResponse.Content.ReadAsStringAsync();
                using var tokenDocument = JsonDocument.Parse(tokenJson);
                var tokenResult = tokenDocument.RootElement;
                var newAccessToken = tokenResult.GetProperty("access_token").GetString();
                var newRefreshToken = tokenResult.TryGetProperty("refresh_token", out var refreshTokenElement)
                    ? refreshTokenElement.GetString()
                    : null;

                // 3. Update the Redis Session TicketStore with the new tokens
                var authResult = await context.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                if (authResult.Succeeded && authResult.Properties != null && authResult.Principal != null)
                {
                    authResult.Properties.UpdateTokenValue("access_token", newAccessToken);
                    if (!string.IsNullOrEmpty(newRefreshToken))
                    {
                        authResult.Properties.UpdateTokenValue("refresh_token", newRefreshToken);
                    }

                    // The cached principal's claims (org, realm_access) still reflect the
                    // pre-switch token, so /bff/me would keep returning the old org until
                    // the session naturally expired. Patch them from the new access token.
                    var newJwt = new JwtSecurityTokenHandler().ReadJwtToken(newAccessToken);
                    var identity = (ClaimsIdentity)authResult.Principal.Identity!;
                    foreach (var claimType in new[] { "org", "realm_access" })
                    {
                        foreach (var oldClaim in identity.FindAll(claimType).ToList())
                        {
                            identity.RemoveClaim(oldClaim);
                        }

                        var newClaim = newJwt.Claims.FirstOrDefault(c => c.Type == claimType);
                        if (newClaim != null)
                        {
                            identity.AddClaim(new Claim(claimType, newClaim.Value, newClaim.ValueType));
                        }
                    }

                    // Re-sign in to update Redis via ITicketStore
                    await context.SignInAsync(
                        CookieAuthenticationDefaults.AuthenticationScheme,
                        authResult.Principal,
                        authResult.Properties);
                }

                return Results.Content(tokenJson, "application/json");
            })
            .RequireAuthorization("RequireAuthenticatedUser");
    }
}
