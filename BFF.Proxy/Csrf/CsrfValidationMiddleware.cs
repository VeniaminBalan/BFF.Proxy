namespace Bff.Proxy.Csrf;

// Validate the CSRF cookie/header pair on state-changing requests. Requests without an
// authenticated BFF session (e.g. Keycloak's back-channel logout callback) skip this check -
// they never carry the CSRF cookie in the first place.
public class CsrfValidationMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var method = context.Request.Method;
        var isUnsafeMethod = HttpMethods.IsPost(method) || HttpMethods.IsPut(method) ||
                              HttpMethods.IsPatch(method) || HttpMethods.IsDelete(method);

        if (isUnsafeMethod && context.User.Identity?.IsAuthenticated == true)
        {
            var cookieToken = context.Request.Cookies[CsrfConstants.CookieName];
            var headerToken = context.Request.Headers[CsrfConstants.HeaderName].ToString();

            if (string.IsNullOrEmpty(cookieToken) || string.IsNullOrEmpty(headerToken) ||
                !CsrfConstants.TokensMatch(cookieToken, headerToken))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new { error = "Missing or invalid CSRF token." });
                return;
            }
        }

        await next(context);
    }
}
