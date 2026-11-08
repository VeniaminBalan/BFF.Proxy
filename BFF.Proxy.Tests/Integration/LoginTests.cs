using System.Net;
using BFF.Proxy.Tests.Integration.Infrastructure;
using Microsoft.AspNetCore.WebUtilities;

namespace BFF.Proxy.Tests.Integration;

public class LoginTests : BffIntegrationTest
{
    [TestCase("https://evil.example/")]
    [TestCase("//evil.example")]
    [TestCase("javascript:alert(1)")]
    public async Task Disallowed_redirectUri_is_rejected_before_the_keycloak_round_trip(string redirectUri)
    {
        using var response = await AnonymousClient().Http.GetAsync($"/bff/login?redirectUri={Uri.EscapeDataString(redirectUri)}");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(response.Headers.Location, Is.Null);
            Assert.That(body, Does.Contain("Invalid redirectUri"));
            Assert.That(body, Does.Not.Contain("evil.example"), "the rejected value must not be reflected");
        });
    }

    [Test]
    public async Task Login_challenges_keycloak_with_the_authorization_code_flow()
    {
        using var response = await AnonymousClient().Http.GetAsync("/bff/login?redirectUri=/orders");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Redirect));
        var location = response.Headers.Location!;
        var query = QueryHelpers.ParseQuery(location.Query);

        Assert.Multiple(() =>
        {
            Assert.That(location.GetLeftPart(UriPartial.Path), Is.EqualTo(Keycloak.AuthorizationEndpoint));
            Assert.That(query["client_id"].ToString(), Is.EqualTo(FakeKeycloak.ClientId));
            Assert.That(query["response_type"].ToString(), Is.EqualTo("code"));
            Assert.That(query["redirect_uri"].ToString(), Is.EqualTo("https://localhost/signin-oidc"));
            Assert.That(query["scope"].ToString().Split(' '), Does.Contain("openid").And.Contain("profile"));
            Assert.That(query.ContainsKey("prompt"), Is.False);
            Assert.That(query.ContainsKey("state"), Is.True);
            Assert.That(query.ContainsKey("code_challenge"), Is.True, "PKCE");
        });
    }

    [Test]
    public async Task Login_never_requests_offline_access()
    {
        using var response = await AnonymousClient().Http.GetAsync("/bff/login");
        var query = QueryHelpers.ParseQuery(response.Headers.Location!.Query);

        Assert.That(query["scope"].ToString().Split(' '), Does.Not.Contain("offline_access"));
    }

    [TestCase("none")]
    [TestCase("select_account")]
    public async Task Prompt_is_passed_through_to_keycloak(string prompt)
    {
        using var response = await AnonymousClient().Http.GetAsync($"/bff/login?prompt={prompt}");
        var query = QueryHelpers.ParseQuery(response.Headers.Location!.Query);

        Assert.That(query["prompt"].ToString(), Is.EqualTo(prompt));
    }

    [Test]
    public async Task Allowed_spa_origin_is_accepted_as_redirectUri()
    {
        var redirectUri = Uri.EscapeDataString($"{BffFactory.SpaOrigin}/after-login");
        using var response = await AnonymousClient().Http.GetAsync($"/bff/login?redirectUri={redirectUri}");

        Assert.That(response.Headers.Location?.ToString(), Does.StartWith(Keycloak.AuthorizationEndpoint));
    }

    [Test]
    public async Task Already_signed_in_user_is_sent_straight_back_without_a_keycloak_round_trip()
    {
        var client = await SignedInClientAsync();

        using var response = await client.Http.GetAsync("/bff/login?redirectUri=/orders");

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Redirect));
            Assert.That(response.Headers.Location?.ToString(), Is.EqualTo("/orders"));
        });
    }

    [Test]
    public async Task Failed_silent_login_returns_to_the_spa_instead_of_an_error_page()
    {
        var client = AnonymousClient();
        using var challenge = await client.Http.GetAsync("/bff/login?prompt=none&redirectUri=/after");
        var state = QueryHelpers.ParseQuery(challenge.Headers.Location!.Query)["state"].ToString();

        // What Keycloak posts back (response_mode=form_post) when there is no SSO session.
        using var callback = await client.Http.PostAsync("/signin-oidc", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["error"] = "login_required",
            ["state"] = state
        }));

        Assert.Multiple(async () =>
        {
            Assert.That(callback.StatusCode, Is.EqualTo(HttpStatusCode.Redirect));
            Assert.That(callback.Headers.Location?.ToString(), Is.EqualTo("/after"));
            Assert.That(await client.GetMeAsync(), Is.Null);
        });
    }
}
