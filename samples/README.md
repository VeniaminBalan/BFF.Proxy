# Samples

`BFF.Proxy` publishes a generic image (`ghcr.io/veniaminbalan/bff.proxy`) that is a pure BFF/API
gateway on its own: no bundled frontend, CORS and CSRF fully configurable. Everything in this
folder is a *downstream* Dockerfile/compose setup — none of it lives inside `BFF.Proxy`'s own
image — showing the different ways teams can build on top of that base image.

| Sample | Shows |
|---|---|
| [`full-local-stack/`](full-local-stack) | A complete, self-contained `docker compose up` with Keycloak (realm pre-provisioned), Redis, a throwaway echo backend, and the BFF - the fastest way to exercise the whole login/CSRF/proxy flow with nothing installed by hand. |
| [`static-spa/`](static-spa) | Simplest same-origin setup: `COPY` an already-built SPA's static output into `./wwwroot`. |
| [`build-from-source-spa/`](build-from-source-spa) | Multi-stage image that builds the SPA from source (Node stage) before copying its output into `./wwwroot`, so the whole thing is one `docker build`. |
| [`multi-spa/`](multi-spa) | Mounting more than one SPA on the same image at different path prefixes (`/app`, `/admin`), each auto-discovered from its own `wwwroot` subdirectory. |
| [`proxy-only-split-origin/`](proxy-only-split-origin) | The base image used as a pure proxy with the SPA hosted on a different origin/container — `Cors:AllowedOrigins` configured, no `wwwroot` involved. |
| [`custom-config-override/`](custom-config-override) | Baking environment-specific `appsettings.*.json` (e.g. `ReverseProxy:BackendAddress`) into a downstream image instead of passing everything as environment variables. |

All samples assume the base image is available locally as `bff-proxy:local`, either built from
source or pulled from GHCR and re-tagged:

```bash
# Option A: build from source
docker build -t bff-proxy:local -f ../BFF.Proxy/Dockerfile ../BFF.Proxy

# Option B: pull the published image
docker pull ghcr.io/veniaminbalan/bff.proxy:v1.0.0
docker tag ghcr.io/veniaminbalan/bff.proxy:v1.0.0 bff-proxy:local
```

Each sample folder has its own `README.md` with the exact build/run commands.
