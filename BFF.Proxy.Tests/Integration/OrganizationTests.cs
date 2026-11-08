using System.Net;
using System.Text.Json;
using BFF.Proxy.Tests.Integration.Infrastructure;

namespace BFF.Proxy.Tests.Integration;

public class OrganizationTests : BffIntegrationTest
{
    private const string OrgsJson = """[{"id":"acme-id","name":"acme"},{"id":"globex-id","name":"globex"}]""";
    private const string AcmeOrgClaim = """{"id":"acme-id","name":"acme","role":[],"attribute":{}}""";

    [Test]
    public async Task Orgs_are_fetched_from_phase_two_with_the_users_token()
    {
        var session = NewSession();
        var client = await SignedInClientAsync(session);
        Keycloak.StubOrganizations(200, OrgsJson);

        using var response = await client.Http.GetAsync("/bff/orgs");

        Assert.Multiple(async () =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/json"));
            Assert.That(await response.Content.ReadAsStringAsync(), Is.EqualTo(OrgsJson));
            Assert.That(Header(Keycloak.RequestsTo("/orgs").Single(), "Authorization"), Is.EqualTo($"Bearer {session.AccessToken}"));
        });
    }

    [TestCase(401)]
    [TestCase(403)]
    [TestCase(404)]
    public async Task Orgs_upstream_error_status_is_passed_through(int status)
    {
        var client = await SignedInClientAsync();
        Keycloak.StubOrganizations(status);

        using var response = await client.Http.GetAsync("/bff/orgs");

        Assert.That((int)response.StatusCode, Is.EqualTo(status));
    }

    [Test]
    public async Task Orgs_require_a_session()
    {
        using var response = await AnonymousClient().Http.GetAsync("/bff/orgs");

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Redirect));
            Assert.That(Keycloak.RequestsTo("/orgs"), Is.Empty);
        });
    }

    [Test]
    public async Task Switch_requires_the_csrf_header()
    {
        var client = await SignedInClientAsync(NewSession(org: AcmeOrgClaim));

        using var response = await client.Http.PutAsJsonAsync("/bff/orgs/switch", new { organizationId = "globex-id" });

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
            Assert.That(Keycloak.RequestsTo("/users/switch-organization"), Is.Empty);
        });
    }

    [Test]
    public async Task Switch_updates_the_session_without_handing_tokens_to_the_browser()
    {
        var session = NewSession(org: AcmeOrgClaim, refreshToken: "refresh-1");
        var client = await SignedInClientAsync(session);
        var globexToken = Keycloak.CreateAccessToken("user-1", session.Sid,
            org: new Dictionary<string, object> { ["id"] = "globex-id", ["name"] = "globex", ["role"] = Array.Empty<string>() });
        // Phase Two answers the switch with a full token set; the BFF must not relay it.
        Keycloak.StubSwitchOrganization(200, new { access_token = "phase-two-token", refresh_token = "phase-two-refresh" });
        Keycloak.StubTokenRefresh(globexToken, "refresh-2");

        using var response = await client.SendWithCsrfAsync(HttpMethod.Put, "/bff/orgs/switch",
            JsonContent.Create(new { organizationId = "globex-id" }));
        var body = await response.Content.ReadAsStringAsync();

        var switchCall = Keycloak.RequestsTo("/users/switch-organization").Single();
        var me = await client.GetMeAsync();
        using (await client.Http.GetAsync("/api/orders")) { }

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
            Assert.That(body, Is.Empty);
            Assert.That(Header(switchCall, "Authorization"), Is.EqualTo($"Bearer {session.AccessToken}"));
            Assert.That(JsonDocument.Parse(switchCall.RequestMessage!.Body!).RootElement.GetProperty("id").GetString(), Is.EqualTo("globex-id"));
            Assert.That(me!.JsonClaim("org")?.GetProperty("name").GetString(), Is.EqualTo("globex"));
            Assert.That(me.Claims.Count(c => c.Type == "org"), Is.EqualTo(1));
            Assert.That(Header(BackendRequests.Single(), "Authorization"), Is.EqualTo($"Bearer {globexToken}"));
        });
    }

    [Test]
    public async Task Switch_rejected_by_phase_two_keeps_the_current_org()
    {
        var client = await SignedInClientAsync(NewSession(org: AcmeOrgClaim));
        Keycloak.StubSwitchOrganization(403);

        using var response = await client.SendWithCsrfAsync(HttpMethod.Put, "/bff/orgs/switch",
            JsonContent.Create(new { organizationId = "not-my-org" }));
        var me = await client.GetMeAsync();

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
            Assert.That(Keycloak.RequestsTo("/protocol/openid-connect/token"), Is.Empty);
            Assert.That(me!.JsonClaim("org")?.GetProperty("name").GetString(), Is.EqualTo("acme"));
        });
    }

    [Test]
    public async Task Switch_reports_a_failed_token_refresh()
    {
        var client = await SignedInClientAsync(NewSession(org: AcmeOrgClaim));
        Keycloak.StubSwitchOrganization(200);
        Keycloak.StubTokenEndpoint(400, new { error = "invalid_grant" });

        using var response = await client.SendWithCsrfAsync(HttpMethod.Put, "/bff/orgs/switch",
            JsonContent.Create(new { organizationId = "globex-id" }));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.InternalServerError));
    }
}
