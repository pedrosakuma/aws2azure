using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Aws2Azure.Core.Observability;

namespace Aws2Azure.Modules.DynamoDb.Operations;

internal static class BatchWriteDiagnostics
{
    private static readonly bool Enabled =
        Environment.GetEnvironmentVariable("AWS2AZURE_BATCH_DIAGNOSTICS") == "1";
    private static readonly Meter? Meter = Enabled ? new(ProxyMetrics.MeterName) : null;
    private static readonly Histogram<double>? Duration = Meter?.CreateHistogram<double>(
        "aws2azure_batch_write_stage_seconds", "s", "Opt-in BatchWriteItem stage wall time; overlapping items are not additive request time");

    public static Scope Measure(string stage) => new(Enabled ? stage : null);

    internal readonly struct Scope : IDisposable
    {
        private readonly string? _stage;
        private readonly long _start;

        internal Scope(string? stage)
        {
            _stage = stage;
            _start = stage is null ? 0 : Stopwatch.GetTimestamp();
        }

        public void Dispose()
        {
            if (_stage is not null)
                Duration!.Record(Stopwatch.GetElapsedTime(_start).TotalSeconds,
                    new KeyValuePair<string, object?>("stage", _stage));
        }
    }
}
