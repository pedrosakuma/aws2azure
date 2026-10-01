using System.Diagnostics;
using Xunit;

namespace Aws2Azure.IntegrationTests.OperationalQualification;

[Trait("Category", "RcObservationOffline")]
public sealed class DiagnosticBlobSettingsTests
{
    [Fact]
    public void Management_read_targets_the_owned_account_without_shell_or_mutations()
    {
        var start = DiagnosticBlobSettings.CreateReadStartInfo("diagnostic", "owned-rg");
        Assert.Equal("az", start.FileName);
        Assert.False(start.UseShellExecute);
        Assert.True(start.RedirectStandardOutput && start.RedirectStandardError);
        Assert.Equal(new[]
        {
            "storage", "account", "blob-service-properties", "show",
            "--account-name", "diagnostic", "--resource-group", "owned-rg",
            "--query", "{blob:deleteRetentionPolicy.enabled,container:containerDeleteRetentionPolicy.enabled,versioning:isVersioningEnabled}",
            "--output", "json", "--only-show-errors",
        }, start.ArgumentList);
    }

    [Fact]
    public void Explicit_disabled_management_properties_are_accepted() =>
        DiagnosticBlobSettings.Validate("""{"blob":false,"container":false,"versioning":false}""");

    [Theory]
    [InlineData("blob")]
    [InlineData("container")]
    [InlineData("versioning")]
    public void Enabled_missing_null_and_non_boolean_properties_fail_closed(string name)
    {
        const string valid = """{"blob":false,"container":false,"versioning":false}""";
        foreach (var value in new[] { "true", "null", "\"false\"", "0", "{}", "[]" })
            Assert.Throws<InvalidDataException>(() =>
                DiagnosticBlobSettings.Validate(valid.Replace($"\"{name}\":false", $"\"{name}\":{value}", StringComparison.Ordinal)));
        var missing = name switch
        {
            "blob" => """{"container":false,"versioning":false}""",
            "container" => """{"blob":false,"versioning":false}""",
            _ => """{"blob":false,"container":false}""",
        };
        Assert.Throws<InvalidDataException>(() => DiagnosticBlobSettings.Validate(missing));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("""{"blob":true,"blob":false,"container":false,"versioning":false}""")]
    public void Empty_or_wrong_root_is_not_disabled_configuration(string json) =>
        Assert.Throws<InvalidDataException>(() => DiagnosticBlobSettings.Validate(json));

    [SkippableTheory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Cli_exit_failure_cannot_be_hidden_by_valid_output(int exitCode)
    {
        Skip.If(OperatingSystem.IsWindows(), "Uses a local POSIX shell fixture, never Azure CLI.");
        var start = Shell("""printf '%s' '{"blob":false,"container":false,"versioning":false}'; printf 'private-error' >&2; exit "$1" """);
        start.ArgumentList.Add("fixture");
        start.ArgumentList.Add(exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (exitCode == 0)
            await DiagnosticBlobSettings.VerifyAsync(start, CancellationToken.None);
        else
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                DiagnosticBlobSettings.VerifyAsync(start, CancellationToken.None));
            Assert.DoesNotContain("private-error", error.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Pre_cancelled_read_never_starts_a_process()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            DiagnosticBlobSettings.VerifyAsync(new ProcessStartInfo("must-not-exist"), cancellation.Token));
    }

    [SkippableFact]
    public async Task Cancellation_terminates_the_specific_read_process()
    {
        Skip.If(OperatingSystem.IsWindows(), "Uses a local POSIX shell fixture, never Azure CLI.");
        var pidFile = Path.GetTempFileName();
        using var cancellation = new CancellationTokenSource();
        Task? read = null;
        try
        {
            var start = Shell("""printf '%s' "$$" > "$1"; exec sleep 60""");
            start.ArgumentList.Add("fixture");
            start.ArgumentList.Add(pidFile);
            read = DiagnosticBlobSettings.VerifyAsync(start, cancellation.Token);
            using var ready = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            string pid;
            do
            {
                await Task.Delay(10, ready.Token);
                pid = await File.ReadAllTextAsync(pidFile, ready.Token);
            } while (string.IsNullOrEmpty(pid));
            using var process = Process.GetProcessById(int.Parse(pid, System.Globalization.CultureInfo.InvariantCulture));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
            Assert.True(process.HasExited);
        }
        finally
        {
            cancellation.Cancel();
            if (read is not null)
                await Record.ExceptionAsync(() => read);
            File.Delete(pidFile);
        }
    }

    private static ProcessStartInfo Shell(string command)
    {
        var start = new ProcessStartInfo("/bin/sh")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(command);
        return start;
    }
}
