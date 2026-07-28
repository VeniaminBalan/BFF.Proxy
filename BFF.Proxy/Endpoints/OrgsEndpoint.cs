using System.Net.Http.Headers;
using Microsoft.AspNetCore.Authentication;

namespace Bff.Proxy.Endpoints;

public static class OrgsEndpoint
{
    public static void MapOrgsEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/bff/orgs", async (
                HttpContext context,
                IHttpClientFactory clientFactory) =>
            {
                var accessToken = await context.GetTokenAsync("access_token");
                if (string.IsNullOrEmpty(accessToken))
                    return Results.Unauthorized();

                var client = clientFactory.CreateClient("KeycloakClient");
                client.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", accessToken);

                // Calls Phase Two endpoint: /realms/{realm}/orgs (KeycloakClient's BaseAddress is
                // already the realm-scoped authority, so this is realm-relative, not hardcoded)
                var response = await client.GetAsync("orgs");
                if (!response.IsSuccessStatusCode)
                {
                    return Results.StatusCode((int)response.StatusCode);
                }

                var content = await response.Content.ReadAsStringAsync();
                return Results.Content(content, "application/json");
            })
            .RequireAuthorization("RequireAuthenticatedUser");
    }
}
