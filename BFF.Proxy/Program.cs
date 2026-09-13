using Bff.Proxy;
using BFF.Proxy;
using Bff.Proxy.Csrf;
using Bff.Proxy.Endpoints;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
await builder.AddBffServicesAsync();

var app = builder.Build();

if (!app.Environment.IsProduction())
{
    app.Logger.LogWarning(
        "Running in {Environment} mode - this configuration is NOT secure and must not be used in production",
        app.Environment.EnvironmentName);
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
app.MapFallbackToFile("index.html");
app.Run();