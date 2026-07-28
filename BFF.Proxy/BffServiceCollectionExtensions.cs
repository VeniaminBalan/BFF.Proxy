using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;
using OpenTelemetry.Trace;
using StackExchange.Redis;
using Yarp.ReverseProxy.Transforms;

namespace Bff.Proxy;

public static class BffServiceCollectionExtensions
{
    public static async Task AddBffServicesAsync(this WebApplicationBuilder builder)
    {
        // Required - binding throws at startup if realm/auth-server-url/resource/credentials.secret
        // are missing from config, instead of silently falling back to a hardcoded realm.
        builder.Services.AddOptions<KeycloakOptions>()
            .Bind(builder.Configuration.GetSection(KeycloakOptions.SectionName))
            .ValidateOnStart();

        var redisConnectionMultiplexer = await ConnectionMultiplexer.ConnectAsync(
            builder.Configuration.GetConnectionString("Redis") ?? "localhost:6379,abortConnect=false");
        builder.Services.AddSingleton<IConnectionMultiplexer>(redisConnectionMultiplexer);

        builder.Services.AddOpenTelemetry()
            .WithTracing(tracing => tracing.AddRedisInstrumentation(redisConnectionMultiplexer));

        // Register an HttpClient tailored for Phase Two / Keycloak REST calls. BaseAddress is the
        // realm-scoped authority with a trailing slash, so callers can use realm-relative paths
        // like "orgs" or "users/switch-organization" without hardcoding the realm name.
        builder.Services.AddHttpClient("KeycloakClient", (sp, client) =>
        {
            var keycloakOptions = sp.GetRequiredService<IOptions<KeycloakOptions>>().Value;
            client.BaseAddress = new Uri($"{keycloakOptions.Authority}/");
        });

        var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
        builder.Services.AddCors(options =>
        {
            options.AddPolicy("BffClient", policy =>
            {
                policy.WithOrigins(allowedOrigins)
                    .AllowAnyHeader()
                    .AllowAnyMethod()
                    .AllowCredentials();
            });
        });

        builder.Services.AddHttpClient("KeycloakHealthClient", client => client.Timeout = TimeSpan.FromSeconds(5));
        builder.Services.AddHttpClient("ProxiedBackendHealthClient", client => client.Timeout = TimeSpan.FromSeconds(5));

        builder.Services.AddHealthChecks()
            .AddRedis(
                builder.Configuration.GetConnectionString("Redis") ?? "localhost:6379",
                name: "redis",
                tags: ["ready"])
            .AddCheck<KeycloakHealthCheck>("keycloak", tags: ["ready"])
            .AddCheck<ProxiedBackendHealthCheck>("proxied-backend", tags: ["ready"]);

        builder.Services.AddStackExchangeRedisCache(options =>
        {
            options.ConnectionMultiplexerFactory = () => Task.FromResult<IConnectionMultiplexer>(redisConnectionMultiplexer);
            options.InstanceName = "BffSessionCache:";
        });

        builder.Services.AddSingleton<RedisTicketStore>();
        builder.Services.AddSingleton<ITicketStore>(sp => sp.GetRequiredService<RedisTicketStore>());
        builder.Services.AddOptions<CookieAuthenticationOptions>(CookieAuthenticationDefaults.AuthenticationScheme)
            .Configure<ITicketStore>((options, ticketStore) => options.SessionStore = ticketStore);

        // Proactively refresh the access token on every request once it's within 60s of expiry,
        // instead of letting it expire and forwarding a stale Bearer token to the backend.
        builder.Services.AddOptions<CookieAuthenticationOptions>(CookieAuthenticationDefaults.AuthenticationScheme)
            .Configure<IHttpClientFactory, IOptions<KeycloakOptions>>((options, httpClientFactory, keycloakOptions) =>
            {
                options.Events.OnValidatePrincipal = async context =>
                {
                    var expiresAtValue = context.Properties.GetTokenValue("expires_at");
                    if (string.IsNullOrEmpty(expiresAtValue))
                        return;

                    if (!DateTimeOffset.TryParse(
                            expiresAtValue,
                            CultureInfo.InvariantCulture,
                            DateTimeStyles.RoundtripKind,
                            out var expiresAt))
                        return;

                    if (expiresAt - DateTimeOffset.UtcNow > TimeSpan.FromSeconds(60))
                        return; // still valid, nothing to do

                    var refreshToken = context.Properties.GetTokenValue("refresh_token");
                    if (string.IsNullOrEmpty(refreshToken))
                    {
                        context.RejectPrincipal();
                        await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                        return;
                    }

                    var tokenEndpoint = $"{keycloakOptions.Value.Authority}/protocol/openid-connect/token";
                    var client = httpClientFactory.CreateClient("KeycloakClient");

                    var tokenParams = new Dictionary<string, string>
                    {
                        ["grant_type"] = "refresh_token",
                        ["client_id"] = keycloakOptions.Value.Resource,
                        ["client_secret"] = keycloakOptions.Value.Credentials.Secret,
                        ["refresh_token"] = refreshToken
                    };

                    // Note: concurrent requests arriving while the token is near expiry can each
                    // trigger a refresh; Keycloak tolerates this unless refresh token rotation is
                    // configured to be strictly single-use.
                    var response = await client.PostAsync(tokenEndpoint, new FormUrlEncodedContent(tokenParams));
                    if (!response.IsSuccessStatusCode)
                    {
                        context.RejectPrincipal();
                        await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                        return;
                    }

                    var json = await response.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;
                    var newAccessToken = root.GetProperty("access_token").GetString()!;
                    var newRefreshToken = root.TryGetProperty("refresh_token", out var refreshTokenElement)
                        ? refreshTokenElement.GetString()
                        : refreshToken;
                    var expiresIn = root.TryGetProperty("expires_in", out var expiresInElement)
                        ? expiresInElement.GetInt32()
                        : 300;

                    context.Properties.UpdateTokenValue("access_token", newAccessToken);
                    context.Properties.UpdateTokenValue("refresh_token", newRefreshToken ?? refreshToken);
                    context.Properties.UpdateTokenValue(
                        "expires_at",
                        DateTimeOffset.UtcNow.AddSeconds(expiresIn).ToString("o", CultureInfo.InvariantCulture));

                    context.ShouldRenew = true;
                };
            });

        builder.Services.AddAuthentication(options =>
            {
                options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                options.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
            })
            .AddCookie(CookieAuthenticationDefaults.AuthenticationScheme, options =>
            {
                options.Cookie.Name = builder.Environment.IsDevelopment()
                    ? "bff-session"
                    : "__Host-bff-session"; // Secure prefix
                options.Cookie.HttpOnly = true;             // Prevents XSS access
                options.Cookie.SameSite = SameSiteMode.Lax; // Adjust to Strict if on identical domain
                options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
                    ? CookieSecurePolicy.SameAsRequest
                    : CookieSecurePolicy.Always;
            })
            .AddOpenIdConnect(OpenIdConnectDefaults.AuthenticationScheme, options =>
            {
                var keycloakOptions = builder.Configuration.GetSection(KeycloakOptions.SectionName).Get<KeycloakOptions>()!;
                options.Authority = keycloakOptions.Authority;
                options.ClientId = keycloakOptions.Resource;
                options.ClientSecret = keycloakOptions.Credentials.Secret;
                options.ResponseType = "code";
                options.SaveTokens = true; // Saves access_token and refresh_token into Redis TicketStore
                options.GetClaimsFromUserInfoEndpoint = true;
                options.RequireHttpsMetadata = !builder.Environment.IsDevelopment(); // For local dev with HTTP
                // Enforce HTTPS in production
                // Set to true in production with HTTPS
                options.Scope.Add("openid");
                options.Scope.Add("profile");
                options.Scope.Add("offline_access"); // For refresh tokens

                options.Events.OnRedirectToIdentityProvider = context =>
                {
                    if (context.Properties.Items.TryGetValue("prompt", out var prompt) && !string.IsNullOrWhiteSpace(prompt))
                    {
                        context.ProtocolMessage.SetParameter("prompt", prompt);
                    }

                    return Task.CompletedTask;
                };

                // A silent SSO check (prompt=none) fails with login_required/interaction_required when
                // there's no active Keycloak session. Don't surface that as an error page - just send the
                // browser back to the SPA so it can fall back to showing an explicit login button.
                options.Events.OnRemoteFailure = context =>
                {
                    var isSilentAttempt = context.Properties?.Items.TryGetValue("prompt", out var prompt) == true
                                          && prompt == "none";

                    if (isSilentAttempt)
                    {
                        context.HandleResponse();
                        context.Response.Redirect(context.Properties?.RedirectUri ?? "/");
                    }

                    return Task.CompletedTask;
                };
            });

        builder.Services.AddAuthorization(options =>
        {
            options.AddPolicy("RequireAuthenticatedUser", policy => policy.RequireAuthenticatedUser());
        });

        builder.Services.AddReverseProxy()
            .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"))
            .AddTransforms(builderContext =>
            {
                // Intercept every proxied request and attach the Bearer token from the session
                builderContext.AddRequestTransform(async transformContext =>
                {
                    var accessToken = await transformContext.HttpContext.GetTokenAsync("access_token");
                    if (!string.IsNullOrEmpty(accessToken))
                    {
                        transformContext.ProxyRequest.Headers.Authorization =
                            new AuthenticationHeaderValue("Bearer", accessToken);
                    }
                });
            });
    }
}
