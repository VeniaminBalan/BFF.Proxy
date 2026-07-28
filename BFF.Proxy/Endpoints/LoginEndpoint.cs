using Microsoft.AspNetCore.Authentication;

namespace Bff.Proxy.Endpoints;

public static class LoginEndpoint
{
    public static void MapLoginEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/bff/login", (HttpContext context, string? prompt, string? redirectUri = "/") =>
            {
                // Silent login: a valid BFF session cookie already exists, so there's no need to
                // round-trip through Keycloak again - just send the browser back with its cookie intact.
                if (context.User.Identity?.IsAuthenticated == true)
                {
                    return Results.Redirect(redirectUri ?? "/");
                }

                var authenticationProperties = new AuthenticationProperties
                {
                    RedirectUri = redirectUri,
                    Items =
                    {
                        ["prompt"] = prompt
                    }
                };

                return Results.Challenge(authenticationProperties);
            })
            .WithDescription("Initiates the login process. If the user is already authenticated, it redirects to the specified redirectUri. if 'prompt = select_account', it will force Keycloak to show the account selection screen.")
            .AllowAnonymous();
    }
}
