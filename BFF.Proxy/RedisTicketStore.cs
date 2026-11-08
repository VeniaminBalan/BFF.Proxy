using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Caching.Distributed;

namespace Bff.Proxy;

// Tickets hold the user's Keycloak access/refresh/id tokens, so they are encrypted with Data Protection before
// they reach the cache: a Redis dump, replica or backup alone does not yield usable tokens. (That only holds if
// the Data Protection keys are not readable alongside them - see "Data Protection keys" in the README.)
public class RedisTicketStore : ITicketStore
{
    private readonly IDistributedCache _cache;
    private readonly IDataProtector _protector;
    private readonly ILogger<RedisTicketStore> _logger;
    private const string KeyPrefix = "bff-session:";
    private const string SidPrefix = "bff-sid:";

    public RedisTicketStore(IDistributedCache cache, IDataProtectionProvider dataProtection, ILogger<RedisTicketStore> logger)
    {
        _cache = cache;
        _protector = dataProtection.CreateProtector("Bff.Proxy.RedisTicketStore.v1");
        _logger = logger;
    }

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

        var val = _protector.Protect(TicketSerializer.Default.Serialize(ticket));
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
        if (val == null)
            return null;

        try
        {
            return TicketSerializer.Default.Deserialize(_protector.Unprotect(val));
        }
        catch (CryptographicException ex)
        {
            // Written before encryption was added, or with a Data Protection key this instance doesn't have:
            // treat it as no session (the user signs in again) rather than failing the request.
            _logger.LogInformation(ex, "Discarding session ticket {Key} that could not be decrypted", key);
            return null;
        }
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