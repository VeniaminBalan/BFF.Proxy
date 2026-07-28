using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;

namespace Bff.Proxy.Endpoints;

public static class LogoutEndpoint
{
    public static void MapLogoutEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/bff/logout", (string? redirectUri = "/") =>
                Results.SignOut(
                    new AuthenticationProperties { RedirectUri = redirectUri ?? "/" },
                    [CookieAuthenticationDefaults.AuthenticationScheme, OpenIdConnectDefaults.AuthenticationScheme]
                ))
            .AllowAnonymous();
    }
}
