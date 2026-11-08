using Microsoft.AspNetCore.Authentication;

namespace Bff.Proxy.Endpoints;

public static class LoginEndpoint
{
    public static void MapLoginEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/bff/login", (HttpContext context, RedirectUriValidator redirectValidator, string? prompt, string? redirectUri = "/") =>
            {
                // Only local paths and allowed SPA origins. Rejected here, before the Keycloak round trip, so a bad
                // value never costs the user a login (and /bff/login is never an open redirect).
                if (!redirectValidator.TryGetSafeRedirect(context, redirectUri, out var safeRedirectUri))
                    return RedirectUriValidator.RejectedRedirect();

                // Silent login: a valid BFF session cookie already exists, so there's no need to
                // round-trip through Keycloak again - just send the browser back with its cookie intact.
                if (context.User.Identity?.IsAuthenticated == true)
                {
                    return Results.Redirect(safeRedirectUri);
                }

                var authenticationProperties = new AuthenticationProperties
                {
                    RedirectUri = safeRedirectUri,
                    Items =
                    {
                        ["prompt"] = prompt
                    }
                };

                return Results.Challenge(authenticationProperties);
            })
            .WithDescription("Initiates the login process. If the user is already authenticated, it redirects to the specified redirectUri (a local path or an origin in Cors:AllowedOrigins; anything else is a 400). if 'prompt = select_account', it will force Keycloak to show the account selection screen.")
            .AllowAnonymous();
    }
}
