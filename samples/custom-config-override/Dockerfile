# Bakes environment-specific, non-secret configuration (CORS origins, reverse-proxy destinations)
# into the image itself instead of passing it all as environment variables at `docker run` time.
# Useful when an environment's config is fixed at build time (e.g. one image per deployment
# target) rather than injected by the orchestrator.
#
# Secrets (Keycloak__credentials__secret, Redis connection string, etc.) should still be supplied
# as environment variables / orchestrator secrets at runtime, never baked into the image.
ARG BASE_IMAGE=bff-proxy:local
FROM ${BASE_IMAGE}

COPY appsettings.Production.json ./appsettings.Production.json
