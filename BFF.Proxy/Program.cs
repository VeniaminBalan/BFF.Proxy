using Bff.Proxy;
using BFF.Proxy;
using Bff.Proxy.Csrf;
using Bff.Proxy.Endpoints;
using Serilog;
using Serilog.Formatting.Compact;

var builder = WebApplication.CreateBuilder(args);

// Human-readable console in Development, single-line JSON elsewhere. writeToProviders keeps the
// other logging providers alive, notably the OpenTelemetry one (builder.Logging.AddOpenTelemetry)
// that exports logs over OTLP; without it Serilog replaces them and OTLP log export silently stops.
// That flag also forwards to the default console provider (duplicate, non-Serilog lines), so drop
// the defaults first; the OpenTelemetry provider is added afterwards in AddServiceDefaults.
builder.Logging.ClearProviders();
builder.Host.UseSerilog((ctx, services, cfg) =>
{
    cfg.ReadFrom.Configuration(ctx.Configuration)
        .Enrich.FromLogContext();

    if (ctx.HostingEnvironment.IsDevelopment())
        cfg.WriteTo.Console();
    else
        cfg.WriteTo.Console(new CompactJsonFormatter());
}, writeToProviders: true);

builder.AddServiceDefaults();
var cacheMode = await builder.AddBffServicesAsync();

// Opt-in reverse proxy support (ForwardedHeaders:* config); ignored when the BFF is exposed directly
builder.Services.AddConfiguredForwardedHeaders(builder.Configuration);

var app = builder.Build();

// Forwarded headers FIRST (only when enabled) so the scheme/host seen behind Traefik are correct
// (OIDC redirect URIs, secure cookies) and YARP forwards the original X-Forwarded-Proto to the APIs
app.UseConfiguredForwardedHeaders(app.Configuration);

if (!app.Environment.IsProduction())
{
    app.Logger.LogWarning(
        "Running in {Environment} mode - this configuration is NOT secure and must not be used in production",
        app.Environment.EnvironmentName);
}

if (cacheMode == SessionCacheMode.InMemory)
{
    if (app.Environment.IsDevelopment())
    {
        app.Logger.LogInformation(
            "ConnectionStrings:Redis is not configured - using an in-process in-memory session cache");
    }
    else
    {
        app.Logger.LogWarning(
            "ConnectionStrings:Redis is not configured - falling back to an in-process in-memory session cache. " +
            "This does not share session state across instances and back-channel logout will only affect this instance's sessions");
    }
}

app.MapDefaultEndpoints();

app.UseWhen(
    ctx => !ctx.Request.Path.StartsWithSegments("/bff")
        && !ctx.Request.Path.StartsWithSegments("/api")
        && !ctx.Request.Path.StartsWithSegments("/health")
        && !ctx.Request.Path.StartsWithSegments("/alive"),
    branch => branch.UseStaticFiles());

app.UseRouting();
app.UseCors("BffClient");
app.UseAuthentication();
app.UseAuthorization();

app.UseMiddleware<CsrfCookieMiddleware>();
app.UseMiddleware<CsrfValidationMiddleware>();

app.MapOrgsEndpoint();
app.MapOrgSwitchEndpoint();
app.MapLoginEndpoint();
app.MapMeEndpoint();
app.MapLogoutEndpoint();
app.MapBackchannelLogoutEndpoint();
app.MapHealthEndpoint();

app.MapReverseProxy();
app.MapStaticSpaMounts();
app.MapSpaFallback();
app.Run();