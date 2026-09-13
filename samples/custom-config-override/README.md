# custom-config-override

Bakes a per-environment `appsettings.Production.json` (CORS origins, YARP reverse-proxy
destinations) into a downstream image, instead of setting every value via `Section__Key`
environment variables at `docker run` time. Handy when you build one image per deployment target
and want its non-secret config to travel with the image.

Secrets (`Keycloak__credentials__secret`, the Redis connection string, ...) are **not** included
in `appsettings.Production.json` here - keep those as runtime environment variables/orchestrator
secrets regardless of this pattern.

## Build

```bash
# from this directory
docker build -t bff-proxy-prod-config:local .
```

## Run

```bash
docker run --rm -p 8080:8080 \
  -e ASPNETCORE_ENVIRONMENT=Production \
  -e ConnectionStrings__Redis=host.docker.internal:6379,abortConnect=false \
  -e Keycloak__realm=<your realm> \
  -e Keycloak__auth-server-url=http://host.docker.internal:7080 \
  -e Keycloak__resource=<your client id> \
  -e Keycloak__credentials__secret=<your client secret> \
  bff-proxy-prod-config:local
```

`ASPNETCORE_ENVIRONMENT=Production` makes ASP.NET Core layer `appsettings.Production.json` over
`appsettings.json`, so the baked-in `Cors:AllowedOrigins` and `ReverseProxy:BackendAddress` values
apply without needing to pass them as environment variables. Only the backend address is
configurable this way - the route's path match and `AuthorizationPolicy` are defined in code
(`BffServiceCollectionExtensions.cs`) and can't be changed via config.
