using System.Net;
using BFF.Proxy.Tests.Integration.Infrastructure;

namespace BFF.Proxy.Tests.Integration;

public class ReverseProxyTests : BffIntegrationTest
{
    [Test]
    public async Task Api_calls_are_forwarded_with_the_session_access_token()
    {
        var session = NewSession();
        var client = await SignedInClientAsync(session);

        using var response = await client.Http.GetAsync("/api/orders/42?expand=lines");

        var forwarded = BackendRequests.Single();
        Assert.Multiple(async () =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(await response.Content.ReadAsStringAsync(), Is.EqualTo("backend-ok"));
            Assert.That(forwarded.RequestMessage!.Path, Is.EqualTo("/api/orders/42"));
            Assert.That(forwarded.RequestMessage!.RawQuery, Is.EqualTo("?expand=lines"));
            Assert.That(Header(forwarded, "Authorization"), Is.EqualTo($"Bearer {session.AccessToken}"));
        });
    }

    [Test]
    public async Task Client_supplied_authorization_header_is_replaced()
    {
        var session = NewSession();
        var client = await SignedInClientAsync(session);

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/orders");
        request.Headers.Add("Authorization", "Bearer attacker-token");
        using (await client.Http.SendAsync(request)) { }

        Assert.That(Header(BackendRequests.Single(), "Authorization"), Is.EqualTo($"Bearer {session.AccessToken}"));
    }

    [Test]
    public async Task Browser_cookies_are_not_forwarded_to_the_backend()
    {
        var client = await SignedInClientAsync();
        await client.GetCsrfTokenAsync(); // make sure the CSRF cookie exists too
        client.Cookies.Add(client.BaseAddress, new Cookie("tracking", "123"));

        using (await client.Http.GetAsync("/api/orders")) { }

        Assert.That(Header(BackendRequests.Single(), "Cookie"), Is.Null);
    }

    [Test]
    public async Task Session_without_access_token_forwards_no_authorization_header()
    {
        var client = await SignedInClientAsync(NewSession() with { AccessToken = null });

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/orders");
        request.Headers.Add("Authorization", "Bearer attacker-token");
        using (await client.Http.SendAsync(request)) { }

        Assert.That(Header(BackendRequests.Single(), "Authorization"), Is.Null);
    }
}
