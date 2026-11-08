using System.Security.Claims;
using System.Text;
using Bff.Proxy;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BFF.Proxy.Tests.Unit;

// RedisTicketStore only depends on IDistributedCache, so the in-memory cache exercises the same code path.
[Category("Unit")]
public class RedisTicketStoreTests
{
    private const string AccessToken = "super-secret-access-token";
    private MemoryDistributedCache _cache = null!;
    private RedisTicketStore _store = null!;

    [SetUp]
    public void SetUp()
    {
        _cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        _store = CreateStore(new EphemeralDataProtectionProvider());
    }

    private RedisTicketStore CreateStore(IDataProtectionProvider dataProtection) =>
        new(_cache, dataProtection, NullLogger<RedisTicketStore>.Instance);

    private static AuthenticationTicket CreateTicket(string? sid = "kc-session-1")
    {
        var claims = new List<Claim> { new("sub", "user-1") };
        if (sid is not null)
            claims.Add(new Claim("sid", sid));

        var properties = new AuthenticationProperties();
        properties.StoreTokens([new AuthenticationToken { Name = "access_token", Value = AccessToken }]);

        return new AuthenticationTicket(
            new ClaimsPrincipal(new ClaimsIdentity(claims, "test")),
            properties,
            CookieAuthenticationDefaults.AuthenticationScheme);
    }

    [Test]
    public async Task Stored_ticket_round_trips()
    {
        var key = await _store.StoreAsync(CreateTicket());

        var ticket = await _store.RetrieveAsync(key);

        Assert.That(ticket, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(key, Does.StartWith("bff-session:"));
            Assert.That(ticket!.Principal.FindFirst("sub")?.Value, Is.EqualTo("user-1"));
            Assert.That(ticket.Properties.GetTokenValue("access_token"), Is.EqualTo(AccessToken));
        });
    }

    [Test]
    public async Task Tokens_are_encrypted_at_rest()
    {
        var key = await _store.StoreAsync(CreateTicket());

        var raw = await _cache.GetAsync(key);

        Assert.That(raw, Is.Not.Null);
        Assert.That(Encoding.UTF8.GetString(raw!), Does.Not.Contain(AccessToken));
    }

    [Test]
    public async Task Keycloak_sid_is_indexed_to_the_session_key()
    {
        var key = await _store.StoreAsync(CreateTicket("kc-session-1"));

        Assert.That(await _cache.GetStringAsync("bff-sid:kc-session-1"), Is.EqualTo(key));
    }

    [Test]
    public async Task RemoveBySid_removes_the_session_and_its_index()
    {
        var key = await _store.StoreAsync(CreateTicket("kc-session-1"));

        var removed = await _store.RemoveBySidAsync("kc-session-1");

        Assert.Multiple(async () =>
        {
            Assert.That(removed, Is.True);
            Assert.That(await _store.RetrieveAsync(key), Is.Null);
            Assert.That(await _cache.GetStringAsync("bff-sid:kc-session-1"), Is.Null);
        });
    }

    [Test]
    public async Task RemoveBySid_for_an_unknown_sid_reports_nothing_removed()
    {
        var key = await _store.StoreAsync(CreateTicket("kc-session-1"));

        var removed = await _store.RemoveBySidAsync("someone-else");

        Assert.Multiple(async () =>
        {
            Assert.That(removed, Is.False);
            Assert.That(await _store.RetrieveAsync(key), Is.Not.Null);
        });
    }

    [Test]
    public async Task Remove_also_clears_the_sid_index()
    {
        var key = await _store.StoreAsync(CreateTicket("kc-session-1"));

        await _store.RemoveAsync(key);

        Assert.That(await _cache.GetStringAsync("bff-sid:kc-session-1"), Is.Null);
    }

    [Test]
    public async Task Ticket_without_sid_is_stored_without_an_index()
    {
        var key = await _store.StoreAsync(CreateTicket(sid: null));

        Assert.That(await _store.RetrieveAsync(key), Is.Not.Null);
    }

    [Test]
    public async Task Ticket_protected_with_another_key_ring_is_treated_as_no_session()
    {
        var key = await _store.StoreAsync(CreateTicket());
        var otherInstance = CreateStore(new EphemeralDataProtectionProvider());

        Assert.That(await otherInstance.RetrieveAsync(key), Is.Null);
    }

    [Test]
    public async Task Missing_key_returns_null() =>
        Assert.That(await _store.RetrieveAsync("bff-session:does-not-exist"), Is.Null);
}
