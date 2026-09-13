# static-spa

Layers an already-built SPA (a plain static `dist/` folder) onto the base `bff-proxy` image
(published as `ghcr.io/veniaminbalan/bff.proxy:v1.0.0`). This is the shape you'd use when the SPA
is built by its own CI job/repo and you just need to publish a combined image at the end.

## Build

```bash
# from this directory
docker build -t bff-proxy-with-spa:local .
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
  bff-proxy-with-spa:local
```

Open `http://localhost:8080/` — you get the sample page from `dist/index.html` instead of the
base image's plain 404 (which is what you'd get with no `wwwroot` at all). `GET /bff/*` and
`GET /api/**` are unaffected; only unmatched routes fall back to the SPA's `index.html`.

Notice there's no `Cors:AllowedOrigins` set here — none is needed, since the SPA and the BFF are
now served from the exact same origin.
