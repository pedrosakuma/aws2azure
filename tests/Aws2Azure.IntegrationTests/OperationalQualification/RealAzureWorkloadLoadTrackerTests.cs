using System.Net;
using System.Text.Json;
using Xunit;

namespace Aws2Azure.IntegrationTests.OperationalQualification;

public sealed class RealAzureWorkloadLoadTrackerTests
{
    [Fact]
    public void First_failure_detail_is_sanitized_and_structured()
    {
        var tracker = new RealAzureWorkloadLoadTracker(
            "secretsmanager",
            ["GetSecretValue"]);
        var raw = "secret-name https://vault.example/secrets/name?token=abc";

        tracker.RecordFailure(
            "GetSecretValue",
            12,
            throttled: true,
            new HttpRequestException(raw, null, HttpStatusCode.TooManyRequests));

        var failure = tracker.FirstFailureDetail("GetSecretValue");
        Assert.NotNull(failure);
        Assert.Equal("throttle", failure!.Category);
        Assert.Equal(429, failure.StatusCode);
        Assert.Equal("HttpRequestException", failure.ErrorCode);
        var rendered = tracker.FirstFailure("GetSecretValue");
        Assert.DoesNotContain("secret-name", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("vault.example", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("token", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void Capture_json_serializes_structured_diagnostics_without_raw_failure_text()
    {
        var tracker = new RealAzureWorkloadLoadTracker(
            "secretsmanager",
            ["GetSecretValue"]);
        var raw = "secret-name https://vault.example/secrets/name?token=abc";
        tracker.RecordFailure(
            "GetSecretValue",
            12,
            throttled: false,
            new HttpRequestException(raw, null, HttpStatusCode.ServiceUnavailable));
        var evidence = new RcObservationCaptureEvidence
        {
            Cohorts =
            [
                new RcObservationCaptureCohort
                {
                    Id = "candidate",
                    Role = "candidate",
                    OperationDiagnostics =
                        RcObservationCaptureWriter.OperationDiagnostics(tracker),
                },
            ],
        };

        var json = JsonSerializer.Serialize(
            evidence,
            RcObservationCaptureJsonContext.Default.RcObservationCaptureEvidence);

        Assert.Contains("operation_diagnostics", json, StringComparison.Ordinal);
        Assert.Contains("first_failure", json, StringComparison.Ordinal);
        Assert.Contains("status_code", json, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-name", json, StringComparison.Ordinal);
        Assert.DoesNotContain("vault.example", json, StringComparison.Ordinal);
        Assert.DoesNotContain("token", json, StringComparison.Ordinal);
    }
}
