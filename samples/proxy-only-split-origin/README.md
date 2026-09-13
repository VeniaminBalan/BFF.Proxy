# proxy-only-split-origin

Uses the base `bff-proxy` image (published as `ghcr.io/veniaminbalan/bff.proxy:v1.0.0`) completely
unmodified - no downstream Dockerfile, no `wwwroot` - as a pure API/BFF gateway, with the SPA
hosted in its own container on a different origin/port.
This is the scenario `Cors:AllowedOrigins` and the `/bff/me` CSRF round-trip exist for; see
`BFF_CLIENT_SETUP.md` at the repo root for the frontend side (axios setup, CSRF header
interceptor, silent SSO).

## Run

```bash
# from this directory
export KEYCLOAK_REALM=<your realm>
export KEYCLOAK_AUTH_SERVER_URL=http://host.docker.internal:7080
export KEYCLOAK_RESOURCE=<your client id>
export KEYCLOAK_CLIENT_SECRET=<your client secret>

docker compose up --build
```

- BFF: `http://localhost:8080` (`/bff/*`, `/api/**`)
- Sample SPA: `http://localhost:3000`

Because these are different origins, `Cors__AllowedOrigins__0` on the `bff` service must name the
SPA's exact origin, and `AllowCredentials()` (already configured in `BffServiceCollectionExtensions.cs`)
is what lets the session cookie flow cross-origin at all.
