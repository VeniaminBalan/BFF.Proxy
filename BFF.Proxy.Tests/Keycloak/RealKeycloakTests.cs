using System.Net;
using System.Text.Json;
using Bff.Proxy.Csrf;
using BFF.Proxy.Tests.Integration.Infrastructure;
using Microsoft.IdentityModel.JsonWebTokens;
using WireMock.Logging;

namespace BFF.Proxy.Tests.Keycloak;

// End-to-end against a real (Phase Two) Keycloak: the full authorization code flow, token refresh, SSO,
// RP-initiated and back-channel logout, and organizations. See KeycloakEnvironment for setup / live mode.
[Category("Keycloak")]
[NonParallelizable]
public class RealKeycloakTests
{
    private readonly List<TestBrowser> _browsers = [];

    private static KeycloakEnvironment Env => KeycloakEnvironment.Current;
    private static Uri Bff => Env.BffBaseUrl;

    [SetUp]
    public void ResetBackendLog() => Env.Backend.ResetLogEntries();

    [TearDown]
    public void DisposeBrowsers()
    {
        foreach (var browser in _browsers)
            browser.Dispose();
        _browsers.Clear();
    }

    private TestBrowser NewBrowser()
    {
        var browser = new TestBrowser();
        _browsers.Add(browser);
        return browser;
    }

    private static void RequireContainer()
    {
        if (Env.IsLive)
            Assert.Ignore("Needs the admin API / known users and orgs of the test container.");
    }

    // /bff/login -> Keycloak login page -> credentials -> form_post back to /signin-oidc -> redirectUri.
    private static async Task<TestBrowser.BrowserPage> LoginAsync(TestBrowser browser, TestUser user, string redirectUri = "/")
    {
        var loginPage = await browser.NavigateAsync(new Uri(Bff, $"/bff/login?redirectUri={Uri.EscapeDataString(redirectUri)}").ToString());
        var form = loginPage.LoginForm ?? throw new AssertionException($"Expected the Keycloak login form, got {loginPage}");

        return await browser.SubmitFormAsync(loginPage, form, new Dictionary<string, string>
        {
            ["username"] = user.Username,
            ["password"] = user.Password
        });
    }

    private async Task<(TestBrowser Browser, MeResponse Me)> SignedInAsync(TestUser? user = null)
    {
        var browser = NewBrowser();
        var landing = await LoginAsync(browser, user ?? Env.UserWithOrganizations);
        Assert.That(landing.Url.GetLeftPart(UriPartial.Authority), Is.EqualTo(Bff.GetLeftPart(UriPartial.Authority)),
            $"login did not return to the BFF: {landing}");

        var me = await browser.GetMeAsync(Bff);
        Assert.That(me, Is.Not.Null, "no BFF session after login");
        return (browser, me!);
    }

    private static async Task<HttpResponseMessage> SendWithCsrfAsync(TestBrowser browser, HttpMethod method, string path, HttpContent? content = null)
    {
        var csrf = (await browser.GetMeAsync(Bff))!.CsrfToken!;
        var request = new HttpRequestMessage(method, new Uri(Bff, path)) { Content = content };
        request.Headers.Add(CsrfConstants.HeaderName, csrf);
        return await browser.SendAsync(request);
    }

