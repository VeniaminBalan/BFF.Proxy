using System.Net;
using BFF.Proxy.Tests.Integration.Infrastructure;
using Microsoft.AspNetCore.WebUtilities;

namespace BFF.Proxy.Tests.Integration;

// OnValidatePrincipal refreshes the access token once it is within 60s of expiry.
public class TokenRefreshTests : BffIntegrationTest
{
    private const string TokenPath = "/protocol/openid-connect/token";

    private Dictionary<string, string> TokenRequest(int index) =>
        QueryHelpers.ParseQuery(Keycloak.RequestsTo(TokenPath)[index].RequestMessage!.Body)
            .ToDictionary(kv => kv.Key, kv => kv.Value.ToString());

    [Test]
    public async Task Token_is_not_refreshed_while_it_is_still_valid()
    {
        var client = await SignedInClientAsync(NewSession(expiresAt: DateTimeOffset.UtcNow.AddMinutes(5)));

        using (await client.Http.GetAsync("/api/orders")) { }

        Assert.That(Keycloak.RequestsTo(TokenPath), Is.Empty);
    }

    [Test]
    public async Task Token_close_to_expiry_is_refreshed_and_the_new_token_is_forwarded_and_kept()
    {
        var client = await SignedInClientAsync(NewSession(expiresAt: DateTimeOffset.UtcNow.AddSeconds(30), refreshToken: "refresh-1"));
        var newAccessToken = Keycloak.CreateAccessToken("user-1", "sid-x");
        Keycloak.StubTokenRefresh(newAccessToken, "refresh-2", expiresIn: 300);

        using (await client.Http.GetAsync("/api/orders")) { }
        using (await client.Http.GetAsync("/api/orders")) { }

        var tokenRequest = TokenRequest(0);
        Assert.Multiple(() =>
        {
            Assert.That(tokenRequest["grant_type"], Is.EqualTo("refresh_token"));
            Assert.That(tokenRequest["refresh_token"], Is.EqualTo("refresh-1"));
            Assert.That(tokenRequest["client_id"], Is.EqualTo(FakeKeycloak.ClientId));
            Assert.That(tokenRequest["client_secret"], Is.EqualTo(FakeKeycloak.ClientSecret));
            Assert.That(Keycloak.RequestsTo(TokenPath), Has.Count.EqualTo(1), "the renewed session should not refresh again");
            Assert.That(BackendRequests.Select(r => Header(r, "Authorization")),
                Is.All.EqualTo($"Bearer {newAccessToken}"));
        });
    }

    [Test]
    public async Task Rotated_refresh_token_is_used_for_the_next_refresh()
    {
        var client = await SignedInClientAsync(NewSession(expiresAt: DateTimeOffset.UtcNow.AddSeconds(30), refreshToken: "refresh-1"));
        // expires_in 30 keeps the token inside the refresh window, so every request refreshes.
        Keycloak.StubTokenRefresh(Keycloak.CreateAccessToken("user-1", "sid-x"), "refresh-2", expiresIn: 30);

        using (await client.Http.GetAsync("/api/orders")) { }
        using (await client.Http.GetAsync("/api/orders")) { }

        Assert.That(TokenRequest(1)["refresh_token"], Is.EqualTo("refresh-2"));
    }

    [Test]
    public async Task Refresh_response_without_refresh_token_keeps_the_previous_one()
    {
        var client = await SignedInClientAsync(NewSession(expiresAt: DateTimeOffset.UtcNow.AddSeconds(30), refreshToken: "refresh-1"));
        Keycloak.StubTokenRefresh(Keycloak.CreateAccessToken("user-1", "sid-x"), refreshToken: null, expiresIn: 30);

        using (await client.Http.GetAsync("/api/orders")) { }
        using (await client.Http.GetAsync("/api/orders")) { }

        Assert.That(TokenRequest(1)["refresh_token"], Is.EqualTo("refresh-1"));
    }

    [Test]
    public async Task Failed_refresh_signs_the_user_out_instead_of_forwarding_a_stale_token()
    {
        var client = await SignedInClientAsync(NewSession(expiresAt: DateTimeOffset.UtcNow.AddSeconds(30)));
        Keycloak.StubTokenEndpoint(400, new { error = "invalid_grant", error_description = "Session not active" });

        using var apiResponse = await client.Http.GetAsync("/api/orders");

        Assert.Multiple(async () =>
        {
            Assert.That(apiResponse.StatusCode, Is.Not.EqualTo(HttpStatusCode.OK));
            Assert.That(BackendRequests, Is.Empty);
            Assert.That(await client.GetMeAsync(), Is.Null);
        });
    }

    [Test]
    public async Task Expiring_session_without_refresh_token_is_signed_out()
    {
        var client = await SignedInClientAsync(NewSession(expiresAt: DateTimeOffset.UtcNow.AddSeconds(30), refreshToken: null));

        Assert.Multiple(async () =>
        {
            Assert.That(await client.GetMeAsync(), Is.Null);
            Assert.That(Keycloak.RequestsTo(TokenPath), Is.Empty);
        });
    }
}
