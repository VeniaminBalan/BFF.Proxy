using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Bff.Proxy.Endpoints;

public static class HealthEndpoint
{
    public static void MapHealthEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/bff/health", async (HttpContext context, CachedHealthReport healthReport) =>
            {
                var report = await healthReport.GetAsync(context.RequestAborted);

                // Anonymous endpoint: only names and statuses. Descriptions and exception messages can carry
                // internal host names, ports and connection details; HealthCheckService already logs them
                // (Warning for Degraded, Error with the exception for Unhealthy).
                var response = new
                {
                    status = report.Status.ToString(),
                    totalDurationMs = report.TotalDuration.TotalMilliseconds,
                    checks = report.Entries.Select(e => new
                    {
                        name = e.Key,
                        status = e.Value.Status.ToString(),
                        durationMs = e.Value.Duration.TotalMilliseconds
                    })
                };

                return report.Status == HealthStatus.Unhealthy
                    ? Results.Json(response, statusCode: StatusCodes.Status503ServiceUnavailable)
                    : Results.Ok(response);
            })
            .AllowAnonymous();
    }
}
