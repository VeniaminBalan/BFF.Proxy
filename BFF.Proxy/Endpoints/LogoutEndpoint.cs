using Bff.Proxy.Csrf;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;

namespace Bff.Proxy.Endpoints;

public static class LogoutEndpoint
{
    public static void MapLogoutEndpoint(this IEndpointRouteBuilder app)
    {
        // POST + CSRF token, so another site can't log the user out by navigating them here (a GET logout is
        // reachable with a plain link, and SameSite=Lax still sends the cookie on top-level GET navigations).
        // The SPA submits a <form> (a navigation, so the browser follows the Keycloak end-session redirects)
        // with the "csrfToken" and optional "redirectUri" fields; see BFF_CLIENT_SETUP.md.
        app.MapPost("/bff/logout", async (HttpContext context, RedirectUriValidator redirectValidator) =>
            {
                string? redirectUri = context.Request.Query["redirectUri"];
                if (context.Request.HasFormContentType)
                {
                    var form = await context.Request.ReadFormAsync(context.RequestAborted);
                    if (form.TryGetValue("redirectUri", out var formRedirectUri))
                        redirectUri = formRedirectUri;
                }

                // The next session gets a fresh CSRF token instead of reusing this one.
                context.Response.Cookies.Delete(CsrfConstants.CookieName, new CookieOptions { Path = "/" });

                return Results.SignOut(
                    new AuthenticationProperties { RedirectUri = redirectValidator.Sanitize(context, redirectUri) },
                    [CookieAuthenticationDefaults.AuthenticationScheme, OpenIdConnectDefaults.AuthenticationScheme]);
            })
            .AllowAnonymous()
            .DisableAntiforgery() // CSRF is enforced by CsrfValidationMiddleware via RequireCsrfFormToken()
            .RequireCsrfFormToken();

        // Former GET logout: answer with an explicit hint instead of a bare 404 from the SPA fallback.
        app.MapGet("/bff/logout", () => Results.Problem(
                detail: "Logout requires POST with the CSRF token (form field 'csrfToken' or the X-CSRF-Token header). See BFF_CLIENT_SETUP.md.",
                statusCode: StatusCodes.Status405MethodNotAllowed))
            .AllowAnonymous();
    }
}
