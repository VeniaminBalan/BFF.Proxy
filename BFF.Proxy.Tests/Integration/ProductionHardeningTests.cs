using System.Net;
using Bff.Proxy.Csrf;
using BFF.Proxy.Tests.Integration.Infrastructure;

namespace BFF.Proxy.Tests.Integration;

// Production-only behaviour. TestServer with an https base address stands in for TLS.
public class ProductionHardeningTests : BffIntegrationTest
{
    private const string PublicHost = "https://bff.example.test";

    protected override string Environment => "Production";

    // Production requires an https authority (RequireHttpsMetadata), checked on every request by the OIDC
    // handler. None of these tests talk to Keycloak, so it never has to resolve.
    protected override IReadOnlyDictionary<string, string?> Settings => new Dictionary<string, string?>
    {
        ["AllowedHosts"] = "bff.example.test",
        ["Keycloak:auth-server-url"] = "https://keycloak.example.test"
    };

    private static string SetCookie(HttpResponseMessage response, string cookieName) =>
        response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith(cookieName + "=", StringComparison.Ordinal));

    [Test]
    public async Task Session_cookie_uses_the_host_prefix_and_is_secure()
    {
        using var response = await AnonymousClient(PublicHost).Http.PostAsJsonAsync(TestSignInStartupFilter.Path, NewSession());
        var cookie = SetCookie(response, "__Host-bff-session").ToLowerInvariant();

        Assert.Multiple(() =>
        {
            Assert.That(cookie, Does.Contain("secure"));
            Assert.That(cookie, Does.Contain("httponly"));
            Assert.That(cookie, Does.Contain("path=/;").Or.EndWith("path=/"));
            Assert.That(cookie, Does.Not.Contain("domain="), "__Host- cookies must not set Domain");
        });
    }

    [Test]
    public async Task Csrf_cookie_is_secure()
    {
        var client = await SignedInClientAsync(baseAddress: PublicHost);

        using var response = await client.Http.GetAsync("/bff/me");

        Assert.That(SetCookie(response, CsrfConstants.CookieName).ToLowerInvariant(), Does.Contain("secure"));
    }

    [Test]
    public async Task Https_responses_carry_hsts()
    {
        using var response = await AnonymousClient(PublicHost).Http.GetAsync("/bff/me");

        Assert.That(response.Headers.Contains("Strict-Transport-Security"), Is.True);
    }

    [Test]
    public async Task Unknown_host_header_is_rejected()
    {
        using var response = await AnonymousClient("https://evil.example.test").Http.GetAsync("/bff/me");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [TestCase("/health")]
    [TestCase("/alive")]
    public async Task Development_health_endpoints_are_not_exposed(string path)
    {
        using var response = await AnonymousClient(PublicHost).Http.GetAsync(path);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }
}
