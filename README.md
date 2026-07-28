# BFF Proxy

## What problem this solves

A public, browser-hosted SPA can't hold a Keycloak client secret, and any token it stores in
memory, `localStorage`, or `sessionStorage` is reachable by any script running on the page (an
XSS bug, a compromised dependency, a browser extension). Handing the SPA an OAuth access token at
all means that token is one XSS away from being stolen.

The **Backend-for-Frontend (BFF) pattern** removes that exposure entirely: the SPA never sees an
access token, a refresh token, or an ID token. Instead, a server-side component - this project -
performs the OAuth/OIDC dance with Keycloak on the SPA's behalf, keeps the tokens in a
server-side session store, and talks to the browser using nothing but an `HttpOnly` session
cookie. The SPA calls the BFF; the BFF calls Keycloak and the downstream API; the browser never
holds a bearer token.

This also happens to solve the SPA's CORS problem: instead of the browser calling multiple
origins (Keycloak, the resource API), it only ever talks to one origin (the BFF), which then fans
out server-side.

## High-level architecture

```
Browser (SPA)
   |  HttpOnly session cookie only - no tokens ever reach the browser
   v
BFF.Proxy  (this project)
   |  - Owns the OAuth Authorization Code flow with Keycloak
   |  - Stores access/refresh/id tokens server-side (Redis)
   |  - Attaches Bearer token to proxied requests (YARP)
   v
Resource API <---- Keycloak (Phase Two organizations)
```

Nothing in the SPA's JS ever constructs a Keycloak URL, holds a client secret, or reads a JWT.
Every `/bff/*` call the SPA makes rides on `withCredentials: true` so the session cookie is sent
automatically; every `/api/*` call is transparently proxied through to the resource API with a
Bearer token attached server-side.

## Request flow

1. **Login** - SPA redirects the browser to `GET /bff/login`. If there's no BFF session yet, the
   endpoint issues an OIDC challenge, which redirects to Keycloak's login page. Keycloak redirects
   back to the BFF's OIDC callback with an authorization code. ASP.NET Core's OpenID Connect
   handler exchanges the code for tokens (`SaveTokens = true`) and signs the user into a cookie
   session backed by Redis (`RedisTicketStore`).
2. **Session cookie only** - From here on, the browser only ever holds the `bff-session` (dev) /
   `__Host-bff-session` (prod) cookie: `HttpOnly`, `SameSite=Lax`, `Secure` in production. The
   actual tokens live server-side in Redis, keyed by the cookie's opaque session id.
3. **Calling the API** - The SPA calls `/api/**` on the BFF's own origin. YARP proxies the request
   to the resource API, and a request transform pulls the access token out of the current cookie
   session and attaches it as `Authorization: Bearer <token>` before forwarding.
4. **Token refresh** - Before any of that happens, a cookie `OnValidatePrincipal` hook checks
   whether the access token is within 60 seconds of expiring and, if so, silently exchanges the
   refresh token for a new one and re-persists the session - so a stale/expired token is never
   forwarded to the API and the user is never bounced back to a login screen just because their
   access token expired mid-session.
5. **Organization switching** - Because the org context lives in a Keycloak "Phase Two"
   organization claim on the JWT, switching organizations means Keycloak has to mint a *new* JWT
   with a different `org` claim. `/bff/orgs/switch` calls Keycloak's Phase Two API to switch the
   active organization, refreshes the token to get the updated claim, and patches the cached
   session's claims in place so `/bff/me` reflects the new organization immediately.
6. **Logout** - `/bff/logout` clears both the cookie and the OIDC session and redirects through
   Keycloak's end-session endpoint. Keycloak also calls `/bff/logout/backchannel` directly
   (OIDC back-channel logout) when a session ends anywhere - e.g. the user logs out from a
   different app in the same realm - so the BFF can kill the matching Redis session even without
   browser involvement.

## Endpoints

| Endpoint | Method | Auth | Purpose |
|---|---|---|---|
| `/bff/login` | GET | anonymous | Starts (or silently reuses) the login flow. `?prompt=none` supports silent SSO checks; `?prompt=select_account` forces Keycloak's account chooser. `?redirectUri=` controls where the browser lands afterward. |
| `/bff/me` | GET | anonymous* | "Am I logged in?" probe. Returns `401` if not authenticated (deliberately `AllowAnonymous` + a manual check - see below), otherwise the user's claims and the current CSRF token. |
| `/bff/logout` | GET | anonymous | Clears the local cookie session and redirects through Keycloak's end-session endpoint. |
| `/bff/logout/backchannel` | POST | anonymous (Keycloak-only) | OIDC back-channel logout callback. Validates the `logout_token` JWT (issuer, audience, signature, no `nonce`, has a logout event) and removes the matching session from Redis by Keycloak's session id (`sid`). |
| `/bff/orgs` | GET | required | Lists the organizations the current user belongs to (proxies to Keycloak Phase Two). |
| `/bff/orgs/switch` | PUT | required | Switches the user's active organization, refreshes the token to pick up the new `org` claim, and re-signs the cookie session. |
| `/bff/health` | GET | anonymous | Aggregated readiness check (Redis, Keycloak reachability, proxied backend reachability). |
| `/api/**` | any | required (per route policy) | Reverse-proxied to the resource API via YARP, with the access token attached server-side. |

