# aws2azure — multi-stage Native-AOT Dockerfile
# Produces a minimal chiseled (distroless) container running as non-root.

# -----------------------------------------------------------------------------
# Build stage: restore, build, publish AOT
# -----------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Native-AOT toolchain. `dotnet publish` with <PublishAot>true</PublishAot>
# shells out to clang + the platform linker and links against zlib; the base
# SDK image does not ship them.
RUN apt-get update \
    && apt-get install -y --no-install-recommends clang zlib1g-dev \
    && rm -rf /var/lib/apt/lists/*

# Docker's TARGETARCH is amd64/arm64; the matching .NET RID is x64/arm64.
# Resolve it once and persist for both the restore and publish layers.
ARG TARGETARCH=amd64
RUN echo "linux-$(echo "${TARGETARCH}" | sed -e 's/amd64/x64/')" > /tmp/dotnet-rid

# Copy the solution, shared build props, and every project file first so the
# restore layer is cached independently of source changes.
COPY aws2azure.slnx Directory.Build.props ./
COPY src/Aws2Azure.Core/Aws2Azure.Core.csproj src/Aws2Azure.Core/
COPY src/Aws2Azure.Amqp/Aws2Azure.Amqp.csproj src/Aws2Azure.Amqp/
COPY src/Aws2Azure.Modules.S3/Aws2Azure.Modules.S3.csproj src/Aws2Azure.Modules.S3/
COPY src/Aws2Azure.Modules.Sqs/Aws2Azure.Modules.Sqs.csproj src/Aws2Azure.Modules.Sqs/
COPY src/Aws2Azure.Modules.DynamoDb/Aws2Azure.Modules.DynamoDb.csproj src/Aws2Azure.Modules.DynamoDb/
COPY src/Aws2Azure.Modules.Kinesis/Aws2Azure.Modules.Kinesis.csproj src/Aws2Azure.Modules.Kinesis/
COPY src/Aws2Azure.Modules.Sns/Aws2Azure.Modules.Sns.csproj src/Aws2Azure.Modules.Sns/
COPY src/Aws2Azure.Modules.SecretsManager/Aws2Azure.Modules.SecretsManager.csproj src/Aws2Azure.Modules.SecretsManager/
COPY src/Aws2Azure.Proxy/Aws2Azure.Proxy.csproj src/Aws2Azure.Proxy/

RUN dotnet restore src/Aws2Azure.Proxy/Aws2Azure.Proxy.csproj -r "$(cat /tmp/dotnet-rid)"

# Copy everything else and publish. PublishAot already emits a single native
# binary, so no PublishSingleFile is needed.
COPY . .
RUN dotnet publish src/Aws2Azure.Proxy \
    -c Release \
    -r "$(cat /tmp/dotnet-rid)" \
    --no-restore \
    -o /app

# -----------------------------------------------------------------------------
# Runtime stage: chiseled (distroless) image with the AOT binary
# -----------------------------------------------------------------------------
# InvariantGlobalization is enabled (Directory.Build.props) so the chiseled
# runtime-deps image — which omits ICU — is sufficient.
FROM mcr.microsoft.com/dotnet/runtime-deps:10.0-noble-chiseled AS runtime

LABEL org.opencontainers.image.source="https://github.com/pedrosakuma/aws2azure"
LABEL org.opencontainers.image.description="AWS to Azure transparent protocol proxy"
LABEL org.opencontainers.image.licenses="MIT"

WORKDIR /app

# Copy the published AOT binary.
COPY --from=build /app/Aws2Azure.Proxy .

# Run as non-root (chiseled images define $APP_UID=1654).
USER $APP_UID

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

# The binary self-handles --health-check (hits /_aws2azure/health) and returns
# a 0/1 exit code, so exec-form CMD is correct here (no shell in chiseled).
HEALTHCHECK --interval=30s --timeout=3s --start-period=5s --retries=3 \
    CMD ["/app/Aws2Azure.Proxy", "--health-check"]

ENTRYPOINT ["/app/Aws2Azure.Proxy"]
