using System.Security.Cryptography;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using WireMock.Logging;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace BFF.Proxy.Tests.Integration.Infrastructure;

// Just enough of Keycloak (+ Phase Two) for the BFF: OIDC discovery, JWKS, the token endpoint and the
// organization endpoints. Tokens are signed with a local RSA key, so tests can also mint *invalid* tokens
// (wrong key/issuer/audience, expired, nonce...) - something a real Keycloak never produces.
public sealed class FakeKeycloak : IDisposable
{
    public const string Realm = "test";
    public const string ClientId = "bff-test";
    public const string ClientSecret = "bff-test-secret";
    public const string BackchannelLogoutEvent = "http://schemas.openid.net/event/backchannel-logout";

    private readonly List<RsaSecurityKey> _publishedKeys = [];

    public FakeKeycloak()
    {
        Server = WireMockServer.Start();
        SigningKey = CreateKey("key-1");
        _publishedKeys.Add(SigningKey);
        Reset();
    }

    public WireMockServer Server { get; }

    // Keycloak:auth-server-url
    public string BaseUrl => Server.Url!;
    public string Authority => $"{BaseUrl}/realms/{Realm}";
    public KeycloakConnection Connection => new(BaseUrl, Realm, ClientId, ClientSecret);
    public string AuthorizationEndpoint => $"{Authority}/protocol/openid-connect/auth";
    public string TokenEndpoint => $"{Authority}/protocol/openid-connect/token";
    public string EndSessionEndpoint => $"{Authority}/protocol/openid-connect/logout";

    public RsaSecurityKey SigningKey { get; private set; }

    public static RsaSecurityKey CreateKey(string keyId) => new(RSA.Create(2048)) { KeyId = keyId };

    // Clears per-test stubs and request logs, keeping discovery and the JWKS.
    public void Reset()
    {
        Server.Reset();
        Server.Given(Request.Create().WithPath($"/realms/{Realm}/.well-known/openid-configuration").UsingGet())
            .RespondWith(Response.Create().WithBodyAsJson(new
            {
                issuer = Authority,
                authorization_endpoint = AuthorizationEndpoint,
                token_endpoint = TokenEndpoint,
                userinfo_endpoint = $"{Authority}/protocol/openid-connect/userinfo",
                end_session_endpoint = EndSessionEndpoint,
                jwks_uri = $"{Authority}/protocol/openid-connect/certs",
                response_types_supported = new[] { "code" },
                subject_types_supported = new[] { "public" },
                id_token_signing_alg_values_supported = new[] { "RS256" }
            }));
        MapJwks();
    }

    // Keycloak rotated its signing key: the new key is published next to the old one and used from now on.
    public void RotateSigningKey(string keyId)
    {
        SigningKey = CreateKey(keyId);
        _publishedKeys.Add(SigningKey);
        MapJwks();
    }

    private void MapJwks()
    {
        var keys = _publishedKeys.Select(key =>
        {
            var parameters = key.Rsa.ExportParameters(false);
            return new
            {
                kty = "RSA",
                use = "sig",
                alg = "RS256",
                kid = key.KeyId,
                n = Base64UrlEncoder.Encode(parameters.Modulus),
                e = Base64UrlEncoder.Encode(parameters.Exponent)
            };
        }).ToArray();

        Server.Given(Request.Create().WithPath($"/realms/{Realm}/protocol/openid-connect/certs").UsingGet())
            .RespondWith(Response.Create().WithBodyAsJson(new { keys }));
    }

    public string CreateJwt(
        IDictionary<string, object> claims,
        RsaSecurityKey? signingKey = null,
        string? issuer = null,
        string? audience = ClientId,
        DateTime? expires = null)
    {
        var expiry = expires ?? DateTime.UtcNow.AddMinutes(5);
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = issuer ?? Authority,
            Audience = audience,
            IssuedAt = expiry.AddMinutes(-10),
            NotBefore = expiry.AddMinutes(-10),
            Expires = expiry,
            Claims = claims,
            SigningCredentials = new SigningCredentials(signingKey ?? SigningKey, SecurityAlgorithms.RsaSha256)
        };
        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    public string CreateAccessToken(string subject, string sid, object? org = null)
    {
        var claims = new Dictionary<string, object>
        {
            ["sub"] = subject,
            ["sid"] = sid,
            ["azp"] = ClientId,
            ["jti"] = Guid.NewGuid().ToString()
        };
        if (org is not null)
            claims["org"] = org;
        return CreateJwt(claims);
    }

    public static Dictionary<string, object> LogoutTokenClaims(string sid) => new()
    {
        ["sub"] = "user-1",
        ["sid"] = sid,
        ["jti"] = Guid.NewGuid().ToString(),
        ["events"] = new Dictionary<string, object> { [BackchannelLogoutEvent] = new Dictionary<string, object>() }
    };

    public void StubTokenEndpoint(int statusCode, object? body = null)
    {
        var response = Response.Create().WithStatusCode(statusCode);
        Server.Given(Request.Create().WithPath($"/realms/{Realm}/protocol/openid-connect/token").UsingPost())
            .RespondWith(body is null ? response : response.WithBodyAsJson(body));
    }

    public void StubTokenRefresh(string accessToken, string? refreshToken, int expiresIn = 300)
    {
        var body = new Dictionary<string, object>
        {
            ["access_token"] = accessToken,
            ["expires_in"] = expiresIn,
            ["token_type"] = "Bearer"
        };
        if (refreshToken is not null)
            body["refresh_token"] = refreshToken;
        StubTokenEndpoint(200, body);
    }

    public void StubOrganizations(int statusCode, string? json = null)
    {
        var response = Response.Create().WithStatusCode(statusCode);
        if (json is not null)
            response = response.WithHeader("Content-Type", "application/json").WithBody(json);
        Server.Given(Request.Create().WithPath($"/realms/{Realm}/orgs").UsingGet()).RespondWith(response);
    }

    public void StubSwitchOrganization(int statusCode, object? body = null)
    {
        var response = Response.Create().WithStatusCode(statusCode);
        Server.Given(Request.Create().WithPath($"/realms/{Realm}/users/switch-organization").UsingPut())
            .RespondWith(body is null ? response : response.WithBodyAsJson(body));
    }

    public IReadOnlyList<ILogEntry> RequestsTo(string pathSuffix) =>
        Server.LogEntries.Where(e => e.RequestMessage!.Path.EndsWith(pathSuffix, StringComparison.Ordinal)).ToList();

    public void Dispose()
    {
        Server.Stop();
        Server.Dispose();
    }
}
