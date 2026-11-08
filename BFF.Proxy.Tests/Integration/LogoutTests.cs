using System.Net;
using Bff.Proxy.Csrf;
using BFF.Proxy.Tests.Integration.Infrastructure;
using Microsoft.AspNetCore.WebUtilities;

namespace BFF.Proxy.Tests.Integration;

public class LogoutTests : BffIntegrationTest
{
    private static FormUrlEncodedContent LogoutForm(string? csrfToken, string? redirectUri = null)
    {
        var fields = new Dictionary<string, string>();
        if (csrfToken is not null) fields[CsrfConstants.FormFieldName] = csrfToken;
        if (redirectUri is not null) fields["redirectUri"] = redirectUri;
        return new FormUrlEncodedContent(fields);
    }

    [Test]
    public async Task Get_logout_is_not_allowed()
    {
        var client = await SignedInClientAsync();

        using var response = await client.Http.GetAsync("/bff/logout");

        Assert.Multiple(async () =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.MethodNotAllowed));
            Assert.That(await client.GetMeAsync(), Is.Not.Null, "a GET must not log the user out");
        });
    }

    [Test]
    public async Task Logout_without_csrf_token_is_rejected_and_the_session_survives()
    {
        var client = await SignedInClientAsync();

        using var response = await client.Http.PostAsync("/bff/logout", LogoutForm(csrfToken: null));

        Assert.Multiple(async () =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
            Assert.That(await client.GetMeAsync(), Is.Not.Null);
        });
    }

    [Test]
    public async Task Anonymous_cross_site_logout_post_is_rejected()
    {
        using var response = await AnonymousClient().Http.PostAsync("/bff/logout", LogoutForm(csrfToken: "guess"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
    }

    [Test]
    public async Task Logout_with_a_disallowed_redirectUri_is_rejected_and_the_session_survives()
    {
        var client = await SignedInClientAsync();
        var csrf = await client.GetCsrfTokenAsync();

        using var response = await client.Http.PostAsync("/bff/logout", LogoutForm(csrf, "https://evil.example/"));

        Assert.Multiple(async () =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(await client.GetMeAsync(), Is.Not.Null);
        });
    }

    [Test]
    public async Task Logout_ends_the_session_and_redirects_through_keycloak_end_session_back_to_the_spa()
    {
        var client = await SignedInClientAsync();
        var csrf = await client.GetCsrfTokenAsync();

        using var response = await client.Http.PostAsync("/bff/logout", LogoutForm(csrf, "/bye"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Redirect));
        var location = response.Headers.Location!;
        var query = QueryHelpers.ParseQuery(location.Query);
        Assert.Multiple(async () =>
        {
            Assert.That(location.GetLeftPart(UriPartial.Path), Is.EqualTo(Keycloak.EndSessionEndpoint));
            Assert.That(query["post_logout_redirect_uri"].ToString(), Is.EqualTo("https://localhost/signout-callback-oidc"));
            Assert.That(await client.GetMeAsync(), Is.Null);
            Assert.That(client.GetCookie(CsrfConstants.CookieName), Is.Null, "CSRF cookie should be cleared");
        });

        // Keycloak sends the browser back to the signout callback, which finishes at the SPA's redirectUri.
        using var callback = await client.Http.GetAsync($"/signout-callback-oidc?state={Uri.EscapeDataString(query["state"].ToString())}");
        Assert.Multiple(() =>
        {
            Assert.That(callback.StatusCode, Is.EqualTo(HttpStatusCode.Redirect));
            Assert.That(callback.Headers.Location?.ToString(), Is.EqualTo("/bye"));
        });
    }

    [Test]
    public async Task Logout_accepts_the_csrf_token_as_a_header_too()
    {
        var client = await SignedInClientAsync();

        using var response = await client.SendWithCsrfAsync(HttpMethod.Post, "/bff/logout");

        Assert.That(response.Headers.Location?.ToString(), Does.StartWith(Keycloak.EndSessionEndpoint));
    }

    [Test]
    public async Task Logout_revokes_the_server_side_session_so_a_copied_cookie_is_useless()
    {
        var client = await SignedInClientAsync();
        var csrf = await client.GetCsrfTokenAsync();
        var copiedCookies = client.Cookies.GetCookieHeader(client.BaseAddress);

        using (await client.Http.PostAsync("/bff/logout", LogoutForm(csrf))) { }

        using var replay = Factory.CreateDefaultClient(client.BaseAddress);
        var request = new HttpRequestMessage(HttpMethod.Get, "/bff/me");
        request.Headers.Add("Cookie", copiedCookies);
        using var response = await replay.SendAsync(request);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }
}
