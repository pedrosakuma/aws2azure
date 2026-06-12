using Aws2Azure.Core.Configuration;

namespace Aws2Azure.Core.Azure;

public readonly record struct AadAuthSettings(
    AzureAuthMode Mode,
    string? TenantId,
    string? ClientId,
    string? ClientSecret);
