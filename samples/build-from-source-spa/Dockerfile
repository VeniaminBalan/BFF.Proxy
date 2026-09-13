# Builds the SPA from source in a Node stage, then layers its output onto the BFF image - one
# `docker build` produces the final combined image, no separate frontend CI run required.
ARG BASE_IMAGE=bff-proxy:local

FROM node:22-alpine AS spa-build
WORKDIR /spa
COPY app/package.json ./
RUN npm install
COPY app/ ./
RUN npm run build

FROM ${BASE_IMAGE}
COPY --from=spa-build /spa/dist/ ./wwwroot/
