# multi-spa

Mounts two separate SPAs on one BFF image, each at its own path prefix, by copying each build
into its own `wwwroot` subdirectory instead of overwriting the top level. The BFF auto-discovers
any `wwwroot` subdirectory that has its own `index.html` and gives it a client-side-routing
fallback scoped to `/<subdirectory-name>/...` - no config on either side.

## Build

```bash
# from this directory
docker build -t bff-proxy-multi-spa:local .
```

## Run

```bash
docker run --rm -p 8080:8080 \
  -e ASPNETCORE_ENVIRONMENT=Development \
  -e ConnectionStrings__Redis=host.docker.internal:6379,abortConnect=false \
  -e Keycloak__realm=<your realm> \
  -e Keycloak__auth-server-url=http://host.docker.internal:7080 \
  -e Keycloak__resource=<your client id> \
  -e Keycloak__credentials__secret=<your client secret> \
  -e ReverseProxy__BackendAddress=http://host.docker.internal:6081 \
  bff-proxy-multi-spa:local
```

- `http://localhost:8080/app` and any nested route under it (`/app/whatever/nested`) serve the
  main app's `index.html`.
- `http://localhost:8080/admin` and any nested route under it serve the admin app's `index.html`
  - independently of the main app.
- `http://localhost:8080/` still 404s (the base image ships no `wwwroot` of its own, and neither
  `app/` nor `admin/` here touches the top-level `wwwroot/index.html`). Combine with
  [`../static-spa`](../static-spa)'s approach (also `COPY`ing a build straight into `wwwroot/`) if
  you want `/` itself to serve one of the apps too.
- `/bff/*` and `/api/**` are unaffected either way - they're matched before either SPA's fallback.

Real frontend builds need a matching base path baked in (e.g. Vite's `base: '/admin/'`) so their
own asset URLs resolve under `/admin/...` instead of assuming they own `/` - the two `index.html`
files here are plain placeholders with no bundled assets, so this doesn't come up in the sample
itself, but it will with a real build.
