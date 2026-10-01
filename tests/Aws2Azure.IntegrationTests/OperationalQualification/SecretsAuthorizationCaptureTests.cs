using System.Diagnostics;
using System.Text.Json;
using Aws2Azure.IntegrationTests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using Xunit;

namespace Aws2Azure.IntegrationTests.OperationalQualification;

[Trait("Category", "RcObservationOffline")]
public sealed class SecretsAuthorizationCaptureTests
{
    private const string RequestId = "0HN8ABCDEFG12:00000001";
    private const string UpstreamId = "e139d1ba-991c-47b8-a6b0-2768b8a6288a";
    private const string Header = "warn: " + SecretsAuthorizationCapture.Category + "[6]";
    private const string Message = "Secrets Manager Key Vault authorization failed. Operation=UpdateSecret "
        + "RequestId=" + RequestId + " UpstreamStatus=403 UpstreamRequestId=" + UpstreamId;
    private const string Payload = "      " + Message;

    [Theory]
    [InlineData(5, 400)]
    [InlineData(5, 401)]
    [InlineData(5, 429)]
    [InlineData(5, 503)]
    [InlineData(6, 401)]
    [InlineData(6, 403)]
    public void Real_simple_console_formatter_round_trips_allowlisted_events(int eventId, int status)
    {
        using var services = new ServiceCollection().AddLogging(logging => logging.AddSimpleConsole())
            .BuildServiceProvider();
        var formatter = services.GetServices<ConsoleFormatter>().Single(item => item.Name == "simple");
        var message = eventId == 5
            ? $"Secrets Manager Entra token acquisition failed. Operation=UpdateSecret RequestId={RequestId} TokenStatus={status} UpstreamRequestId=unavailable"
            : Message.Replace("403", status.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
        var entry = new LogEntry<string>(LogLevel.Warning, SecretsAuthorizationCapture.Category,
            new EventId(eventId), message, null, static (state, _) => state);
        using var output = new StringWriter();
        formatter.Write(entry, null, output);
        var capture = new SecretsAuthorizationCapture();
        using var lines = new StringReader(output.ToString());
        while (lines.ReadLine() is { } line) capture.Observe(line, false);
        var result = Assert.Single(capture.Snapshot().Events);
        Assert.Equal(eventId, result.EventId);
        Assert.Equal(eventId == 5 ? "entra" : "key_vault", result.Source);
        Assert.Equal(status, result.UpstreamStatus);
        Assert.Equal(RequestId, result.RequestId);
        Assert.Equal(eventId == 5 ? "unavailable" : UpstreamId, result.UpstreamRequestId);
        Assert.Equal(0, capture.Snapshot().RejectedCandidates);
    }

    [Theory]
    [InlineData("suffix")]
    [InlineData("newline")]
    [InlineData("operation")]
    [InlineData("request")]
    [InlineData("upstream")]
    [InlineData("status")]
    [InlineData("oversize")]
    [InlineData("empty")]
    [InlineData("missing")]
    [InlineData("wrong-shape")]
    public void Malformed_candidates_never_export_untrusted_text(string scenario)
    {
        const string secret = "credential-secret";
        var payload = scenario switch
        {
            "suffix" => Payload + " " + secret,
            "newline" => Payload + "\n" + secret,
            "operation" => Payload.Replace("UpdateSecret", secret, StringComparison.Ordinal),
            "request" => Payload.Replace(RequestId, secret, StringComparison.Ordinal),
            "upstream" => Payload.Replace(UpstreamId, "https://vault-private/secret", StringComparison.Ordinal),
            "status" => Payload.Replace("403", "429", StringComparison.Ordinal),
            "oversize" => Payload + new string('x', SecretsAuthorizationCapture.LineLimit),
            "empty" => "",
            "missing" => null,
            _ => Payload.Replace("UpstreamStatus", "TokenStatus", StringComparison.Ordinal),
        };
        var capture = new SecretsAuthorizationCapture();
        capture.Observe(Header, false);
        capture.Observe(payload, false);
        var evidence = capture.Snapshot();
        Assert.Empty(evidence.Events);
        Assert.Equal(1, evidence.RejectedCandidates);
        var report = new SecretsConcurrencyReport
        {
            Slots = [new() { AuthorizationEvidence = evidence }],
        };
        var json = JsonSerializer.Serialize(report, SecretsConcurrencyJsonContext.Default.SecretsConcurrencyReport);
        Assert.DoesNotContain(secret, json, StringComparison.Ordinal);
        Assert.DoesNotContain("vault-private", json, StringComparison.Ordinal);
        Assert.DoesNotContain("UpstreamStatus", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Streams_are_independent_and_other_logger_headers_cannot_authorize_payloads()
    {
        var capture = new SecretsAuthorizationCapture();
        capture.Observe(Header, false);
        capture.Observe(Payload, true);
        Assert.Empty(capture.Snapshot().Events);
        capture.Observe("warn: other.logger[6]", false);
        capture.Observe(Payload, false);
        capture.Observe("info: " + SecretsAuthorizationCapture.Category + "[6]", false);
        capture.Observe(Payload, false);
        Assert.Empty(capture.Snapshot().Events);
        capture.Observe(Header, true);
        capture.Observe(Payload, true);
        Assert.Single(capture.Snapshot().Events);
        Assert.Equal(1, capture.Snapshot().RejectedCandidates);
    }

    [Fact]
    public void Storage_is_bounded_and_snapshots_do_not_change_after_publication()
    {
        var capture = new SecretsAuthorizationCapture();
        capture.Observe(Header, false);
        capture.Observe(Payload, false);
        var early = capture.Snapshot();
        for (var index = 0; index < SecretsAuthorizationCapture.EventLimit + 4; index++)
        {
            capture.Observe(Header, true);
            capture.Observe(Payload, true);
        }
        capture.Observe(null, false);
        Assert.False(capture.Snapshot().StreamsClosed);
        capture.Observe(null, true);
        var final = capture.Snapshot();
        Assert.Single(early.Events);
        Assert.False(early.Truncated);
        Assert.False(early.StreamsClosed);
        Assert.Equal(SecretsAuthorizationCapture.EventLimit, final.Events.Length);
        Assert.Equal(5, final.DroppedEvents);
        Assert.True(final.Truncated);
        Assert.True(final.StreamsClosed);
        Assert.Equal(0, final.RejectedCandidates);
    }

    [Fact]
    public async Task Exited_process_is_drained_before_snapshot()
    {
        if (!OperatingSystem.IsLinux()) return;
        var start = new ProcessStartInfo("/bin/sh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("printf '%s\\n' \"$1\" \"$2\"");
        start.ArgumentList.Add("capture-test");
        start.ArgumentList.Add(Header);
        start.ArgumentList.Add(Payload);
        var process = Process.Start(start)!;
        var capture = new SecretsAuthorizationCapture();
        _ = new SecretsManagerRealAzureProxyFixture.ProxyInstance(process, "", "", "",
            Aws2Azure.TestSupport.OperationalQualification.SealedRuntimeRole.Candidate, capture);
        await process.WaitForExitAsync();
        await SecretsManagerRealAzureProxyFixture.StopDiagnosticProcessAsync(process);
        var evidence = capture.Snapshot();
        Assert.True(evidence.StreamsClosed);
        Assert.Single(evidence.Events);
    }

    [Fact]
    public async Task Graceful_shutdown_retains_events_written_during_termination()
    {
        if (!OperatingSystem.IsLinux()) return;
        var start = new ProcessStartInfo("/bin/sh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("trap 'printf \"%s\\n\" \"$1\" \"$2\"; exit 0' TERM; printf 'ready\\n'; while :; do sleep 0.1; done");
        start.ArgumentList.Add("capture-test");
        start.ArgumentList.Add(Header);
        start.ArgumentList.Add(Payload);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var process = Process.Start(start)!;
        process.OutputDataReceived += (_, args) =>
        {
            if (args.Data == "ready") ready.TrySetResult();
        };
        var capture = new SecretsAuthorizationCapture();
        _ = new SecretsManagerRealAzureProxyFixture.ProxyInstance(process, "", "", "",
            Aws2Azure.TestSupport.OperationalQualification.SealedRuntimeRole.Candidate, capture);
        try
        {
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            await SecretsManagerRealAzureProxyFixture.StopDiagnosticProcessAsync(process);
        }
        Assert.True(capture.Snapshot().StreamsClosed);
        Assert.Single(capture.Snapshot().Events);
    }
}
