using BFF.Proxy.Tests.Integration.Infrastructure;
using WireMock.Server;

namespace BFF.Proxy.Tests.Integration;

// Misconfiguration must stop the app at startup, not surface as broken logins later.
[Category("Integration")]
public class StartupValidationTests
{
    private static string AllMessages(Exception exception)
    {
        var messages = new List<string>();
        for (Exception? e = exception; e is not null; e = e.InnerException)
            messages.Add(e.Message);
        if (exception is AggregateException aggregate)
            messages.AddRange(aggregate.InnerExceptions.Select(AllMessages));
        return string.Join(" | ", messages);
    }

    private static Exception? StartAndCatch(string environment, Dictionary<string, string?> overrides)
    {
        using var keycloak = new FakeKeycloak();
        using var backend = WireMockServer.Start();
        using var factory = new BffFactory(keycloak.Connection, backend.Url!, environment, overrides);
        try
        {
            using var client = factory.CreateClient();
            return null;
        }
        catch (Exception e)
        {
            return e;
        }
    }

    [Test]
    public void Missing_backend_address_fails_startup()
    {
        var error = StartAndCatch("Development", new() { ["ReverseProxy:BackendAddress"] = "" });

        Assert.That(error, Is.Not.Null, "startup should fail");
        Assert.That(AllMessages(error!), Does.Contain("ReverseProxy:BackendAddress"));
    }

    // appsettings.json ships the Keycloak keys as empty strings, so "missing" means "empty" in practice.
    [TestCase("Keycloak:realm")]
    [TestCase("Keycloak:auth-server-url")]
    [TestCase("Keycloak:resource")]
    [TestCase("Keycloak:credentials:secret")]
    public void Empty_keycloak_setting_fails_startup(string key)
    {
        var error = StartAndCatch("Development", new() { [key] = "" });

        Assert.That(error, Is.Not.Null, $"startup should fail when {key} is empty");
        Assert.That(AllMessages(error!), Does.Contain("Keycloak"));
    }
}
