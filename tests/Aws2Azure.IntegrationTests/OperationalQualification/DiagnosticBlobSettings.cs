using System.Diagnostics;
using System.Text.Json;

namespace Aws2Azure.IntegrationTests.OperationalQualification;

internal static class DiagnosticBlobSettings
{
    internal static ProcessStartInfo CreateReadStartInfo(string account, string resourceGroup)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(account);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceGroup);
        var start = new ProcessStartInfo("az")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in new[]
        {
            "storage", "account", "blob-service-properties", "show",
            "--account-name", account, "--resource-group", resourceGroup,
            "--query", "{blob:deleteRetentionPolicy.enabled,container:containerDeleteRetentionPolicy.enabled,versioning:isVersioningEnabled}",
            "--output", "json", "--only-show-errors",
        })
            start.ArgumentList.Add(argument);
        return start;
    }

    internal static async Task VerifyAsync(ProcessStartInfo start, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(S3Crossover.InventoryTimeout);
        deadline.Token.ThrowIfCancellationRequested();
        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Could not start the Blob management-property read.");
        var output = process.StandardOutput.ReadToEndAsync(deadline.Token);
        var error = process.StandardError.ReadToEndAsync(deadline.Token);
        try
        {
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            await Task.WhenAll(output, error).ConfigureAwait(false);
            if (process.ExitCode != 0)
                throw new InvalidOperationException("Blob management-property read failed.");
            Validate(await output.ConfigureAwait(false));
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    internal static void Validate(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || root.EnumerateObject().Count() != 3
            || !Disabled("blob") || !Disabled("container") || !Disabled("versioning"))
            throw new InvalidDataException("Diagnostic requires explicit management-plane confirmation that blob/container soft delete and versioning are disabled.");

        bool Disabled(string name) =>
            root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.False;
    }
}
