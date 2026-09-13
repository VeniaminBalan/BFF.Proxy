# build-from-source-spa

Builds the SPA from source in a Node stage as part of the same `docker build`, then copies the
build output into the base `bff-proxy` image's (published as
`ghcr.io/veniaminbalan/bff.proxy:v1.0.0`) `wwwroot`. Use this shape when you want a single build
pipeline producing one combined image, instead of a separate frontend CI job.

`app/` here is a stand-in for a real frontend project - `build.js` just copies `src/` to `dist/`
so the sample has zero npm dependencies. Replace `app/` with your actual Vite/CRA/Angular project
and the Dockerfile doesn't need to change (as long as `npm run build` still emits to `dist/`).

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
  bff-proxy-with-spa:local
```

Open `http://localhost:8080/` to see the built SPA page.
