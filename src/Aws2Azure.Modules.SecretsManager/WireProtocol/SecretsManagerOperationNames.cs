using System.Collections.Generic;

namespace Aws2Azure.Modules.SecretsManager.WireProtocol;

internal enum SecretsManagerOperation
{
    Unknown = 0,
    GetSecretValue,
    CreateSecret,
    PutSecretValue,
    UpdateSecret,
    DeleteSecret,
    ListSecrets,
    DescribeSecret,
    RotateSecret,
}

internal static class SecretsManagerOperationNames
{
    private static readonly Dictionary<string, SecretsManagerOperation> Map = new(System.StringComparer.Ordinal)
    {
        ["GetSecretValue"] = SecretsManagerOperation.GetSecretValue,
        ["CreateSecret"] = SecretsManagerOperation.CreateSecret,
        ["PutSecretValue"] = SecretsManagerOperation.PutSecretValue,
        ["UpdateSecret"] = SecretsManagerOperation.UpdateSecret,
        ["DeleteSecret"] = SecretsManagerOperation.DeleteSecret,
        ["ListSecrets"] = SecretsManagerOperation.ListSecrets,
        ["DescribeSecret"] = SecretsManagerOperation.DescribeSecret,
        // Recognised for routing/metrics but deliberately unsupported: a stateless
        // wire-protocol proxy cannot orchestrate AWS rotation Lambda functions.
        // Dispatch rejects it with a NotImplementedException (see the module + the
        // RotateSecret gap doc) rather than silently translating to Key Vault.
        ["RotateSecret"] = SecretsManagerOperation.RotateSecret,
    };

    /// <summary>
    /// All recognised Secrets Manager target short-names. The module's
    /// <c>KnownOperations</c> allowlist and dispatch gate derive from this
    /// wire-protocol table so routing and metrics labels cannot drift.
    /// </summary>
    public static IReadOnlyCollection<string> Names => Map.Keys;

    public static SecretsManagerOperation Resolve(string? operationName)
    {
        if (string.IsNullOrEmpty(operationName))
        {
            return SecretsManagerOperation.Unknown;
        }

        return Map.TryGetValue(operationName, out var operation)
            ? operation
            : SecretsManagerOperation.Unknown;
    }

    public static string ToShortName(SecretsManagerOperation operation)
        => operation == SecretsManagerOperation.Unknown ? "unknown" : operation.ToString();
}
