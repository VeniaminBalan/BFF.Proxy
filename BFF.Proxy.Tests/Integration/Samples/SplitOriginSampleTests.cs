using System.Net;
using System.Text.RegularExpressions;
using Bff.Proxy.Csrf;
using BFF.Proxy.Tests.Integration.Infrastructure;

namespace BFF.Proxy.Tests.Integration.Samples;

// samples/proxy-only-split-origin: the unmodified base image (no wwwroot) as a pure BFF, with the SPA on another
// origin. The allowed origin is read from the sample's docker-compose.yml so the two can't drift apart.
public partial class SplitOriginSampleTests : BffIntegrationTest
{
    private static readonly string SpaOrigin = ReadComposeSpaOrigin();
    private const string OtherOrigin = "http://localhost:3001";


    protected override IReadOnlyDictionary<string, string?> Settings => new Dictionary<string, string?>
    {
        ["Cors:AllowedOrigins:0"] = SpaOrigin
    };

    protected override string CreateContentRoot()
    {
        var layout = DisposeAfterFixture(SampleLayout.Create());
        return layout.ContentRoot;
    }

    private static string ReadComposeSpaOrigin()
    {
        var compose = SampleLayout.ReadSampleFile("proxy-only-split-origin/docker-compose.yml");
        var match = AllowedOriginLine().Match(compose);
        Assert.That(match.Success, Is.True, "Cors__AllowedOrigins__0 not found in the sample's docker-compose.yml");
        return match.Groups[1].Value.Trim();
    }

    [GeneratedRegex(@"Cors__AllowedOrigins__0:\s*(\S+)")]
    private static partial Regex AllowedOriginLine();

    private static HttpRequestMessage Preflight(string path, string origin, string method, string? headers = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Options, path);
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", method);
        if (headers is not null)
            request.Headers.Add("Access-Control-Request-Headers", headers);
        return request;
    }

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? string.Join(",", values) : null;

    [Test]
    public async Task Without_a_wwwroot_unknown_routes_are_404()
    {
        using var response = await AnonymousClient().Http.GetAsync("/");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task Spa_origin_may_call_the_bff_with_credentials()
    {
        using var response = await AnonymousClient().Http.SendAsync(Preflight("/bff/me", SpaOrigin, "GET"));

        Assert.Multiple(() =>
        {
            Assert.That(Header(response, "Access-Control-Allow-Origin"), Is.EqualTo(SpaOrigin));
            Assert.That(Header(response, "Access-Control-Allow-Credentials"), Is.EqualTo("true"));
        });
    }

    [Test]
    public async Task Spa_origin_may_send_the_csrf_header_on_state_changing_calls()
    {
        using var response = await AnonymousClient().Http.SendAsync(
            Preflight("/bff/orgs/switch", SpaOrigin, "PUT", $"{CsrfConstants.HeaderName},content-type"));

        Assert.Multiple(() =>
        {
            Assert.That(Header(response, "Access-Control-Allow-Origin"), Is.EqualTo(SpaOrigin));
            Assert.That(Header(response, "Access-Control-Allow-Headers")?.ToLowerInvariant(),
                Does.Contain(CsrfConstants.HeaderName.ToLowerInvariant()));
        });
    }

    [Test]
    public async Task Other_origins_get_no_cors_grant()
    {
        using var response = await AnonymousClient().Http.SendAsync(Preflight("/bff/me", OtherOrigin, "GET"));

        Assert.Multiple(() =>
        {
            Assert.That(Header(response, "Access-Control-Allow-Origin"), Is.Null);
            Assert.That(Header(response, "Access-Control-Allow-Credentials"), Is.Null);
        });
    }

    // The split-origin round trip from BFF_CLIENT_SETUP.md: read the CSRF token from /bff/me (the SPA can't read
    // the BFF's cookie cross-origin), then echo it in the header.
    [Test]
    public async Task Cross_origin_csrf_round_trip_reaches_the_backend()
    {
        var client = await SignedInClientAsync();

        var meRequest = new HttpRequestMessage(HttpMethod.Get, "/bff/me");
        meRequest.Headers.Add("Origin", SpaOrigin);
        using var meResponse = await client.Http.SendAsync(meRequest);
        var csrf = (await meResponse.Content.ReadFromJsonAsync<MeResponse>())!.CsrfToken!;

        var apiRequest = new HttpRequestMessage(HttpMethod.Post, "/api/orders") { Content = JsonContent.Create(new { }) };
        apiRequest.Headers.Add("Origin", SpaOrigin);
        apiRequest.Headers.Add(CsrfConstants.HeaderName, csrf);
        using var apiResponse = await client.Http.SendAsync(apiRequest);

        Assert.Multiple(() =>
        {
            Assert.That(Header(meResponse, "Access-Control-Allow-Origin"), Is.EqualTo(SpaOrigin));
            Assert.That(apiResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(Header(apiResponse, "Access-Control-Allow-Origin"), Is.EqualTo(SpaOrigin));
        });
    }

    [Test]
    public async Task Login_may_return_to_the_spa_origin()
    {
        using var response = await AnonymousClient().Http.GetAsync($"/bff/login?redirectUri={Uri.EscapeDataString(SpaOrigin + "/after-login")}");

        Assert.That(response.Headers.Location?.ToString(), Does.StartWith(Keycloak.AuthorizationEndpoint));
    }

    [Test]
    public async Task Login_may_not_return_to_another_origin()
    {
        using var response = await AnonymousClient().Http.GetAsync($"/bff/login?redirectUri={Uri.EscapeDataString(OtherOrigin + "/")}");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }
}
