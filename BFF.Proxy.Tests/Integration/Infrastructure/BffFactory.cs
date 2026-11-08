using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace BFF.Proxy.Tests.Integration.Infrastructure;

// The BFF's "Keycloak" config section.
public sealed record KeycloakConnection(string AuthServerUrl, string Realm, string ClientId, string ClientSecret)
{
    public string Authority => $"{AuthServerUrl.TrimEnd('/')}/realms/{Realm}";
}

// Hosts the real Program in-process. Every Keycloak/backend setting is overridden, so nothing in
// appsettings.{Environment}.json (e.g. a developer's real Keycloak) is ever contacted.
public sealed class BffFactory(
    KeycloakConnection keycloak,
    string backendUrl,
    string environment = "Development",
    IReadOnlyDictionary<string, string?>? overrides = null) : WebApplicationFactory<Program>
{
    public const string SpaOrigin = "https://spa.example.test";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(environment);

        var settings = new Dictionary<string, string?>
        {
            ["Keycloak:realm"] = keycloak.Realm,
            ["Keycloak:auth-server-url"] = keycloak.AuthServerUrl,
            ["Keycloak:resource"] = keycloak.ClientId,
            ["Keycloak:credentials:secret"] = keycloak.ClientSecret,
            ["ConnectionStrings:Redis"] = "",
            ["ReverseProxy:BackendAddress"] = backendUrl,
            ["Cors:AllowedOrigins:0"] = SpaOrigin,
            ["OTEL_EXPORTER_OTLP_ENDPOINT"] = ""
        };
        foreach (var (key, value) in overrides ?? new Dictionary<string, string?>())
            settings[key] = value;

        foreach (var (key, value) in settings)
            builder.UseSetting(key, value);

        builder.ConfigureServices(services => services.AddSingleton<IStartupFilter, TestSignInStartupFilter>());
    }

    // TestServer serves any host; https so cookies behave as they would behind TLS.
    public BffClient CreateBffClient(string baseAddress = "https://localhost") => new(this, new Uri(baseAddress));
}
