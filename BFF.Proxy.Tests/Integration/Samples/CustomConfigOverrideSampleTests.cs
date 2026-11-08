using System.Text.Json;
using Bff.Proxy;
using BFF.Proxy.Tests.Integration.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Yarp.ReverseProxy.Configuration;

namespace BFF.Proxy.Tests.Integration.Samples;

// samples/custom-config-override: the image's own appsettings.json plus the sample's baked-in
// appsettings.Production.json, run with ASPNETCORE_ENVIRONMENT=Production. Backend address and CORS origins are
// deliberately NOT overridden here: they must come from the sample's file.
public class CustomConfigOverrideSampleTests : BffIntegrationTest
{
    private JsonElement _sampleConfig;

    protected override string Environment => "Production";

    protected override IReadOnlyDictionary<string, string?> Settings => new Dictionary<string, string?>
    {
        ["ReverseProxy:BackendAddress"] = null,
        ["Cors:AllowedOrigins:0"] = null,
        // Production requires an https authority; none of these tests talk to Keycloak.
        ["Keycloak:auth-server-url"] = "https://keycloak.example.test"
    };

    protected override string CreateContentRoot()
    {
        _sampleConfig = JsonDocument.Parse(SampleLayout.ReadSampleFile("custom-config-override/appsettings.Production.json")).RootElement;
        var layout = DisposeAfterFixture(SampleLayout.Create())
            .CopyFile("BFF.Proxy/appsettings.json", "appsettings.json")
            .CopyFile("samples/custom-config-override/appsettings.Production.json", "appsettings.Production.json");
        return layout.ContentRoot;
    }

    private string SampleBackendAddress => _sampleConfig.GetProperty("ReverseProxy").GetProperty("BackendAddress").GetString()!;

    private string[] SampleAllowedOrigins =>
        _sampleConfig.GetProperty("Cors").GetProperty("AllowedOrigins").EnumerateArray().Select(o => o.GetString()!).ToArray();

    [Test]
    public void Backend_address_comes_from_the_baked_in_production_file()
    {
        var proxyConfig = Factory.Services.GetRequiredService<IProxyConfigProvider>().GetConfig();

        var destination = proxyConfig.Clusters.Single().Destinations!.Values.Single();

        Assert.That(destination.Address, Is.EqualTo(SampleBackendAddress));
    }

    [Test]
    public void Route_shape_is_fixed_in_code_and_still_requires_authentication()
    {
        var route = Factory.Services.GetRequiredService<IProxyConfigProvider>().GetConfig().Routes.Single();

        Assert.Multiple(() =>
        {
            Assert.That(route.Match.Path, Is.EqualTo("/api/{**catch-all}"));
            Assert.That(route.AuthorizationPolicy, Is.EqualTo("RequireAuthenticatedUser"));
        });
    }

    [Test]
    public async Task Cors_origins_come_from_the_baked_in_production_file()
    {
        foreach (var origin in SampleAllowedOrigins)
        {
            var request = new HttpRequestMessage(HttpMethod.Options, "/bff/me");
            request.Headers.Add("Origin", origin);
            request.Headers.Add("Access-Control-Request-Method", "GET");
            using var response = await AnonymousClient("https://bff.example.test").Http.SendAsync(request);

            Assert.That(response.Headers.TryGetValues("Access-Control-Allow-Origin", out var allowed) ? allowed.Single() : null,
                Is.EqualTo(origin), $"CORS for {origin}");
        }
    }

    [Test]
    public void Redirects_back_to_the_configured_spa_origin_are_allowed()
    {
        var validator = Factory.Services.GetRequiredService<RedirectUriValidator>();
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("bff.example.test");

        Assert.Multiple(() =>
        {
            foreach (var origin in SampleAllowedOrigins)
                Assert.That(validator.IsAllowed(context, origin + "/after-login"), Is.True, origin);
            Assert.That(validator.IsAllowed(context, "http://localhost:3000/"), Is.False, "the Development default must not leak in");
        });
    }
}
