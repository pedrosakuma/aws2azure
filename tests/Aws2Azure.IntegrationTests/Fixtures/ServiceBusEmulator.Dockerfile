# Stage 1 — borrow a Linux shell to set world-readable perms on the
# config file (the final emulator image is distroless, so we can't
# RUN chmod there). Using `--chmod=0644` on COPY would be simpler but
# requires BuildKit, which Testcontainers' classic build client does
# not enable.
FROM alpine:3.19 AS perms
COPY ServiceBusEmulatorConfig.json /staging/Config.json
RUN chmod 0644 /staging/Config.json

FROM mcr.microsoft.com/azure-messaging/servicebus-emulator:latest
COPY --from=perms /staging/Config.json /ServiceBus_Emulator/ConfigFiles/Config.json
