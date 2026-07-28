using System.Security.Cryptography;

namespace Bff.Proxy.Csrf;

// Issue a (non-HttpOnly) double-submit CSRF cookie for every authenticated session.
// It isn't meant to be read by the SPA directly - the BFF and SPA are on different
// origins in dev, so the SPA can't see this cookie's value via document.cookie anyway.
// Instead the value is handed back in the /bff/me response body, and the SPA echoes
// it in the X-CSRF-Token header on state-changing requests.
public class CsrfCookieMiddleware(RequestDelegate next, IHostEnvironment environment)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (context.User.Identity?.IsAuthenticated == true)
        {
            var existingToken = context.Request.Cookies[CsrfConstants.CookieName];
            if (string.IsNullOrEmpty(existingToken))
            {
                var newToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
                context.Response.Cookies.Append(CsrfConstants.CookieName, newToken, new CookieOptions
                {
                    HttpOnly = false,
                    SameSite = SameSiteMode.Lax,
                    Secure = !environment.IsDevelopment(),
                    Path = "/"
                });
                context.Items[CsrfConstants.CookieName] = newToken;
            }
            else
            {
                context.Items[CsrfConstants.CookieName] = existingToken;
            }
        }

        await next(context);
    }
}
