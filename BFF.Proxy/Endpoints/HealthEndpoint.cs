using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Bff.Proxy.Endpoints;

public static class HealthEndpoint
{
    public static void MapHealthEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/bff/health", async (HealthCheckService healthCheckService) =>
            {
                var report = await healthCheckService.CheckHealthAsync(check => check.Tags.Contains("ready"));

                var response = new
                {
                    status = report.Status.ToString(),
                    totalDurationMs = report.TotalDuration.TotalMilliseconds,
                    checks = report.Entries.Select(e => new
                    {
                        name = e.Key,
                        status = e.Value.Status.ToString(),
                        description = e.Value.Description,
                        durationMs = e.Value.Duration.TotalMilliseconds,
                        error = e.Value.Exception?.Message
                    })
                };

                return report.Status == HealthStatus.Unhealthy
                    ? Results.Json(response, statusCode: StatusCodes.Status503ServiceUnavailable)
                    : Results.Ok(response);
            })
            .AllowAnonymous();
    }
}
