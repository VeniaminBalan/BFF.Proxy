using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace BFF.Proxy;

// Adds common Aspire services: service discovery, resilience, health checks, and OpenTelemetry.
// This project should be referenced by each service project in your solution.
// To learn more about using this project, see https://aka.ms/dotnet/aspire/service-defaults
public static class Extensions
{
    private const string HealthEndpointPath = "/health";
    private const string AlivenessEndpointPath = "/alive";

    public static TBuilder AddServiceDefaults<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.ConfigureOpenTelemetry();

        builder.AddDefaultHealthChecks();

        builder.Services.AddServiceDiscovery();

        builder.Services.ConfigureHttpClientDefaults(http =>
        {
            // Turn on resilience by default
            http.AddStandardResilienceHandler();

            // Turn on service discovery by default
            http.AddServiceDiscovery();
        });

        // Uncomment the following to restrict the allowed schemes for service discovery.
        // builder.Services.Configure<ServiceDiscoveryOptions>(options =>
        // {
        //     options.AllowedSchemes = ["https"];
        // });

        return builder;
    }

    public static TBuilder ConfigureOpenTelemetry<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });

        builder.Services.AddOpenTelemetry()
            .WithMetrics(metrics =>
            {
                metrics.AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation()
                    .AddMeter("Microsoft.AspNetCore.Hosting")
                    .AddMeter("Microsoft.AspNetCore.Server.Kestrel");
            })
            .WithTracing(tracing =>
            {
                tracing.AddSource(builder.Environment.ApplicationName)
                    .AddAspNetCoreInstrumentation(options =>
                        // Exclude health checks and Hangfire from tracing
                        options.Filter = context =>
                            !context.Request.Path.StartsWithSegments(HealthEndpointPath)
                            && !context.Request.Path.StartsWithSegments(AlivenessEndpointPath)
                    )
                    // Uncomment the following line to enable gRPC instrumentation (requires the OpenTelemetry.Instrumentation.GrpcNetClient package)
                    //.AddGrpcClientInstrumentation()
                    .AddHttpClientInstrumentation();
                tracing.AddSource("Yarp.ReverseProxy");
                    //.AddNpgsql();
            });
        
        builder.AddOpenTelemetryExporters();

        return builder;
    }

    private static TBuilder AddOpenTelemetryExporters<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        var useOtlpExporter = !string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);

        if (useOtlpExporter)
        {
            builder.Services.AddOpenTelemetry().UseOtlpExporter();
        }

        // Uncomment the following lines to enable the Azure Monitor exporter (requires the Azure.Monitor.OpenTelemetry.AspNetCore package)
        //if (!string.IsNullOrEmpty(builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]))
        //{
        //    builder.Services.AddOpenTelemetry()
        //       .UseAzureMonitor();
        //}

        return builder;
    }

    public static TBuilder AddDefaultHealthChecks<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.Services.AddHealthChecks()
            // Add a default liveness check to ensure app is responsive
            .AddCheck("self", () => HealthCheckResult.Healthy(), ["live"]);

        return builder;
    }

    public static WebApplication MapDefaultEndpoints(this WebApplication app)
    {
        // Adding health checks endpoints to applications in non-development environments has security implications.
        // See https://aka.ms/dotnet/aspire/healthchecks for details before enabling these endpoints in non-development environments.
        if (app.Environment.IsDevelopment())
        {
            // All health checks must pass for app to be considered ready to accept traffic after starting
            app.MapHealthChecks(HealthEndpointPath);

            // Only health checks tagged with the "live" tag must pass for app to be considered alive
            app.MapHealthChecks(AlivenessEndpointPath, new HealthCheckOptions
            {
                Predicate = r => r.Tags.Contains("live")
            });
        }

        return app;
    }

    // Path prefixes reserved for the BFF itself (proxied APIs, health checks). A subdirectory
    // matching one of these must never get a SPA fallback route, since its constrained catch-all
    // would take precedence over the corresponding YARP/health route and swallow those requests.
    private static readonly string[] ReservedSpaMountNames = ["bff", "api", "health", "alive"];

    // Auto-discovers additional SPAs mounted under wwwroot: any immediate subdirectory with its
    // own index.html (e.g. wwwroot/admin/index.html) gets a client-side-routing fallback scoped
    // to its own path prefix (/admin/**), instead of falling through to the root SPA's shell.
    // No-op if wwwroot doesn't exist or has no such subdirectories.
    public static WebApplication MapStaticSpaMounts(this WebApplication app)
    {
        if (!Directory.Exists(app.Environment.WebRootPath))
            return app;

        foreach (var dir in Directory.GetDirectories(app.Environment.WebRootPath))
        {
            if (!File.Exists(Path.Combine(dir, "index.html")))
                continue;

            var name = Path.GetFileName(dir);

            if (ReservedSpaMountNames.Contains(name, StringComparer.OrdinalIgnoreCase))
                continue;

            app.MapFallbackToFile($"/{name}/{{**slug:nonfile}}", $"{name}/index.html");
        }

        return app;
    }

    // Serves the root SPA shell for any unmatched non-file request, except under the reserved
    // prefixes (BFF endpoints, the reverse proxy, health checks). Those must 404 instead of
    // falling through to the SPA shell — otherwise an unmatched /bff/** or /api/** request (or
    // /health, /alive in Production, where MapDefaultEndpoints doesn't map them) would get a 200
    // with the SPA's index.html instead of a 404 or the intended handler's response.
    public static WebApplication MapSpaFallback(this WebApplication app, string filePath = "index.html")
    {
        app.MapFallback(async context =>
        {
            var path = context.Request.Path;

            if (ReservedSpaMountNames.Any(name => path.StartsWithSegments($"/{name}")))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            var fileInfo = app.Environment.WebRootFileProvider.GetFileInfo(filePath);
            if (!fileInfo.Exists)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            context.Response.ContentType = "text/html";
            await context.Response.SendFileAsync(fileInfo);
        });

        return app;
    }
}
