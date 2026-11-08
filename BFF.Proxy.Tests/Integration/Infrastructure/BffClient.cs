using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Bff.Proxy.Csrf;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;

namespace BFF.Proxy.Tests.Integration.Infrastructure;

// An HttpClient with a cookie jar the test can inspect, no automatic redirects.
public sealed class BffClient : IDisposable
{
    public BffClient(WebApplicationFactory<Program> factory, Uri baseAddress)
    {
        BaseAddress = baseAddress;
        Http = factory.CreateDefaultClient(baseAddress, new CookieContainerHandler(Cookies));
    }

    public Uri BaseAddress { get; }
    public CookieContainer Cookies { get; } = new();
    public HttpClient Http { get; }

    public Cookie? GetCookie(string name) => Cookies.GetCookies(BaseAddress)[name];

    public async Task SignInAsync(TestSession session)
    {
        using var response = await Http.PostAsJsonAsync(TestSignInStartupFilter.Path, session);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NoContent), "test sign-in failed");
    }

    public async Task<MeResponse?> GetMeAsync()
    {
        using var response = await Http.GetAsync("/bff/me");
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            return null;

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), "/bff/me");
        return await response.Content.ReadFromJsonAsync<MeResponse>();
    }

    public async Task<string> GetCsrfTokenAsync()
    {
        var me = await GetMeAsync();
        Assert.That(me?.CsrfToken, Is.Not.Null.And.Not.Empty, "no CSRF token - not signed in?");
        return me!.CsrfToken!;
    }

    public async Task<HttpResponseMessage> SendWithCsrfAsync(HttpMethod method, string path, HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, path) { Content = content };
        request.Headers.Add(CsrfConstants.HeaderName, await GetCsrfTokenAsync());
        return await Http.SendAsync(request);
    }

    public void Dispose() => Http.Dispose();
}

public sealed record MeResponse(bool IsAuthenticated, List<MeClaim> Claims, string? CsrfToken)
{
    public string? Claim(string type) => Claims.FirstOrDefault(c => c.Type == type)?.Value;

    public JsonElement? JsonClaim(string type) =>
        Claim(type) is { } value ? JsonDocument.Parse(value).RootElement : null;
}

public sealed record MeClaim(string Type, string Value);
