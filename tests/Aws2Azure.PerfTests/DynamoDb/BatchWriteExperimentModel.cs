using System.Diagnostics;
using System.Text.Json;
using Amazon.DynamoDBv2.Model;
using Aws2Azure.Modules.DynamoDb.Operations;

namespace Aws2Azure.PerfTests.DynamoDb;

internal sealed record BatchWriteExperimentPlan(int BatchSize, int Concurrency, string Kind, string Partitions, string Route)
{
    public const int MaxBatches = 128;
    public const int WarmupBatches = 4;
    public const int MaxSubmissions = 5;
    public static readonly TimeSpan DispatchDuration = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(30);

    public static int? ParseExecutionBlock(string? value)
    {
        if (value is null) return null;
        if (int.TryParse(value, out var block) && block is >= 1 and <= 4) return block;
        throw new ArgumentException("Execution block must be 1 through 4.");
    }

    public static BatchWriteExperimentPlan Parse(string? selection)
    {
        var parts = selection?.Split(':');
        if (parts is not { Length: 5 }
            || !int.TryParse(parts[0], out var size) || size is not (1 or 5 or 10 or 25)
            || !int.TryParse(parts[1], out var concurrency) || concurrency is not (1 or 2 or 5 or 8)
            || parts[2] is not ("put" or "delete" or "mixed")
            || parts[3] is not ("shared" or "distinct")
            || parts[4] is not ("proxy" or "direct"))
            throw new ArgumentException("Select size:concurrency:put|delete|mixed:shared|distinct:proxy|direct.");
        return new(size, concurrency, parts[2], parts[3], parts[4]);
    }

    public BatchWriteExperimentItem[] Items(int batch)
    {
        if (batch < 0 || batch >= MaxBatches + WarmupBatches)
            throw new ArgumentOutOfRangeException(nameof(batch));
        return Enumerable.Range(0, BatchSize).Select(index =>
        {
            var id = $"b{batch:D3}i{index:D2}";
            var pk = Partitions == "shared" ? "partition" : id;
            var delete = Kind == "delete" || (Kind == "mixed" && index % 2 == 0);
            return new BatchWriteExperimentItem(pk, id, delete);
        }).ToArray();
    }
}

internal sealed record BatchWriteExperimentItem(string Pk, string Id, bool Delete)
{
    public string CosmosPk => Encode(Pk);
    public string CosmosId => Encode(Id);

    private static string Encode(string value)
    {
        using var document = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(value));
        if (!KeyScalarCodec.TryEncode("S", new ParsedAttributeValue("S", document.RootElement), "key", out var encoded, out var error))
            throw new InvalidOperationException(error);
        return encoded;
    }

    public Dictionary<string, AttributeValue> Key() => new()
    {
        ["pk"] = new() { S = Pk },
        ["sk"] = new() { S = Id },
    };

    public WriteRequest Write()
    {
        if (Delete) return new WriteRequest { DeleteRequest = new DeleteRequest { Key = Key() } };
        var item = Key();
        item["payload"] = new() { S = new string('x', 256) };
        return new WriteRequest { PutRequest = new PutRequest { Item = item } };
    }

    public byte[] CosmosDocument()
    {
        using var item = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(new
        {
            pk = new { S = Pk }, sk = new { S = Id }, payload = new { S = new string('x', 256) },
        }));
        return ItemHandlers.BuildItemDocumentBytes(CosmosId, CosmosPk, item.RootElement);
    }

    public static string Identity(WriteRequest request)
    {
        var item = request.DeleteRequest?.Key ?? request.PutRequest?.Item
            ?? throw new InvalidOperationException("Missing write request.");
        return item["pk"].S + "/" + item["sk"].S;
    }
}

internal sealed class BatchWriteExperimentAccounting
{
    private readonly object _gate = new();
    private readonly List<double> _latencies = new(BatchWriteExperimentPlan.MaxBatches);
    private readonly List<double> _itemLatencies = new(BatchWriteExperimentPlan.MaxBatches * 25);
    private int _started, _completed, _failed, _items, _submissions, _resubmittedItems, _unprocessed;
    private double _backoffMs;
    private int _initialRequests, _resubmissionRequests;

    public void Attempt(bool retry)
    {
        lock (_gate)
        {
            if (_initialRequests + _resubmissionRequests >= BatchWriteExperimentPlan.MaxBatches * BatchWriteExperimentPlan.MaxSubmissions)
                throw new InvalidOperationException("Submission attempt budget exhausted.");
            if (retry) _resubmissionRequests++; else _initialRequests++;
        }
    }

