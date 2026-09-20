using Bff.Proxy;
using BFF.Proxy;
using Bff.Proxy.Csrf;
using Bff.Proxy.Endpoints;

var builder = WebApplication.CreateBuilder(args);

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