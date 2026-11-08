using System.Net;
using Bff.Proxy.Csrf;
using BFF.Proxy.Tests.Integration.Infrastructure;

namespace BFF.Proxy.Tests.Integration;

public class SessionAndCsrfTests : BffIntegrationTest
{
    [Test]
    public async Task Me_reports_401_for_anonymous_callers_instead_of_redirecting_to_keycloak()
    {
        using var response = await AnonymousClient().Http.GetAsync("/bff/me");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task Me_returns_claims_and_the_csrf_token_matching_the_csrf_cookie()
    {
        var client = await SignedInClientAsync(NewSession(sid: "sid-me"));

        var me = await client.GetMeAsync();

        Assert.That(me, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(me!.IsAuthenticated, Is.True);
            Assert.That(me.Claim("sub"), Is.EqualTo("user-1"));
            Assert.That(me.Claim("sid"), Is.EqualTo("sid-me"));
            Assert.That(me.CsrfToken, Is.Not.Null.And.Not.Empty);
            // ASP.NET Core URL-encodes cookie values (and decodes them on the way back in).
            Assert.That(Uri.UnescapeDataString(client.GetCookie(CsrfConstants.CookieName)!.Value), Is.EqualTo(me.CsrfToken));
        });
    }

    [Test]
    public async Task Me_never_exposes_tokens()
    {
        var session = NewSession();
        var client = await SignedInClientAsync(session);

        using var response = await client.Http.GetAsync("/bff/me");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            Assert.That(body, Does.Not.Contain(session.AccessToken!));
            Assert.That(body, Does.Not.Contain(session.RefreshToken!));
        });
    }

    [Test]
    public async Task Csrf_token_is_stable_for_the_session()
    {
        var client = await SignedInClientAsync();

        var first = await client.GetCsrfTokenAsync();
        var second = await client.GetCsrfTokenAsync();

        Assert.That(second, Is.EqualTo(first));
    }

    [Test]
    public async Task Session_cookie_is_http_only_and_not_readable_by_the_spa()
    {
        var client = AnonymousClient();

        using var response = await client.Http.PostAsJsonAsync(TestSignInStartupFilter.Path, NewSession());
        var setCookie = response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("bff-session-"));

        Assert.Multiple(() =>
        {
            Assert.That(setCookie, Does.Contain("httponly").IgnoreCase);
            Assert.That(setCookie, Does.Contain("samesite=lax").IgnoreCase);
        });
    }

    [TestCase("POST")]
    [TestCase("PUT")]
    [TestCase("PATCH")]
    [TestCase("DELETE")]
    public async Task Unsafe_api_calls_without_csrf_header_are_rejected_before_reaching_the_backend(string method)
    {
        var client = await SignedInClientAsync();

        using var response = await client.Http.SendAsync(new HttpRequestMessage(new HttpMethod(method), "/api/items"));

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
            Assert.That(BackendRequests, Is.Empty);
        });
    }

    [Test]
    public async Task Unsafe_api_call_with_a_wrong_csrf_header_is_rejected()
    {
        var client = await SignedInClientAsync();
        await client.GetCsrfTokenAsync(); // issues the cookie

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/items");
        request.Headers.Add(CsrfConstants.HeaderName, "forged-token");
        using var response = await client.Http.SendAsync(request);

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
            Assert.That(BackendRequests, Is.Empty);
        });
    }

    [Test]
    public async Task Unsafe_api_call_with_the_csrf_header_reaches_the_backend()
    {
        var client = await SignedInClientAsync();

        using var response = await client.SendWithCsrfAsync(HttpMethod.Post, "/api/items", JsonContent.Create(new { name = "x" }));

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(BackendRequests.Single().RequestMessage!.Method, Is.EqualTo("POST"));
            Assert.That(BackendRequests.Single().RequestMessage!.Body, Does.Contain("\"name\""));
        });
    }

    [Test]
    public async Task Safe_api_calls_need_no_csrf_header()
    {
        var client = await SignedInClientAsync();

        using var response = await client.Http.GetAsync("/api/items");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public async Task Anonymous_api_calls_are_never_proxied()
    {
        using var response = await AnonymousClient().Http.GetAsync("/api/items");

        Assert.Multiple(() =>
        {
            // The route's RequireAuthenticatedUser policy challenges the default scheme (OIDC).
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Redirect));
            Assert.That(response.Headers.Location?.ToString(), Does.StartWith(Keycloak.AuthorizationEndpoint));
            Assert.That(BackendRequests, Is.Empty);
        });
    }

    [Test]
    public async Task Health_endpoint_reports_statuses_without_details()
    {
        using var response = await AnonymousClient().Http.GetAsync("/bff/health");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(body, Does.Contain("\"keycloak\"").And.Contain("\"proxied-backend\""));
            Assert.That(body, Does.Not.Contain(Keycloak.BaseUrl).And.Not.Contain(Backend.Url!));
        });
    }
}