    private static async Task<JsonWebToken> ForwardedAccessTokenAsync(TestBrowser browser)
    {
        using var response = await browser.SendAsync(new HttpRequestMessage(HttpMethod.Get, new Uri(Bff, "/api/data")));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), "proxied API call");
        ILogEntry forwarded = Env.Backend.LogEntries.Last();
        var authorization = forwarded.RequestMessage!.Headers!["Authorization"].Single();
        Assert.That(authorization, Does.StartWith("Bearer "));
        return new JsonWebToken(authorization["Bearer ".Length..]);
    }

    private static string? OrgName(string? orgClaimJson) =>
        orgClaimJson is null ? null : JsonDocument.Parse(orgClaimJson).RootElement.GetProperty("name").GetString();

    [Test]
    public async Task Login_establishes_a_bff_session_with_keycloak_claims()
    {
        var browser = NewBrowser();

        var landing = await LoginAsync(browser, Env.UserWithOrganizations, "/welcome");
        var me = await browser.GetMeAsync(Bff);

        Assert.That(me, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(landing.Url.AbsolutePath, Is.EqualTo("/welcome"));
            Assert.That(me!.Claim("preferred_username"), Is.EqualTo(Env.UserWithOrganizations.Username));
            // RedisTicketStore indexes sessions by this exact claim type; back-channel logout depends on it.
            Assert.That(me.Claim("sid"), Is.Not.Null.And.Not.Empty);
            Assert.That(me.CsrfToken, Is.Not.Null.And.Not.Empty);
        });
    }

    [Test]
    public async Task Wrong_password_does_not_create_a_session()
    {
        var browser = NewBrowser();

        var page = await LoginAsync(browser, Env.UserWithOrganizations with { Password = "wrong-password" });

        Assert.Multiple(async () =>
        {
            Assert.That(page.LoginForm, Is.Not.Null, "Keycloak should show the login form again");
            Assert.That(await browser.GetMeAsync(Bff), Is.Null);
        });
    }

    [Test]
    public async Task Api_calls_carry_the_keycloak_access_token_and_no_cookies()
    {
        var (browser, _) = await SignedInAsync();

        var token = await ForwardedAccessTokenAsync(browser);

        Assert.Multiple(() =>
        {
            Assert.That(token.Issuer, Is.EqualTo(Env.Connection.Authority));
            Assert.That(token.GetClaim("azp").Value, Is.EqualTo(Env.Connection.ClientId));
            Assert.That(token.GetClaim("preferred_username").Value, Is.EqualTo(Env.UserWithOrganizations.Username));
            Assert.That(Env.Backend.LogEntries.Last().RequestMessage!.Headers!.ContainsKey("Cookie"), Is.False);
        });
    }

    // The test client's access tokens live 65s (realm file); the BFF refreshes once fewer than 60s remain.
    [Test]
    public async Task Access_token_is_refreshed_before_it_expires()
    {
        RequireContainer();
        var (browser, _) = await SignedInAsync();

        var first = await ForwardedAccessTokenAsync(browser);
        await Task.Delay(TimeSpan.FromSeconds(6));
        var refreshed = await ForwardedAccessTokenAsync(browser);
        var next = await ForwardedAccessTokenAsync(browser);

        Assert.Multiple(() =>
        {
            Assert.That(refreshed.EncodedToken, Is.Not.EqualTo(first.EncodedToken), "expected a refreshed token");
            Assert.That(refreshed.ValidTo, Is.GreaterThan(first.ValidTo));
            Assert.That(refreshed.Subject, Is.EqualTo(first.Subject));
            Assert.That(next.EncodedToken, Is.EqualTo(refreshed.EncodedToken), "the refreshed token should be kept in the session");
        });
    }

    [Test]
    public async Task Silent_login_without_a_keycloak_session_returns_to_the_spa_unauthenticated()
    {
        var browser = NewBrowser();

        var page = await browser.NavigateAsync(new Uri(Bff, "/bff/login?prompt=none&redirectUri=/after").ToString());

        Assert.Multiple(async () =>
        {
            Assert.That(page.Url.GetLeftPart(UriPartial.Path), Is.EqualTo(new Uri(Bff, "/after").ToString()));
            Assert.That(await browser.GetMeAsync(Bff), Is.Null);
        });
    }

    [Test]
    public async Task Silent_login_with_a_keycloak_session_signs_in_without_a_password()
    {
        var (browser, before) = await SignedInAsync();
        browser.ClearCookies(Bff); // the BFF session is gone, the Keycloak SSO session is not

        var page = await browser.NavigateAsync(new Uri(Bff, "/bff/login?prompt=none&redirectUri=/after").ToString());
        var me = await browser.GetMeAsync(Bff);

        Assert.Multiple(() =>
        {
            Assert.That(page.Url.AbsolutePath, Is.EqualTo("/after"));
            Assert.That(me?.Claim("preferred_username"), Is.EqualTo(Env.UserWithOrganizations.Username));
            Assert.That(me?.Claim("sid"), Is.EqualTo(before.Claim("sid")), "same Keycloak SSO session");
        });
    }

    [Test]
    public async Task Logout_ends_the_bff_and_the_keycloak_session()
    {
        var (browser, me) = await SignedInAsync();
        var csrf = me.CsrfToken!;

        var page = await browser.NavigateAsync(new HttpRequestMessage(HttpMethod.Post, new Uri(Bff, "/bff/logout"))
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                [CsrfConstants.FormFieldName] = csrf,
                ["redirectUri"] = "/bye"
            })
        });

        Assert.Multiple(async () =>
        {
            Assert.That(page.Url.AbsolutePath, Is.EqualTo("/bye"), $"ended at {page}");
            Assert.That(await browser.GetMeAsync(Bff), Is.Null);
        });

        if (Env.Admin is not null)
        {
            var userId = await Env.Admin.GetUserIdAsync(Env.UserWithOrganizations.Username);
            Assert.That(await Env.Admin.GetUserSessionIdsAsync(userId), Does.Not.Contain(me.Claim("sid")));
        }
    }

    [Test]
    public async Task Keycloak_side_logout_reaches_the_bff_through_backchannel_logout()
    {
        RequireContainer();
        var (browser, me) = await SignedInAsync();

        await Env.Admin!.DeleteSessionAsync(me.Claim("sid")!);

        MeResponse? after = me;
        for (var attempt = 0; attempt < 20 && after is not null; attempt++)
        {
            await Task.Delay(250);
            after = await browser.GetMeAsync(Bff);
        }
        Assert.That(after, Is.Null, "the BFF session should end when Keycloak's session is deleted");
    }

    [Test]
    public async Task Organizations_can_be_listed_and_switched()
    {
        var (browser, me) = await SignedInAsync();

        using var orgsResponse = await browser.SendAsync(new HttpRequestMessage(HttpMethod.Get, new Uri(Bff, "/bff/orgs")));
        Assert.That(orgsResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var orgs = await orgsResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.That(orgs.ValueKind, Is.EqualTo(JsonValueKind.Array));

        if (Env.IsLive)
            return; // the live realm's orgs are unknown; listing them is all we check

        Assert.That(orgs.EnumerateArray().Select(o => o.GetProperty("name").GetString()), Is.EquivalentTo(new[] { "acme", "globex" }));
        var current = OrgName(me.Claim("org"));
        var target = current == "acme" ? "globex" : "acme";

        try
        {
            using var switchResponse = await SendWithCsrfAsync(browser, HttpMethod.Put, "/bff/orgs/switch",
                JsonContent.Create(new { organizationId = Env.OrganizationIds[target] }));
            var afterSwitch = await browser.GetMeAsync(Bff);
            var token = await ForwardedAccessTokenAsync(browser);

            Assert.Multiple(async () =>
            {
                Assert.That(switchResponse.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
                Assert.That(await switchResponse.Content.ReadAsStringAsync(), Is.Empty);
                Assert.That(OrgName(afterSwitch?.Claim("org")), Is.EqualTo(target));
                Assert.That(OrgName(token.GetClaim("org").Value), Is.EqualTo(target));
            });
        }
        finally
        {
            // The active org is stored on the Keycloak user; put it back for the other tests.
            using (await SendWithCsrfAsync(browser, HttpMethod.Put, "/bff/orgs/switch",
                       JsonContent.Create(new { organizationId = Env.OrganizationIds[current ?? "acme"] }))) { }
        }
    }

    [Test]
    public async Task User_without_organizations_has_an_empty_org_claim_and_an_empty_list()
    {
        RequireContainer();
        var (browser, me) = await SignedInAsync(Env.UserWithoutOrganizations);

        using var orgsResponse = await browser.SendAsync(new HttpRequestMessage(HttpMethod.Get, new Uri(Bff, "/bff/orgs")));

        Assert.Multiple(async () =>
        {
            // Phase Two's active-organization mapper emits an empty object rather than omitting the claim.
            Assert.That(me.Claim("org"), Is.EqualTo("{}"));
            Assert.That(orgsResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(await orgsResponse.Content.ReadAsStringAsync(), Is.EqualTo("[]"));
        });
    }

    [Test]
    public async Task Health_reports_keycloak_reachable()
    {
        using var browser = new TestBrowser();
        using var response = await browser.SendAsync(new HttpRequestMessage(HttpMethod.Get, new Uri(Bff, "/bff/health")));
        var health = await response.Content.ReadFromJsonAsync<JsonElement>();

        var keycloak = health.GetProperty("checks").EnumerateArray().Single(c => c.GetProperty("name").GetString() == "keycloak");
        Assert.That(keycloak.GetProperty("status").GetString(), Is.EqualTo("Healthy"));
    }
}
