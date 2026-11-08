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
        var address = configuration["ReverseProxy:BackendAddress"];
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


// /bff/health is anonymous, and every check run makes outbound calls (Keycloak, Redis, the proxied backend).
// Reusing one report for a few seconds keeps a flood of anonymous probes from turning into a flood of
// outbound requests; concurrent callers share a single in-flight run.
public sealed class CachedHealthReport(HealthCheckService healthCheckService)
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(5);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private HealthReport? _report;
    private DateTimeOffset _createdAt;

    public async Task<HealthReport> GetAsync(CancellationToken cancellationToken)
    {
        if (TryGetFresh(out var report))
            return report;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (TryGetFresh(out report))
                return report;

            report = await healthCheckService.CheckHealthAsync(check => check.Tags.Contains("ready"), cancellationToken);
            _report = report;
            _createdAt = DateTimeOffset.UtcNow;
            return report;
        }
        finally
        {
            _gate.Release();
        }
    }

    private bool TryGetFresh(out HealthReport report)
    {
        report = _report!;
        return _report is not null && DateTimeOffset.UtcNow - _createdAt < Ttl;
    }
}
