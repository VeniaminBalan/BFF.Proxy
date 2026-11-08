using Bff.Proxy;
using Microsoft.Extensions.Configuration;

namespace BFF.Proxy.Tests.Unit;

[Category("Unit")]
public class KeycloakOptionsTests
{
    [TestCase("https://auth.example.com", "acme", "https://auth.example.com/realms/acme")]
    [TestCase("https://auth.example.com/", "acme", "https://auth.example.com/realms/acme")]
    [TestCase("http://localhost:8080//", "dev", "http://localhost:8080/realms/dev")]
    public void Authority_is_the_realm_scoped_issuer(string authServerUrl, string realm, string expected)
    {
        var options = new KeycloakOptions
        {
            AuthServerUrl = authServerUrl,
            Realm = realm,
            Resource = "client",
            Credentials = new KeycloakCredentialsOptions { Secret = "secret" }
        };

        Assert.That(options.Authority, Is.EqualTo(expected));
    }

    [Test]
    public void Binds_the_keycloak_json_adapter_key_names()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Keycloak:realm"] = "acme",
                ["Keycloak:auth-server-url"] = "https://auth.example.com/",
                ["Keycloak:resource"] = "bff",
                ["Keycloak:credentials:secret"] = "s3cret"
            })
            .Build();

        var options = configuration.GetSection(KeycloakOptions.SectionName).Get<KeycloakOptions>()!;

        Assert.Multiple(() =>
        {
            Assert.That(options.Realm, Is.EqualTo("acme"));
            Assert.That(options.AuthServerUrl, Is.EqualTo("https://auth.example.com/"));
            Assert.That(options.Resource, Is.EqualTo("bff"));
            Assert.That(options.Credentials.Secret, Is.EqualTo("s3cret"));
        });
    }
}
