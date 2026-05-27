# aws2azure — multi-stage AOT Dockerfile
# Produces a minimal container (~30-50 MB) running as non-root.

# -----------------------------------------------------------------------------
# Build stage: restore, build, publish AOT
# -----------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy solution and project files first for layer caching
COPY aws2azure.slnx Directory.Build.props Directory.Packages.props ./
COPY src/Aws2Azure.Core/Aws2Azure.Core.csproj src/Aws2Azure.Core/
COPY src/Aws2Azure.Amqp/Aws2Azure.Amqp.csproj src/Aws2Azure.Amqp/
COPY src/Aws2Azure.Modules.S3/Aws2Azure.Modules.S3.csproj src/Aws2Azure.Modules.S3/
COPY src/Aws2Azure.Modules.Sqs/Aws2Azure.Modules.Sqs.csproj src/Aws2Azure.Modules.Sqs/
COPY src/Aws2Azure.Modules.DynamoDb/Aws2Azure.Modules.DynamoDb.csproj src/Aws2Azure.Modules.DynamoDb/
COPY src/Aws2Azure.Modules.Kinesis/Aws2Azure.Modules.Kinesis.csproj src/Aws2Azure.Modules.Kinesis/
COPY src/Aws2Azure.Modules.Sns/Aws2Azure.Modules.Sns.csproj src/Aws2Azure.Modules.Sns/
COPY src/Aws2Azure.Proxy/Aws2Azure.Proxy.csproj src/Aws2Azure.Proxy/

# Restore packages
RUN dotnet restore src/Aws2Azure.Proxy/Aws2Azure.Proxy.csproj

# Copy everything else and publish
COPY . .
ARG TARGETARCH=amd64
RUN dotnet publish src/Aws2Azure.Proxy \
    -c Release \
    -r linux-${TARGETARCH} \
    --no-restore \
    -o /app \
    -p:PublishSingleFile=true

# -----------------------------------------------------------------------------
# Runtime stage: chiseled (distroless) image with AOT binary
# -----------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/runtime-deps:10.0-noble-chiseled AS runtime

# Labels for container registry
LABEL org.opencontainers.image.source="https://github.com/pedrosakuma/aws2azure"
LABEL org.opencontainers.image.description="AWS to Azure transparent protocol proxy"
LABEL org.opencontainers.image.licenses="MIT"

WORKDIR /app

# Copy published AOT binary
COPY --from=build /app/Aws2Azure.Proxy .

# Run as non-root (chiseled images use $APP_UID=1654)
USER $APP_UID

# Default port
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

# Health check (hits /_aws2azure/health)
HEALTHCHECK --interval=30s --timeout=3s --start-period=5s --retries=3 \
    CMD ["/app/Aws2Azure.Proxy", "--health-check"] || exit 1

ENTRYPOINT ["/app/Aws2Azure.Proxy"]
