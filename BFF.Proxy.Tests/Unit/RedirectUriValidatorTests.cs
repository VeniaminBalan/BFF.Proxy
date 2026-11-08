using Bff.Proxy;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace BFF.Proxy.Tests.Unit;

[Category("Unit")]
public class RedirectUriValidatorTests
{
    private const string SpaOrigin = "https://spa.example.com";

    private static RedirectUriValidator CreateValidator(params string[] allowedOrigins)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(allowedOrigins.Select((origin, i) =>
                new KeyValuePair<string, string?>($"Cors:AllowedOrigins:{i}", origin)))
            .Build();
        return new RedirectUriValidator(configuration, NullLogger<RedirectUriValidator>.Instance);
    }

    // The BFF itself is served from https://bff.example.com.
    private static HttpContext BffRequest()
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("bff.example.com");
        context.Request.Path = "/bff/login";
        return context;
    }

    [TestCase("/")]
    [TestCase("/orders")]
    [TestCase("/orders?page=2&sort=desc")]
    [TestCase("/orders#details")]
    [TestCase("https://spa.example.com/after-login")]
    [TestCase("https://SPA.example.com/after-login", TestName = "Allowed origin, host case-insensitive")]
    [TestCase("https://spa.example.com:443/after-login", TestName = "Allowed origin, explicit default port")]
    [TestCase("https://bff.example.com/after-login", TestName = "The BFF's own origin")]
    public void Allows_local_paths_and_allowed_origins(string redirectUri)
    {
        var validator = CreateValidator(SpaOrigin);

        var allowed = validator.TryGetSafeRedirect(BffRequest(), redirectUri, out var safe);

        Assert.Multiple(() =>
        {
            Assert.That(allowed, Is.True);
            Assert.That(safe, Is.EqualTo(redirectUri));
        });
    }

    [TestCase("//evil.example", TestName = "Protocol-relative URL")]
    [TestCase("/\\evil.example", TestName = "Backslash protocol-relative URL")]
    [TestCase("\\\\evil.example", TestName = "UNC-style URL")]
    [TestCase("/\t/evil.example", TestName = "Tab that browsers strip")]
    [TestCase("/\n/evil.example", TestName = "Newline that browsers strip")]
    [TestCase("https://evil.example/", TestName = "Foreign origin")]
    [TestCase("https://spa.example.com.evil.example/", TestName = "Allowed origin as subdomain prefix")]
    [TestCase("https://spa.example.com:8443/", TestName = "Allowed host, other port")]
    [TestCase("http://spa.example.com/", TestName = "Allowed host, other scheme")]
    [TestCase("http://bff.example.com/", TestName = "Own host, downgraded scheme")]
    [TestCase("https://user:pass@spa.example.com/", TestName = "Userinfo on allowed origin")]
    [TestCase("javascript:alert(1)", TestName = "javascript URL")]
    [TestCase("data:text/html,<script>alert(1)</script>", TestName = "data URL")]
    [TestCase("ftp://spa.example.com/", TestName = "Non-http scheme")]
    [TestCase("evil.example", TestName = "Relative URL without leading slash")]
    public void Rejects_open_redirect_attempts(string redirectUri)
    {
        var validator = CreateValidator(SpaOrigin);

        var allowed = validator.TryGetSafeRedirect(BffRequest(), redirectUri, out var safe);

        Assert.Multiple(() =>
        {
            Assert.That(allowed, Is.False);
            Assert.That(safe, Is.EqualTo("/"));
        });
    }

    [TestCase(null)]
    [TestCase("")]
    public void Missing_redirectUri_means_root(string? redirectUri)
    {
        var validator = CreateValidator();

        var allowed = validator.TryGetSafeRedirect(BffRequest(), redirectUri, out var safe);

        Assert.Multiple(() =>
        {
            Assert.That(allowed, Is.True);
            Assert.That(safe, Is.EqualTo("/"));
        });
    }

    [Test]
    public void Sanitize_falls_back_to_root_instead_of_failing()
    {
        var validator = CreateValidator(SpaOrigin);

        Assert.Multiple(() =>
        {
            Assert.That(validator.Sanitize(BffRequest(), "https://evil.example/"), Is.EqualTo("/"));
            Assert.That(validator.Sanitize(BffRequest(), "/orders"), Is.EqualTo("/orders"));
        });
    }

    [Test]
    public void Malformed_allowed_origins_in_config_are_ignored()
    {
        var validator = CreateValidator("not a url", SpaOrigin);

        Assert.That(validator.IsAllowed(BffRequest(), "https://spa.example.com/x"), Is.True);
    }
}
