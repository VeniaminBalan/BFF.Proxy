using WireMock.Logging;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace BFF.Proxy.Tests.Integration.Infrastructure;

// One BFF host + fake Keycloak + fake backend API per fixture; stubs and request logs reset per test.
[Category("Integration")]
public abstract class BffIntegrationTest
{
    protected FakeKeycloak Keycloak { get; private set; } = null!;
    protected WireMockServer Backend { get; private set; } = null!;
    protected BffFactory Factory { get; private set; } = null!;

    protected virtual string Environment => "Development";
    protected virtual IReadOnlyDictionary<string, string?> Settings => new Dictionary<string, string?>();

    private readonly List<IDisposable> _disposables = [];

    [OneTimeSetUp]
    public void StartBff()
    {
        Keycloak = new FakeKeycloak();
        Backend = WireMockServer.Start();
        Factory = new BffFactory(Keycloak.Connection, Backend.Url!, Environment, Settings);
    }

    [SetUp]
    public void ResetStubs()
    {
        Keycloak.Reset();
        Backend.Reset();
        Backend.Given(Request.Create().WithPath("/api/*"))
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("backend-ok"));
    }

    [TearDown]
    public void DisposeClients()
    {
        foreach (var disposable in _disposables)
            disposable.Dispose();
        _disposables.Clear();
    }

    [OneTimeTearDown]
    public void StopBff()
    {
        Factory.Dispose();
        Backend.Stop();
        Backend.Dispose();
        Keycloak.Dispose();
    }

    protected BffClient AnonymousClient(string baseAddress = "https://localhost")
    {
        var client = Factory.CreateBffClient(baseAddress);
        _disposables.Add(client);
        return client;
    }

    protected TestSession NewSession(
        string? sid = null,
        DateTimeOffset? expiresAt = null,
        string? refreshToken = "refresh-1",
        string? org = null)
    {
        sid ??= $"sid-{Guid.NewGuid():N}";
        return new TestSession(
            Subject: "user-1",
            Sid: sid,
            AccessToken: Keycloak.CreateAccessToken("user-1", sid),
            RefreshToken: refreshToken,
            ExpiresAt: expiresAt ?? DateTimeOffset.UtcNow.AddHours(1),
            Org: org);
    }

    protected async Task<BffClient> SignedInClientAsync(TestSession? session = null, string baseAddress = "https://localhost")
    {
        var client = AnonymousClient(baseAddress);
        await client.SignInAsync(session ?? NewSession());
        return client;
    }

    protected IReadOnlyList<ILogEntry> BackendRequests => Backend.LogEntries.ToList();

    protected static string? Header(ILogEntry entry, string name) =>
        entry.RequestMessage!.Headers?
            .FirstOrDefault(h => string.Equals(h.Key, name, StringComparison.OrdinalIgnoreCase))
            .Value?.FirstOrDefault();
}
