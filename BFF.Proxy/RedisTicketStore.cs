using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Caching.Distributed;

namespace Bff.Proxy;

public class RedisTicketStore : ITicketStore
{
    private readonly IDistributedCache _cache;
    private const string KeyPrefix = "bff-session:";
    private const string SidPrefix = "bff-sid:";

    public RedisTicketStore(IDistributedCache cache) => _cache = cache;

    public async Task<string> StoreAsync(AuthenticationTicket ticket)
    {
        var key = $"{KeyPrefix}{Guid.NewGuid()}";
        await RenewAsync(key, ticket);
        return key;
    }

    public async Task RenewAsync(string key, AuthenticationTicket ticket)
    {
        var options = new DistributedCacheEntryOptions();
        var expiresAt = ticket.Properties.ExpiresUtc;

        if (expiresAt.HasValue)
            options.SetAbsoluteExpiration(expiresAt.Value);
        else
            options.SetSlidingExpiration(TimeSpan.FromHours(8));

        var val = TicketSerializer.Default.Serialize(ticket);
        await _cache.SetAsync(key, val, options);

        // Index Keycloak Session ID (sid) to this session key
        var sid = ticket.Principal.FindFirst("sid")?.Value;
        if (!string.IsNullOrEmpty(sid))
        {
            await _cache.SetStringAsync($"{SidPrefix}{sid}", key, options);
        }
    }

    public async Task<AuthenticationTicket?> RetrieveAsync(string key)
    {
        var val = await _cache.GetAsync(key);
        return val == null ? null : TicketSerializer.Default.Deserialize(val);
    }

    public async Task RemoveAsync(string key)
    {
        var ticket = await RetrieveAsync(key);
        if (ticket != null)
        {
            var sid = ticket.Principal.FindFirst("sid")?.Value;
            if (!string.IsNullOrEmpty(sid))
            {
                await _cache.RemoveAsync($"{SidPrefix}{sid}");
            }
        }
        await _cache.RemoveAsync(key);
    }

    // Helper method to delete session by Keycloak SID during Back-Channel Logout
    // Returns whether a session was indexed under this sid (and therefore removed).
    public async Task<bool> RemoveBySidAsync(string sid)
    {
        var sessionKey = await _cache.GetStringAsync($"{SidPrefix}{sid}");
        if (string.IsNullOrEmpty(sessionKey))
        {
            return false;
        }

        await RemoveAsync(sessionKey);
        return true;
    }
}