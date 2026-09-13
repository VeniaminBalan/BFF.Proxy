# Mounts two independent SPAs on the same BFF image at different path prefixes. Each is copied
# into its own subdirectory of wwwroot instead of overwriting wwwroot's top level - any immediate
# subdirectory of wwwroot with its own index.html is auto-mounted at /<subdirectory-name>/...
# with its own client-side-routing fallback (see MapStaticSpaMounts in ../../BFF.Proxy/Extensions.cs).
#
# Swap `app/` and `admin/` for wherever each frontend's real build output lands, and add more
# COPY lines for additional apps - no BFF-side config changes needed either way.
ARG BASE_IMAGE=bff-proxy:local
FROM ${BASE_IMAGE}

COPY app/ ./wwwroot/app/
COPY admin/ ./wwwroot/admin/