    public void Started()
    {
        lock (_gate)
        {
            if (_started >= BatchWriteExperimentPlan.MaxBatches)
                throw new InvalidOperationException("Batch inventory exhausted.");
            _started++;
        }
    }

    public void Submission(int submitted, int unprocessed, bool retry)
    {
        if (submitted < 1 || unprocessed < 0 || unprocessed > submitted)
            throw new ArgumentOutOfRangeException(nameof(unprocessed));
        lock (_gate)
        {
            _submissions++;
            _items += submitted - unprocessed;
            _unprocessed += unprocessed;
            if (retry) _resubmittedItems += submitted;
        }
    }

    public void Backoff(double milliseconds) { lock (_gate) _backoffMs += milliseconds; }

    public void Acknowledged(int count, double milliseconds)
    {
        if (count < 0 || milliseconds < 0 || !double.IsFinite(milliseconds))
            throw new ArgumentOutOfRangeException(nameof(count));
        lock (_gate)
        {
            if (_itemLatencies.Count + count > BatchWriteExperimentPlan.MaxBatches * 25)
                throw new InvalidOperationException("Item latency inventory exhausted.");
            for (var i = 0; i < count; i++) _itemLatencies.Add(milliseconds);
        }
    }

    public void Settled(double milliseconds, bool failed)
    {
        lock (_gate)
        {
            if (_latencies.Count >= _started)
                throw new InvalidOperationException("More settled batches than started batches.");
            _latencies.Add(milliseconds);
            if (failed) _failed++; else _completed++;
        }
    }

    public object Snapshot(double seconds)
    {
        if (seconds <= 0) throw new ArgumentOutOfRangeException(nameof(seconds));
        lock (_gate) return new
        {
            startedBatches = _started, completedBatches = _completed, failedBatches = _failed,
            unresolvedBatches = _started - _completed - _failed,
            acknowledgedItems = _items, submissions = _submissions, resubmittedItems = _resubmittedItems,
            initialRequests = _initialRequests, resubmissionRequests = _resubmissionRequests,
            requestsWithoutAcknowledgement = _initialRequests + _resubmissionRequests - _submissions,
            unprocessedItemOccurrences = _unprocessed, cumulativeBackoffMs = _backoffMs,
            settledSeconds = seconds, batchesPerSecond = _completed / seconds,
            acknowledgedItemsPerSecond = _items / seconds,
            batchLatencyMs = Distribution(_latencies),
            itemAcknowledgementLatencyMs = Distribution(_itemLatencies),
        };
    }

    public static object Distribution(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        double? Percentile(double p) => sorted.Length == 0 ? null : sorted[(int)Math.Ceiling(p * sorted.Length) - 1];
        return new { count = sorted.Length, p50 = Percentile(.5), p95 = Percentile(.95), p99 = Percentile(.99), max = Percentile(1) };
    }

    public static async Task DrainAsync(
        List<WriteRequest> initial,
        Func<List<WriteRequest>, CancellationToken, Task<List<WriteRequest>>> submit,
        BatchWriteExperimentAccounting accounting,
        CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        var pending = initial;
        var allowed = initial.Select(BatchWriteExperimentItem.Identity).ToHashSet(StringComparer.Ordinal);
        if (allowed.Count != initial.Count) throw new InvalidOperationException("Duplicate inventory keys.");
        for (var attempt = 0; attempt < BatchWriteExperimentPlan.MaxSubmissions; attempt++)
        {
            accounting.Attempt(attempt != 0);
            var remaining = await submit(pending, ct);
            var returned = remaining.Select(BatchWriteExperimentItem.Identity).ToHashSet(StringComparer.Ordinal);
            if (returned.Count != remaining.Count || !returned.IsSubsetOf(allowed))
                throw new InvalidOperationException("UnprocessedItems is not a unique subset of the submitted inventory.");
            accounting.Submission(pending.Count, remaining.Count, attempt != 0);
            accounting.Acknowledged(pending.Count - remaining.Count, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            if (remaining.Count == 0) return;
            pending = remaining;
            allowed = returned;
            if (attempt + 1 < BatchWriteExperimentPlan.MaxSubmissions)
            {
                var start = Stopwatch.GetTimestamp();
                await Task.Delay(25 << attempt, ct);
                accounting.Backoff(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            }
        }
        throw new InvalidOperationException("UnprocessedItems retry budget exhausted.");
    }
}
