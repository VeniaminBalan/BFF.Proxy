using Microsoft.Extensions.Configuration;

namespace Bff.Proxy;

// Mirrors the "Keycloak" section shape used by AdminPortal/ToDo.API (realm, auth-server-url,
// resource, credentials.secret) so the same appsettings convention applies across services.
// Properties are `required`, so binding throws at startup if any value is missing from config.
public class KeycloakOptions
{
    public const string SectionName = "Keycloak";

    [ConfigurationKeyName("realm")]
    public required string Realm { get; set; }

    [ConfigurationKeyName("auth-server-url")]
    public required string AuthServerUrl { get; set; }

    [ConfigurationKeyName("resource")]
    public required string Resource { get; set; }

    public required KeycloakCredentialsOptions Credentials { get; set; }

    // The realm-scoped issuer URL, e.g. http://localhost:7080/realms/multitenant
    public string Authority => $"{AuthServerUrl.TrimEnd('/')}/realms/{Realm}";
}

public class KeycloakCredentialsOptions
{
    [ConfigurationKeyName("secret")]
    public required string Secret { get; set; }
}
