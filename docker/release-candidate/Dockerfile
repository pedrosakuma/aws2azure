# syntax=docker/dockerfile:1

# Release-candidate images consume one already-produced Native-AOT executable.
# The workflow overrides this pinned multi-architecture default with the pinned
# matching platform-manifest digest.
ARG RUNTIME_BASE=mcr.microsoft.com/dotnet/runtime-deps:10.0-noble-chiseled@sha256:bc6ba0158e93277ca1a5bc0881d13b08e6ca7f6d98db623592095eaf7fd7a816
FROM ${RUNTIME_BASE}

ARG ARCHIVE_CONTENT_DIGEST
ARG BASE_IMAGE_DIGEST
ARG CANDIDATE
ARG SOURCE_SHA

LABEL org.opencontainers.image.source="https://github.com/pedrosakuma/aws2azure" \
      org.opencontainers.image.description="AWS to Azure transparent protocol proxy" \
      org.opencontainers.image.licenses="MIT" \
      org.opencontainers.image.version="${CANDIDATE}" \
      org.opencontainers.image.revision="${SOURCE_SHA}" \
      org.opencontainers.image.base.name="mcr.microsoft.com/dotnet/runtime-deps:10.0-noble-chiseled" \
      org.opencontainers.image.base.digest="${BASE_IMAGE_DIGEST}" \
      io.aws2azure.release-candidate.archive-content-digest="${ARCHIVE_CONTENT_DIGEST}"

WORKDIR /app
COPY --chmod=0755 Aws2Azure.Proxy /app/Aws2Azure.Proxy

USER $APP_UID

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=3s --start-period=5s --retries=3 \
    CMD ["/app/Aws2Azure.Proxy", "--health-check"]

ENTRYPOINT ["/app/Aws2Azure.Proxy"]
