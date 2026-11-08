using System.Net;
using System.Text;
using BFF.Proxy.Tests.Integration.Infrastructure;
using Microsoft.IdentityModel.Tokens;

namespace BFF.Proxy.Tests.Integration;

public class BackchannelLogoutTests : BffIntegrationTest
{
    private async Task<HttpResponseMessage> PostLogoutTokenAsync(string logoutToken) =>
        await AnonymousClient().Http.PostAsync("/bff/logout/backchannel",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["logout_token"] = logoutToken }));

    [Test]
    public async Task Valid_logout_token_ends_only_the_matching_session()
    {
        var target = await SignedInClientAsync(NewSession(sid: "sid-target"));
        var bystander = await SignedInClientAsync(NewSession(sid: "sid-bystander"));

        using var response = await PostLogoutTokenAsync(Keycloak.CreateJwt(FakeKeycloak.LogoutTokenClaims("sid-target")));

        Assert.Multiple(async () =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(await target.GetMeAsync(), Is.Null);
            Assert.That(await bystander.GetMeAsync(), Is.Not.Null);
        });
    }

    [Test]
    public async Task Valid_logout_token_for_an_unknown_session_is_acknowledged()
    {
        using var response = await PostLogoutTokenAsync(Keycloak.CreateJwt(FakeKeycloak.LogoutTokenClaims("sid-nobody")));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public async Task Missing_logout_token_is_rejected()
    {
        using var response = await AnonymousClient().Http.PostAsync("/bff/logout/backchannel",
            new FormUrlEncodedContent(new Dictionary<string, string>()));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task Non_form_body_is_rejected()
    {
        using var response = await AnonymousClient().Http.PostAsJsonAsync("/bff/logout/backchannel", new { logout_token = "x" });

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    private static IEnumerable<TestCaseData> ForgedTokens()
    {
        TestCaseData Case(string name, Func<FakeKeycloak, string, string> forge) =>
            new TestCaseData(forge).SetName($"Rejects logout token: {name}");

        yield return Case("signed with an unknown key",
            (kc, sid) => kc.CreateJwt(FakeKeycloak.LogoutTokenClaims(sid), signingKey: FakeKeycloak.CreateKey("rogue")));
        yield return Case("signed with another key under a published kid",
            (kc, sid) => kc.CreateJwt(FakeKeycloak.LogoutTokenClaims(sid), signingKey: FakeKeycloak.CreateKey(kc.SigningKey.KeyId)));
        yield return Case("unsigned (alg none)",
            (_, sid) => Unsigned(FakeKeycloak.LogoutTokenClaims(sid)));
        yield return Case("audience is another client",
            (kc, sid) => kc.CreateJwt(FakeKeycloak.LogoutTokenClaims(sid), audience: "another-client"));
        yield return Case("no audience",
            (kc, sid) => kc.CreateJwt(FakeKeycloak.LogoutTokenClaims(sid), audience: null));
        yield return Case("issued by another realm",
            (kc, sid) => kc.CreateJwt(FakeKeycloak.LogoutTokenClaims(sid), issuer: kc.Authority.Replace("/realms/test", "/realms/other")));
        yield return Case("expired",
            (kc, sid) => kc.CreateJwt(FakeKeycloak.LogoutTokenClaims(sid), expires: DateTime.UtcNow.AddMinutes(-10)));
        yield return Case("contains a nonce", (kc, sid) =>
        {
            var claims = FakeKeycloak.LogoutTokenClaims(sid);
            claims["nonce"] = "n-123";
            return kc.CreateJwt(claims);
        });
        yield return Case("no events claim", (kc, sid) =>
        {
            var claims = FakeKeycloak.LogoutTokenClaims(sid);
            claims.Remove("events");
            return kc.CreateJwt(claims);
        });
        yield return Case("events without the back-channel logout event", (kc, sid) =>
        {
            var claims = FakeKeycloak.LogoutTokenClaims(sid);
            claims["events"] = new Dictionary<string, object> { ["http://schemas.openid.net/event/other"] = new Dictionary<string, object>() };
            return kc.CreateJwt(claims);
        });
        yield return Case("events as a plain string containing the event URI", (kc, sid) =>
        {
            var claims = FakeKeycloak.LogoutTokenClaims(sid);
            claims["events"] = FakeKeycloak.BackchannelLogoutEvent;
            return kc.CreateJwt(claims);
        });
        yield return Case("no sid", (kc, sid) =>
        {
            var claims = FakeKeycloak.LogoutTokenClaims(sid);
            claims.Remove("sid");
            return kc.CreateJwt(claims);
        });
        yield return Case("not a JWT", (_, _) => "not-a-jwt");
    }

    private static string Unsigned(Dictionary<string, object> claims)
    {
        claims["iss"] = "ignored";
        var header = Base64UrlEncoder.Encode("""{"alg":"none","typ":"JWT"}""");
        var payload = Base64UrlEncoder.Encode(Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(claims)));
        return $"{header}.{payload}.";
    }

    [TestCaseSource(nameof(ForgedTokens))]
    public async Task Forged_or_invalid_logout_token_is_rejected_and_the_session_survives(Func<FakeKeycloak, string, string> forge)
    {
        var client = await SignedInClientAsync(NewSession(sid: "sid-victim"));

        using var response = await PostLogoutTokenAsync(forge(Keycloak, "sid-victim"));

        Assert.Multiple(async () =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(await client.GetMeAsync(), Is.Not.Null, "the session must survive a rejected logout token");
        });
    }
}

// Separate fixture: rotating the fake Keycloak's key must not leak into the other back-channel tests.
public class BackchannelLogoutKeyRotationTests : BffIntegrationTest
{
    [Test]
    public async Task Logout_token_signed_with_a_newly_rotated_key_is_accepted()
    {
        var client = AnonymousClient();
        // Warm the BFF's JWKS cache with the original key.
        using (var warmUp = await client.Http.PostAsync("/bff/logout/backchannel", new FormUrlEncodedContent(
                   new Dictionary<string, string> { ["logout_token"] = Keycloak.CreateJwt(FakeKeycloak.LogoutTokenClaims("sid-warm-up")) })))
        {
            Assert.That(warmUp.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        }

        var victim = await SignedInClientAsync(NewSession(sid: "sid-rotated"));
        Keycloak.RotateSigningKey("key-2");

        using var response = await client.Http.PostAsync("/bff/logout/backchannel", new FormUrlEncodedContent(
            new Dictionary<string, string> { ["logout_token"] = Keycloak.CreateJwt(FakeKeycloak.LogoutTokenClaims("sid-rotated")) }));

        Assert.Multiple(async () =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(await victim.GetMeAsync(), Is.Null);
        });
    }
}
