using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bff.Proxy;

/// <summary>
/// Opt-in reverse proxy support. Forwarded headers are ignored unless <c>ForwardedHeaders:Enabled</c> is true,
/// and are only honoured from the configured proxies/networks (loopback only when none are configured).
/// </summary>
/// <remarks>
/// Config: <c>ForwardedHeaders:Enabled</c>, <c>ForwardedHeaders:TrustHost</c> (also honour X-Forwarded-Host),
/// <c>ForwardedHeaders:KnownProxies</c> (IPs), <c>ForwardedHeaders:KnownNetworks</c> (CIDRs, e.g. 172.18.0.0/16).
/// </remarks>
public static class ForwardedHeadersExtensions
{
    private const string SectionName = "ForwardedHeaders";

    public static IServiceCollection AddConfiguredForwardedHeaders(
        this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(SectionName);
        if (!section.GetValue<bool>("Enabled"))
        {
            return services;
        }

        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            if (section.GetValue<bool>("TrustHost"))
            {
                options.ForwardedHeaders |= ForwardedHeaders.XForwardedHost;
            }

            foreach (var proxy in section.GetSection("KnownProxies").Get<string[]>() ?? [])
            {
                options.KnownProxies.Add(IPAddress.Parse(proxy));
            }

            foreach (var network in section.GetSection("KnownNetworks").Get<string[]>() ?? [])
            {
                options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(network));
            }
        });

        return services;
    }

    public static IApplicationBuilder UseConfiguredForwardedHeaders(
        this IApplicationBuilder app, IConfiguration configuration)
    {
        return configuration.GetSection(SectionName).GetValue<bool>("Enabled")
            ? app.UseForwardedHeaders()
            : app;
    }
}