\* `/bff/me` and `/bff/login` are `AllowAnonymous` on purpose: if they required authorization,
hitting them unauthenticated would trigger an automatic 302 challenge to Keycloak (the default
challenge scheme) instead of a clean `401`/redirect-with-context that the SPA can react to in
JS. Each endpoint does its own manual `context.User.Identity.IsAuthenticated` check instead.

Each endpoint lives in its own file under `Endpoints/`, registered as an
`IEndpointRouteBuilder` extension method (`MapOrgsEndpoint()`, `MapMeEndpoint()`, etc.) and wired
up in `Program.cs`.

## Cross-cutting concerns

### Session storage - Redis (`RedisTicketStore`)

The cookie itself only carries an opaque key; the actual `AuthenticationTicket` (claims + tokens
+ expiry) is serialized into Redis (`ITicketStore`/`RedisTicketStore`). This is what makes the BFF
horizontally scalable (any instance can serve any request) and is also how back-channel logout
works: Keycloak's `sid` claim is indexed in Redis (`bff-sid:{sid} -> session key`), so a
server-to-server logout call can find and kill the right session without a browser involved.

### Proactive token refresh

Configured on the cookie handler's `OnValidatePrincipal` event
(`BffServiceCollectionExtensions.cs`): on every request, if the stored access token is within 60
seconds of expiring, the BFF exchanges the refresh token for a new one *before* the request
continues, updates the session in Redis, and lets the request proceed with a fresh token. If the
refresh fails (refresh token itself expired/revoked), the session is rejected and signed out
rather than silently forwarding a dead token to the API.

### CSRF protection (double-submit cookie)

`SameSite=Lax` on the session cookie blocks classic cross-site form-based CSRF, but it's not a
complete defense on its own, so the BFF layers a double-submit CSRF token on top
(`Csrf/CsrfCookieMiddleware.cs`, `Csrf/CsrfValidationMiddleware.cs`):

- A non-`HttpOnly` `bff-csrf` cookie is issued for every authenticated session.
- Because the BFF and SPA run on different origins in dev, the SPA can't read that cookie
  directly via `document.cookie` - so the same value is also returned in the `/bff/me` response
  body as `csrfToken`.
- The SPA echoes that value back via the `X-CSRF-Token` header on state-changing requests
  (`POST`/`PUT`/`PATCH`/`DELETE`).
- The BFF rejects any such request (`403`) unless the header matches the cookie
  (constant-time comparison).

An attacker's page can force the cookie to be sent automatically, but can't read the `/bff/me`
JSON response cross-origin (CORS blocks that) and so can't produce a matching header value.

### CORS

