namespace Aws2Azure.Modules.Sns.EventGrid;

internal sealed record EventGridPublishDestination(
    string Endpoint,
    string? AccessKey,
    string? TenantId,
    string? ClientId,
    string? ClientSecret);
