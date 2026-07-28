using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Bff.Proxy;

public class KeycloakHealthCheck(IHttpClientFactory httpClientFactory, IOptions<KeycloakOptions> keycloakOptions) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var authority = keycloakOptions.Value.Authority;

        try
        {
            var client = httpClientFactory.CreateClient("KeycloakHealthClient");
            var response = await client.GetAsync($"{authority}/.well-known/openid-configuration", cancellationToken);

            return response.IsSuccessStatusCode
                ? HealthCheckResult.Healthy("Keycloak is reachable.")
                : HealthCheckResult.Degraded($"Keycloak responded with status code {(int)response.StatusCode}.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Keycloak is unreachable.", ex);
        }
    }
}

public class ProxiedBackendHealthCheck(IHttpClientFactory httpClientFactory, IConfiguration configuration) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var address = configuration["ReverseProxy:Clusters:dotnet-backend-cluster:Destinations:backend1:Address"];
        if (string.IsNullOrWhiteSpace(address))
        {
            return HealthCheckResult.Unhealthy("Proxied backend address is not configured.");
        }

        try
        {
            var client = httpClientFactory.CreateClient("ProxiedBackendHealthClient");
            var response = await client.GetAsync($"{address.TrimEnd('/')}/api/health", cancellationToken);

            return response.IsSuccessStatusCode
                ? HealthCheckResult.Healthy("Proxied backend is reachable.")
                : HealthCheckResult.Degraded($"Proxied backend responded with status code {(int)response.StatusCode}.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Proxied backend is unreachable.", ex);
        }
    }
}
