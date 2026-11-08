using System.Globalization;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace BFF.Proxy.Tests.Integration.Infrastructure;

// What a successful OIDC login would have put in the session. Org is the raw JSON of Phase Two's `org` claim.
public sealed record TestSession(
    string Subject,
    string Sid,
    string? AccessToken,
    string? RefreshToken,
    DateTimeOffset ExpiresAt,
    string? Org = null);

// Test-only shortcut past the Keycloak login page: signs in through the app's real cookie handler and
// ticket store, with whatever tokens/claims the test wants. Registered only by BffFactory.
public sealed class TestSignInStartupFilter : IStartupFilter
{
    public const string Path = "/__test/signin";

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        // Not app.Map: that would move the path into PathBase, and the cookie handler scopes the cookie to PathBase.
        app.Use(async (context, nextMiddleware) =>
        {
            if (!context.Request.Path.Equals(Path, StringComparison.Ordinal))
            {
                await nextMiddleware(context);
                return;
            }

            var session = await context.Request.ReadFromJsonAsync<TestSession>()
                          ?? throw new InvalidOperationException("Missing session");

            var claims = new List<Claim>
            {
                new("sub", session.Subject),
                new("sid", session.Sid),
                new("preferred_username", session.Subject)
            };
            if (session.Org is not null)
                claims.Add(new Claim("org", session.Org, "JSON"));

            var tokens = new List<AuthenticationToken>
            {
                new() { Name = "expires_at", Value = session.ExpiresAt.ToString("o", CultureInfo.InvariantCulture) }
            };
            if (session.AccessToken is not null)
                tokens.Add(new AuthenticationToken { Name = "access_token", Value = session.AccessToken });
            if (session.RefreshToken is not null)
                tokens.Add(new AuthenticationToken { Name = "refresh_token", Value = session.RefreshToken });

            var properties = new AuthenticationProperties();
            properties.StoreTokens(tokens);

            await context.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                new ClaimsPrincipal(new ClaimsIdentity(claims, "test", "preferred_username", "roles")),
                properties);
            context.Response.StatusCode = StatusCodes.Status204NoContent;
        });

        next(app);
    };
}
