using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BFF.Proxy.Tests.Keycloak;

// Keycloak admin REST API (+ Phase Two's organization API) for test setup and assertions.
public sealed class KeycloakAdmin(Uri baseUrl, string realm, string username, string password) : IDisposable
{
    private readonly HttpClient _http = new() { BaseAddress = baseUrl };

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body = null)
    {
        // master's admin-cli tokens live 60s; a fresh one per call keeps long test runs simple.
        using var tokenResponse = await _http.PostAsync("realms/master/protocol/openid-connect/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "password",
                ["client_id"] = "admin-cli",
                ["username"] = username,
                ["password"] = password
            }));
        tokenResponse.EnsureSuccessStatusCode();
        var token = (await tokenResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("access_token").GetString();

        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
            request.Content = JsonContent.Create(body);

        var response = await _http.SendAsync(request);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"{method} {path} -> {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return response;
    }

    private async Task<JsonElement> GetJsonAsync(string path)
    {
        using var response = await SendAsync(HttpMethod.Get, path);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    // Redirect and back-channel URLs depend on the BFF's port, known only at runtime.
    public async Task ConfigureBffClientAsync(string clientId, Uri bffBaseUrl, Uri backchannelLogoutUrl)
    {
        var clients = await GetJsonAsync($"admin/realms/{realm}/clients?clientId={Uri.EscapeDataString(clientId)}");
        var id = clients[0].GetProperty("id").GetString();
        var client = JsonNode.Parse(clients[0].GetRawText())!.AsObject();

        client["redirectUris"] = new JsonArray($"{bffBaseUrl.GetLeftPart(UriPartial.Authority)}/*");
        var attributes = client["attributes"]?.AsObject() ?? new JsonObject();
        attributes["backchannel.logout.url"] = backchannelLogoutUrl.ToString();
        attributes["post.logout.redirect.uris"] = "+";
        client["attributes"] = attributes;

        using (await SendAsync(HttpMethod.Put, $"admin/realms/{realm}/clients/{id}", client)) { }
    }

    public async Task<string> GetUserIdAsync(string username)
    {
        var users = await GetJsonAsync($"admin/realms/{realm}/users?username={Uri.EscapeDataString(username)}&exact=true");
        return users[0].GetProperty("id").GetString()!;
    }

    public async Task<string> CreateOrganizationAsync(string name)
    {
        using var response = await SendAsync(HttpMethod.Post, $"realms/{realm}/orgs", new { name, displayName = name });
        return response.Headers.Location!.Segments[^1];
    }

    public async Task AddOrganizationMemberAsync(string organizationId, string userId)
    {
        using (await SendAsync(HttpMethod.Put, $"realms/{realm}/orgs/{organizationId}/members/{userId}")) { }
    }

    public async Task<IReadOnlyList<string>> GetUserSessionIdsAsync(string userId)
    {
        var sessions = await GetJsonAsync($"admin/realms/{realm}/users/{userId}/sessions");
        return sessions.EnumerateArray().Select(s => s.GetProperty("id").GetString()!).ToList();
    }

    // Like "Sign out" on a session in the admin console: Keycloak sends back-channel logout to the clients.
    public async Task DeleteSessionAsync(string sessionId)
    {
        using (await SendAsync(HttpMethod.Delete, $"admin/realms/{realm}/sessions/{sessionId}")) { }
    }

    public void Dispose() => _http.Dispose();
}