Only origins listed in `Cors:AllowedOrigins` (the SPA's own origin) are allowed, and
`AllowCredentials()` is required so the session cookie can flow on cross-port requests in local
dev (the SPA and BFF run on different ports).

### Keycloak configuration

Keycloak settings are required, strongly-typed configuration (`KeycloakOptions.cs`), bound from a
`Keycloak` section shape (`realm`, `auth-server-url`, `resource`, `credentials.secret`) rather
than a single ad hoc `Authority` string. Binding is validated at startup (`ValidateOnStart()`), so
a missing value fails fast instead of silently falling back to a hardcoded realm/host. The
realm-scoped issuer URL (`Authority`) is computed once (`{auth-server-url}/realms/{realm}`) and
reused everywhere - the `KeycloakClient` HttpClient, the OIDC handler, token refresh, and the
Keycloak health check.

## Setup

Running the BFF requires three things reachable: a Keycloak realm with a confidential client set
up for it, a Redis instance, and the downstream API it proxies to. All of this is plain
configuration - nothing here is specific to any particular orchestration/hosting setup.

### 1. Keycloak client

Create a confidential client in the target realm (Admin Console -> Clients -> Create client):

| Setting | Value | Why |
|---|---|---|
| Client authentication | On (confidential) | The BFF needs a secret to exchange the auth code server-side; a public client can't do this safely. |
| Standard flow (Authorization Code) | On | This is the flow the BFF's OIDC handler uses. |
| Valid redirect URIs | `https://<bff-host>/*` (and the SPA's own origin if it redirects through post-login) | Where Keycloak is allowed to send the browser back to after login. |
| Web origins | `https://<bff-host>` | CORS allowance for the BFF's own calls into Keycloak. |
| Backchannel logout URL | `https://<bff-host>/bff/logout/backchannel` | Without this, Keycloak never calls `/bff/logout/backchannel`, so a logout in another app won't kill this BFF's session for the same user. This has to be set explicitly - Keycloak does not infer it. |

After saving, open the Credentials tab and copy the client secret - you'll need it below.

If the same client is also used by a resource API for its own service-to-service Keycloak calls
(a common shortcut - see "One client, two roles reused deliberately" below), also enable "Service
accounts roles" on it; the BFF itself doesn't need that setting.

### 2. Redis

Any reachable Redis instance works - this is where session tickets (tokens + claims) are stored,
keyed by the session cookie's opaque id. For local development, a throwaway container is enough:

```bash
docker run --rm -p 6379:6379 redis:7
```

### 3. Configuration

Fill in `BFF.Proxy/appsettings.json` (or an environment-specific
`appsettings.<Environment>.json`, or environment variables using the standard ASP.NET Core
`Section__Key` convention):

```json
{
  "ConnectionStrings": {
    "Redis": "localhost:6379,abortConnect=false"
  },
  "Keycloak": {
    "realm": "<your realm>",
    "auth-server-url": "http://localhost:7080",
    "resource": "<the client id you created above>",
    "credentials": {
      "secret": "<the client secret you copied above>"
    }
  },
  "Cors": {
    "AllowedOrigins": ["https://<spa-origin>"]
  },
  "ReverseProxy": {
    "Clusters": {
      "dotnet-backend-cluster": {
        "Destinations": {
          "backend1": { "Address": "http://localhost:6081" }
        }
      }
    }
  }
}
```

All four `Keycloak:*` values are required - `KeycloakOptions` binds them at startup with
`ValidateOnStart()`, so a missing value fails immediately with a clear error instead of the app
starting and failing mysteriously on first login attempt. `ReverseProxy:Clusters:*` points at
whichever resource server the BFF is proxying `/api/**` to.

### 4. Run it

```bash
cd BFF.Proxy
dotnet run
```

The SPA needs to know where the BFF ended up listening (its own base URL, used for `/bff/*` calls
and as the proxy target for `/api/*` calls) - see [BFF_CLIENT_SETUP.md](BFF_CLIENT_SETUP.md) for
how to wire a React SPA up to it.

By default `dotnet run` uses the `Development` launch profile, which layers
`appsettings.Development.json` on top of `appsettings.json` (see `Properties/launchSettings.json`).
Use that file for local overrides (e.g. `Cors:AllowedOrigins` pointing at your SPA's dev server)
instead of editing `appsettings.json`.

## Running with Docker

### Build the image

```bash
docker build -t bff-proxy -f BFF.Proxy/Dockerfile BFF.Proxy
```

### Run it

```bash
docker run --rm -p 8080:8080 \
  -e ASPNETCORE_ENVIRONMENT=Development \
  -e ConnectionStrings__Redis=host.docker.internal:6379,abortConnect=false \
  -e Keycloak__realm=<your realm> \
  -e Keycloak__auth-server-url=http://host.docker.internal:7080 \
  -e Keycloak__resource=<your client id> \
  -e Keycloak__credentials__secret=<your client secret> \
  -e Cors__AllowedOrigins__0=http://localhost:3000 \
  bff-proxy
```

Configuration is supplied the same way as any ASP.NET Core app: environment variables using the
`Section__Key` convention (double underscore for nesting), which override whatever is baked into
`appsettings.json`. There's no separate Docker-specific configuration mechanism.

### `ASPNETCORE_ENVIRONMENT`

The image defaults to `ASPNETCORE_ENVIRONMENT=Production`. Set it to `Development` (as above) when
running the container for local development against `localhost` dependencies - this makes the app
pick up `appsettings.Development.json` in addition to environment variable overrides. Whenever the
app isn't running as `Production` it logs a startup warning, since a non-Production configuration
is not secure and must never be used outside local development.

Pre-built images are published to `ghcr.io` on every push to `main` (see
`.github/workflows/docker-publish.yml`).

### Docker Compose (dev setup)

`docker-compose.yml` at the repo root spins up the BFF alongside a Redis container, wired for
local development (`ASPNETCORE_ENVIRONMENT=Development`). It still needs a reachable Keycloak
instance and resource API - point at wherever those are already running (e.g. `localhost` via
`host.docker.internal`, or another compose project on the same Docker network).

```bash
cp .env.example .env   # fill in your Keycloak realm/client and backend API URL
docker compose up --build
```

The BFF listens on `http://localhost:8080`; Redis is also exposed on `localhost:6379`. See
`.env.example` for the full list of variables the compose file expects.

## Why this shape (design rationale)

- **One client, two roles reused deliberately** - the same Keycloak client can be used both for
  the BFF's browser login (confidential, authorization code flow) and for a resource API's own
  service-account calls into Keycloak's Phase Two API. This trades some separation-of-concerns
  purity for one fewer client to manage; splitting into a dedicated client for the BFF is a
  reasonable follow-up if the two start needing different lifecycles (e.g. secret rotation).
- **Redis over in-memory session state** - required for horizontal scaling and for back-channel
  logout to have somewhere durable to look up a session by Keycloak's `sid`.
- **YARP over a hand-rolled proxy** - request/response streaming, header handling, and health
  checks come for free; the only custom behavior needed is the one request transform that
  attaches the Bearer token.
- **Extension-method organization** - service registration (`BffServiceCollectionExtensions.cs`),
  CSRF middleware (`Csrf/`), and each endpoint (`Endpoints/`) are split into their own files so
  `Program.cs` stays a short, readable list of "what's wired up," not "how it's implemented."
