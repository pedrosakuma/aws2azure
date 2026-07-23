using System;
using System.Collections.Generic;
using Aws2Azure.Modules.Sqs.Errors;
using Aws2Azure.Modules.Sqs.Operations;
using Xunit;

namespace Aws2Azure.UnitTests.Sqs;

/// <summary>
/// Pins the shared validation contract for Slice-4 batch operations
/// (DeleteMessageBatch / ChangeMessageVisibilityBatch). The receipt-handle
/// decode and per-entry SB call paths are exercised in the integration
/// suite; the shape validator runs entirely client-side and is the most
/// likely regression surface.
/// </summary>
public sealed class BatchAdminShapeTests
{
    [Fact]
    public void Empty_batch_is_rejected()
    {
        var ok = BatchAdminHandlers.ValidateBatchShape(new List<string>(), out var err);
        Assert.False(ok);
        Assert.NotNull(err);
        Assert.Equal("AWS.SimpleQueueService.EmptyBatchRequest", err!.Value.Code);
    }

    [Fact]
    public void Over_ten_entries_is_rejected()
    {
        var ids = new List<string>();
        for (var i = 0; i < 11; i++) ids.Add("e" + i);

        var ok = BatchAdminHandlers.ValidateBatchShape(ids, out var err);
        Assert.False(ok);
        Assert.Equal("AWS.SimpleQueueService.TooManyEntriesInBatchRequest", err!.Value.Code);
    }

    [Fact]
    public void Duplicate_ids_are_rejected()
    {
        var ok = BatchAdminHandlers.ValidateBatchShape(new[] { "a", "b", "a" }, out var err);
        Assert.False(ok);
        Assert.Equal("AWS.SimpleQueueService.BatchEntryIdsNotDistinct", err!.Value.Code);
    }

    [Theory]
    [InlineData("")]                       // empty
    [InlineData("has space")]              // disallowed char
    [InlineData("has.dot")]                // disallowed char
    [InlineData("a/b")]                    // disallowed char
    public void Invalid_id_chars_are_rejected(string id)
    {
        var ok = BatchAdminHandlers.ValidateBatchShape(new[] { id }, out var err);
        Assert.False(ok);
        Assert.Equal("AWS.SimpleQueueService.InvalidBatchEntryId", err!.Value.Code);
    }

    [Fact]
    public void Id_at_80_chars_is_accepted()
    {
        var id = new string('a', 80);
        var ok = BatchAdminHandlers.ValidateBatchShape(new[] { id }, out var err);
        Assert.True(ok);
        Assert.Null(err);
    }

    [Fact]
    public void Id_over_80_chars_is_rejected()
    {
        var id = new string('a', 81);
        var ok = BatchAdminHandlers.ValidateBatchShape(new[] { id }, out var err);
        Assert.False(ok);
        Assert.Equal("AWS.SimpleQueueService.InvalidBatchEntryId", err!.Value.Code);
    }

    [Fact]
    public void Ten_unique_valid_ids_are_accepted()
    {
        var ids = new List<string>();
        for (var i = 0; i < 10; i++) ids.Add("entry-" + i);

        var ok = BatchAdminHandlers.ValidateBatchShape(ids, out var err);
        Assert.True(ok);
        Assert.Null(err);
    }

    [Fact]
    public void Purge_cooldown_rejects_overlap_and_reuses_expired_state()
    {
        var tracker = new BatchAdminHandlers.PurgeCooldownTracker(
            TimeSpan.FromSeconds(60),
            maxEntries: 2);
        var now = new DateTimeOffset(2026, 7, 22, 0, 0, 0, TimeSpan.Zero);

        Assert.Equal(BatchAdminHandlers.PurgeStartResult.Started, tracker.TryStart("ns|q", now));
        Assert.Equal(BatchAdminHandlers.PurgeStartResult.InProgress, tracker.TryStart("ns|q", now.AddSeconds(59)));
        Assert.Equal(1, tracker.Count);

        Assert.Equal(BatchAdminHandlers.PurgeStartResult.Started, tracker.TryStart("ns|q", now.AddSeconds(60)));
        Assert.Equal(1, tracker.Count);
    }

    [Fact]
    public void Purge_cooldown_releases_failed_attempt_and_bounds_cardinality()
    {
        var tracker = new BatchAdminHandlers.PurgeCooldownTracker(
            TimeSpan.FromSeconds(60),
            maxEntries: 2);
        var now = new DateTimeOffset(2026, 7, 22, 0, 0, 0, TimeSpan.Zero);

        Assert.Equal(BatchAdminHandlers.PurgeStartResult.Started, tracker.TryStart("ns|a", now));
        Assert.Equal(BatchAdminHandlers.PurgeStartResult.Started, tracker.TryStart("ns|b", now));
        Assert.Equal(BatchAdminHandlers.PurgeStartResult.CapacityExceeded, tracker.TryStart("ns|c", now));

        tracker.Release("ns|a", now);
        Assert.Equal(BatchAdminHandlers.PurgeStartResult.Started, tracker.TryStart("ns|c", now));
        Assert.Equal(2, tracker.Count);
    }
}
