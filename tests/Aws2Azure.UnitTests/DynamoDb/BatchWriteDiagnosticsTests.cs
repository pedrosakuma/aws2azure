using System.Diagnostics.Metrics;
using Aws2Azure.Modules.DynamoDb.Operations;
using Xunit;

namespace Aws2Azure.UnitTests.DynamoDb;

public sealed class BatchWriteDiagnosticsTests
{
    [Fact]
    public void Opt_in_uses_existing_meter_with_only_stage_tags_and_disabled_scope_allocates_nothing()
    {
        var enabled = Environment.GetEnvironmentVariable("AWS2AZURE_BATCH_DIAGNOSTICS") == "1";
        var observed = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, owner) =>
        {
            if (instrument.Name == "aws2azure_batch_write_stage_seconds")
            {
                Assert.Equal("Aws2Azure.Proxy", instrument.Meter.Name);
                owner.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<double>((_, value, tags, _) =>
        {
            Assert.True(value >= 0);
            Assert.Equal(1, tags.Length);
            Assert.Equal("stage", tags[0].Key);
            Interlocked.Increment(ref observed);
        });
        listener.Start();
        using (BatchWriteDiagnostics.Measure("envelope_parse")) { }
        var start = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++)
            using (BatchWriteDiagnostics.Measure("envelope_parse")) { }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - start;
        if (enabled) Assert.True(observed >= 1001);
        else
        {
            Assert.Equal(0, observed);
            Assert.Equal(0, allocated);
        }
    }
}
