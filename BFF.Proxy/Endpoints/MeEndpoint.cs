using Bff.Proxy.Csrf;

namespace Bff.Proxy.Endpoints;

public static class MeEndpoint
{
    public static void MapMeEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/bff/me", (HttpContext context) =>
            {
                // Must stay AllowAnonymous + a manual check: this endpoint IS the "am I logged in?" probe.
                // RequireAuthorization here would make an unauthenticated call auto-challenge the default
                // scheme (OIDC), 302-redirecting straight to Keycloak instead of just reporting 401/false.
                if (context.User.Identity?.IsAuthenticated != true)
                    return Results.Unauthorized();

                var claims = context.User.Claims.Select(c => new { c.Type, c.Value });
                var csrfToken = context.Items[CsrfConstants.CookieName] as string ?? context.Request.Cookies[CsrfConstants.CookieName];
                return Results.Ok(new { IsAuthenticated = true, Claims = claims, CsrfToken = csrfToken });
            })
            .AllowAnonymous();
    }
}
