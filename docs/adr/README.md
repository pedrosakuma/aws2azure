# Architecture Decision Records

Accepted architecture decisions for `aws2azure`. ADRs are append-only: when a
decision changes, add a new ADR that supersedes the old one rather than editing
history.

| ADR | Decision | Status |
|---|---|---|
| [0001](0001-sb-rest-runtime-protocol.md) | Service Bus runtime protocol: REST, not AMQP | Accepted |
| [0002](0002-amqp-client-library.md) | Hand-rolled AMQP 1.0 client library | Accepted |
| [0003](0003-aws-to-azure-only.md) | AWS-to-Azure translation only | Accepted |
| [0004](0004-wire-protocol-sidecar-translator.md) | Wire-protocol sidecar translator | Accepted |
| [0005](0005-sidecar-resource-budget.md) | Sidecar-first resource budget | Accepted |
| [0006](0006-dotnet-native-aot-runtime.md) | .NET with Native AOT | Accepted |
| [0007](0007-single-binary-service-multiplexing.md) | Single binary with service multiplexing | Accepted |
| [0008](0008-direct-azure-rest-integration.md) | Direct Azure REST integration | Accepted |
| [0009](0009-aws-wire-protocol-without-aws-sdk.md) | AWS wire protocol without AWS SDK dependencies | Accepted |
| [0010](0010-static-credential-mapping.md) | Static credential mapping | Accepted |
| [0011](0011-gap-docs-as-source-of-truth.md) | Gap docs as the capability source of truth | Accepted |
| [0012](0012-greenfield-implementation.md) | Greenfield implementation with no reused proxy code | Accepted |
| [0013](0013-manual-composition-and-aot-safe-di.md) | Manual composition and AOT-safe dependency injection | Accepted |
| [0014](0014-system-text-json-source-generation.md) | System.Text.Json source generation | Accepted |
| [0015](0015-streaming-xml-reader-writer.md) | Streaming XML with XmlReader and XmlWriter | Accepted |
| [0016](0016-loggermessage-source-generated-logging.md) | LoggerMessage source-generated logging | Accepted |
| [0017](0017-manual-resilience-policies.md) | Manual resilience policies instead of Polly | Accepted |
