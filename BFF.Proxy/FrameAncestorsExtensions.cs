using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;

namespace Bff.Proxy;

/// <summary>
/// Controls which origins may embed this BFF's responses (the served SPA in particular) in a frame.
/// Every response gets <c>Content-Security-Policy: frame-ancestors 'self' [origins...]</c>, so by default
/// only same-origin framing is allowed (clickjacking protection); listed origins (e.g. the UserPortal,
/// which embeds applications in an iframe) are allowed in addition.
/// </summary>
/// <remarks>
/// Config: <c>Security:FrameAncestors</c> (origins, e.g. https://portal.example.com).
/// </remarks>
public static class FrameAncestorsExtensions
{
    private const string SectionName = "Security:FrameAncestors";

    public static IApplicationBuilder UseConfiguredFrameAncestors(
        this IApplicationBuilder app, IConfiguration configuration)
    {
        var origins = configuration.GetSection(SectionName).Get<string[]>() ?? [];
        var policy = string.Join(' ', new[] { "frame-ancestors", "'self'" }.Concat(origins));

        return app.Use(async (context, next) =>
        {
            context.Response.OnStarting(() =>
            {
                context.Response.Headers.ContentSecurityPolicy = policy;
                return Task.CompletedTask;
            });

            await next(context);
        });
    }
}
