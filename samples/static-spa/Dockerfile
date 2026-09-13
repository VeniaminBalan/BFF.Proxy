# Simplest same-origin setup: the SPA is already built (e.g. by a separate CI job/repo) and you
# just need to layer its static output onto the BFF image.
#
# Swap `bff-proxy:local` for `ghcr.io/veniaminbalan/bff.proxy:v1.0.0` to use the
# published base image, and swap `dist/` for wherever your frontend's build actually drops its
# output.
ARG BASE_IMAGE=bff-proxy:local
FROM ${BASE_IMAGE}

# The base image ships no wwwroot of its own, so this is what turns the fallback route from a
# 404 no-op into an actual SPA shell.
COPY dist/ ./wwwroot/
