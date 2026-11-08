using System.Net;
using System.Net.Sockets;
using BFF.Proxy.Tests.Integration.Infrastructure;
using DotNet.Testcontainers.Configurations;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Testcontainers.Keycloak;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace BFF.Proxy.Tests.Keycloak;

public sealed record TestUser(string Username, string Password);

// Starts a real Keycloak (Phase Two image, realm imported from bff-test-realm.json) and the BFF on a real Kestrel
// port that Keycloak can call back for back-channel logout. Runs only when a test in this namespace is selected.
//
// Live mode: set BFF_TEST_KEYCLOAK_URL (+ _REALM, _CLIENT_ID, _CLIENT_SECRET, _USERNAME, _PASSWORD) to run the same
// tests against an existing realm instead of a container. Its client must allow http://localhost:{BFF_TEST_BFF_PORT}/*
// (default 5199) as redirect URI. Tests that need the admin API or a known org setup are skipped in that mode.
[SetUpFixture]
public sealed class KeycloakEnvironment
{
    public const string Image = "quay.io/phasetwo/phasetwo-keycloak:26.8.1";
    private const string ContainerRealm = "bff-test";

    private KeycloakContainer? _container;
    private BffFactory? _bff;

    public static KeycloakEnvironment Current { get; private set; } = null!;

    public KeycloakConnection Connection { get; private set; } = null!;
    public Uri BffBaseUrl { get; private set; } = null!;
    public WireMockServer Backend { get; private set; } = null!;
    public TestUser UserWithOrganizations { get; private set; } = null!;
    public TestUser? UserWithoutOrganizations { get; private set; }
    public KeycloakAdmin? Admin { get; private set; }
    public IReadOnlyDictionary<string, string> OrganizationIds { get; private set; } = new Dictionary<string, string>();
    public bool IsLive => _container is null;

    [OneTimeSetUp]
    public async Task StartAsync()
    {
        Current = this;
        Backend = WireMockServer.Start();
        Backend.Given(Request.Create().WithPath("/api/*"))
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("backend-ok"));

        var liveUrl = Environment.GetEnvironmentVariable("BFF_TEST_KEYCLOAK_URL");
        int bffPort;
        if (!string.IsNullOrWhiteSpace(liveUrl))
        {
            bffPort = int.TryParse(Environment.GetEnvironmentVariable("BFF_TEST_BFF_PORT"), out var p) ? p : 5199;
            Connection = new KeycloakConnection(liveUrl, Required("BFF_TEST_KEYCLOAK_REALM"),
                Required("BFF_TEST_KEYCLOAK_CLIENT_ID"), Required("BFF_TEST_KEYCLOAK_CLIENT_SECRET"));
            UserWithOrganizations = new TestUser(Required("BFF_TEST_KEYCLOAK_USERNAME"), Required("BFF_TEST_KEYCLOAK_PASSWORD"));
            BffBaseUrl = new Uri($"http://localhost:{bffPort}");
        }
        else
        {
            bffPort = GetFreePort();
            BffBaseUrl = new Uri($"http://localhost:{bffPort}");

            // Must happen before the container is created: it gets host.testcontainers.internal -> this host's port.
            await TestcontainersSettings.ExposeHostPortsAsync((ushort)bffPort);

            _container = new KeycloakBuilder(Image)
                .WithRealm(Path.Combine(TestContext.CurrentContext.TestDirectory, "Keycloak", "bff-test-realm.json"))
                .Build();
            await _container.StartAsync();

            // A different host name than the BFF's "localhost", so the two cookie jars never mix.
            var keycloakUrl = new UriBuilder(_container.GetBaseAddress());
            if (keycloakUrl.Host == "localhost")
                keycloakUrl.Host = "127.0.0.1";

            Connection = new KeycloakConnection(keycloakUrl.Uri.ToString(), ContainerRealm, "bff-test", "bff-test-secret");
            UserWithOrganizations = new TestUser("alice", "alice-password");
            UserWithoutOrganizations = new TestUser("bob", "bob-password");

            Admin = new KeycloakAdmin(keycloakUrl.Uri, ContainerRealm, "admin", "admin");
            await Admin.ConfigureBffClientAsync("bff-test", BffBaseUrl,
                new Uri($"http://host.testcontainers.internal:{bffPort}/bff/logout/backchannel"));

            var alice = await Admin.GetUserIdAsync("alice");
            var organizations = new Dictionary<string, string>();
            foreach (var name in new[] { "acme", "globex" })
            {
                organizations[name] = await Admin.CreateOrganizationAsync(name);
                await Admin.AddOrganizationMemberAsync(organizations[name], alice);
            }
            OrganizationIds = organizations;
        }

        _bff = new BffFactory(Connection, Backend.Url!);
        _bff.UseKestrel(options => options.ListenLocalhost(bffPort));
        _bff.StartServer();
    }

    [OneTimeTearDown]
    public async Task StopAsync()
    {
        _bff?.Dispose();
        Admin?.Dispose();
        Backend.Stop();
        Backend.Dispose();
        if (_container is not null)
            await _container.DisposeAsync();
    }

    private static string Required(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"{name} must be set when BFF_TEST_KEYCLOAK_URL is set.");

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
