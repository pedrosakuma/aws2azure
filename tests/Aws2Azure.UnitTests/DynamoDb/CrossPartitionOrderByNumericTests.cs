using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Aws2Azure.Modules.DynamoDb.Expressions;
using Aws2Azure.Modules.DynamoDb.Operations;
using Xunit;

namespace Aws2Azure.UnitTests.DynamoDb;

using static CrossPartitionOrderByQuery;

/// <summary>
/// Coverage for the #482 numeric-order path of the cross-partition ORDER BY
/// executor: a high-precision Number GSI sort key is stored as a
/// <c>{"_a2a:N":…}</c> envelope object that Cosmos orders structurally, so both
/// the per-range <c>ORDER BY</c> and the client-side merge switch to the stored
/// order-preserving <c>_a2a$ord$&lt;attr&gt;</c> encoding. These tests drive the
/// merge with a backend pre-sorted by that encoding (what Cosmos does
/// server-side) and assert numeric correctness where the plain IEEE-754 double
/// comparator would tie or mis-order.
/// </summary>
public sealed class CrossPartitionOrderByNumericTests
{
    private const string Sk = "sk";

    // Two distinct values that collapse to the SAME IEEE-754 double (both round
    // to 1.0, differing only at ~1e-18): the plain comparator ties them, the
    // order-key comparator does not. Both exceed 15 sig digits so the storage
    // layer keeps them in the {"_a2a:N":…} envelope.
    private const string HiPrecLo = "1.000000000000000001";
    private const string HiPrecHi = "1.000000000000000002";

    // ---- comparator ------------------------------------------------------

    [Fact]
    public void Numeric_order_key_distinguishes_values_that_double_collapses()
    {
        var loDouble = SortValue.FromAttribute(N(HiPrecLo));
        var hiDouble = SortValue.FromAttribute(N(HiPrecHi));
        Assert.Equal(0, SortValue.Compare(loDouble, hiDouble)); // double path ties

        var loEnc = SortValue.FromAttribute(N(HiPrecLo), numericOrderKey: true);
        var hiEnc = SortValue.FromAttribute(N(HiPrecHi), numericOrderKey: true);
        Assert.True(SortValue.Compare(loEnc, hiEnc) < 0); // order-key path distinguishes
    }

    [Fact]
    public void Numeric_order_key_orders_large_magnitude_and_negatives()
    {
        // Values beyond double precision / magnitude that live in the envelope.
        SortValue Enc(string raw) => SortValue.FromAttribute(N(raw), numericOrderKey: true);
        Assert.True(SortValue.Compare(Enc("-100000000000000000000"), Enc("-99999999999999999999")) < 0);
        Assert.True(SortValue.Compare(Enc("-1"), Enc("1")) < 0);
        Assert.True(SortValue.Compare(Enc("99999999999999999999"), Enc("100000000000000000000")) < 0);
        Assert.True(SortValue.Compare(
            Enc("123456789012345678901234567890.0001"),
            Enc("123456789012345678901234567890.0002")) < 0);
    }

    [Fact]
    public void Numeric_boundary_token_round_trips_the_raw_number()
    {
        // ToAttributeClone must emit the EXACT raw Number, not the lossy double,
        // so the resumed page re-encodes the identical boundary.
        var enc = SortValue.FromAttribute(N(HiPrecLo), numericOrderKey: true);
        var clone = enc.ToAttributeClone();
        Assert.Equal(HiPrecLo, clone.GetProperty("N").GetString());
    }

    // ---- resume filter SQL ----------------------------------------------

    [Fact]
    public void BuildResumeFilter_numeric_uses_encoded_bound_without_type_guards()
    {
        var p = new List<CosmosSqlParameter>();
        var sql = BuildResumeFilter("c[\"_a2a$ord$sk\"]", forward: true, N("5"), "@rv0", p, numericOrderKey: true);

        Assert.Equal("(c[\"_a2a$ord$sk\"] >= @rv0)", sql);
        Assert.Single(p);
        // The bound is the order-preserving encoding of 5 (a digits-only string),
        // not the number 5 itself.
        Assert.Equal(JsonValueKind.String, p[0].Value.ValueKind);
        var enc = p[0].Value.GetString()!;
        Assert.NotEqual("5", enc);
        Assert.Matches("^[0-9]+$", enc);
    }

    [Fact]
    public void BuildResumeFilter_numeric_descending_uses_lte()
    {
        var p = new List<CosmosSqlParameter>();
        var sql = BuildResumeFilter("c[\"_a2a$ord$sk\"]", forward: false, N("5"), "@rv0", p, numericOrderKey: true);
        Assert.Equal("(c[\"_a2a$ord$sk\"] <= @rv0)", sql);
    }

    // ---- merge + pagination ---------------------------------------------

    [Fact]
    public async Task Merge_orders_high_precision_values_across_partitions()
    {
        var backend = NumericBackend(pageSize: 10, ascending: true, new()
        {
            ["A"] = new[] { "1", HiPrecHi, "3" },
            ["B"] = new[] { HiPrecLo, "2", "1000000000000000000000" },
        });

        var result = await MergeAsync(
            backend.Ranges, Sk, numericOrderKey: true, forward: true, scanCap: 100, token: null,
            backend.Fetch, residualFilter: null, projection: null, CancellationToken.None);

        Assert.Equal(
            new[] { "1", HiPrecLo, HiPrecHi, "2", "3", "1000000000000000000000" },
            RawValues(result.Items));
        Assert.Null(result.Next);
    }

    [Fact]
    public async Task Merge_paginates_high_precision_values_correctly()
    {
        var data = new Dictionary<string, string[]>
        {
            ["A"] = new[] { "1", HiPrecHi, "3", "5" },
            ["B"] = new[] { HiPrecLo, "2", "4", "6" },
        };
        var expected = new[] { "1", HiPrecLo, HiPrecHi, "2", "3", "4", "5", "6" };

        var collected = await PaginateAll(data, scanCapPerPage: 3, forward: true);
        Assert.Equal(expected, collected);
    }

    // ---- SQL builder ----------------------------------------------------

    [Fact]
    public void BuildGsiSql_numeric_orders_by_encoded_field_with_membership_guard()
    {
        var empty = new FilterPushdownResult(null, Array.Empty<CosmosSqlParameter>(), null);
        var (sql, _) = QueryHandler.BuildGsiSql(
            "ghk", Sk, forward: true, empty, empty, empty,
            emitOrderBy: true, orderByPathOverride: "c[\"_a2a$ord$sk\"]");

        Assert.Contains("ORDER BY c[\"_a2a$ord$sk\"] ASC", sql);
        Assert.Contains("IS_DEFINED(c[\"_a2a$ord$sk\"])", sql);
    }

    [Fact]
    public void Numeric_sort_key_comparison_pushes_against_encoded_field()
    {
        // seq >= <high-precision N>: must push exactly against the encoded field
        // (no residual), so a range condition + Limit does not over-scan
        // out-of-range envelope rows.
        const string encodedPath = "c[\"_a2a$ord$sk\"]";
        var node = Compare(CompareOp.GreaterEqual, Sk, HiPrecHi);

        var push = QueryHandler.BuildNumericSortKeyPushdown(node, encodedPath, "sk");

        Assert.NotNull(push);
        Assert.Null(push!.Residual);
        Assert.Equal("(" + encodedPath + " >= @sk0)", push.Sql);
        var p = Assert.Single(push.Parameters);
        Assert.Equal("@sk0", p.Name);
        Assert.True(KeyScalarCodec.TryEncodeNumberOrderKey(HiPrecHi, out var expected, out _));
        Assert.Equal(expected, p.Value.GetString());
    }

    [Fact]
    public void Numeric_sort_key_between_pushes_encoded_bounds()
    {
        const string encodedPath = "c[\"_a2a$ord$sk\"]";
        var node = new BetweenCondition(
            new ConditionPathOperand(Path(Sk)),
            new ConditionValueOperand(new ValueRefOperand(":lo", N(HiPrecLo))),
            new ConditionValueOperand(new ValueRefOperand(":hi", N(HiPrecHi))));

        var push = QueryHandler.BuildNumericSortKeyPushdown(node, encodedPath, "sk");

        Assert.NotNull(push);
        Assert.Null(push!.Residual);
        Assert.Equal(
            "(" + encodedPath + " >= @sk0 AND " + encodedPath + " <= @sk1)", push.Sql);
        Assert.Equal(2, push.Parameters.Count);
        Assert.True(KeyScalarCodec.TryEncodeNumberOrderKey(HiPrecLo, out var encLo, out _));
        Assert.True(KeyScalarCodec.TryEncodeNumberOrderKey(HiPrecHi, out var encHi, out _));
        Assert.Equal(encLo, push.Parameters[0].Value.GetString());
        Assert.Equal(encHi, push.Parameters[1].Value.GetString());
    }

    private static CompareCondition Compare(CompareOp op, string attr, string rawN) =>
        new(op,
            new ConditionPathOperand(Path(attr)),
            new ConditionValueOperand(new ValueRefOperand(":v", N(rawN))));

    private static DocumentPath Path(string attr) =>
        new(new[] { new AttributePathSegment(attr) });

    // ---- helpers --------------------------------------------------------

    private async Task<List<string>> PaginateAll(
        Dictionary<string, string[]> data, int scanCapPerPage, bool forward)
    {
        var collected = new List<string>();
        OrderByToken? token = null;
        for (int guard = 0; guard < 1000; guard++)
        {
            // Simulate the server-side resume SQL: filter each range to rows
            // at/after (ASC) the boundary, ordered by the encoded key.
            var filtered = ApplyNumericResumeFilter(data, token, forward);
            var backend = NumericBackend(pageSize: 4, ascending: forward, filtered);

            var result = await MergeAsync(
                backend.Ranges, Sk, numericOrderKey: true, forward, scanCapPerPage, token,
                backend.Fetch, residualFilter: null, projection: null, CancellationToken.None);

            collected.AddRange(RawValues(result.Items));
            if (result.Next is null) return collected;
            token = result.Next;
        }
        throw new Xunit.Sdk.XunitException("pagination did not terminate");
    }

    private static Dictionary<string, string[]> ApplyNumericResumeFilter(
        Dictionary<string, string[]> data, OrderByToken? token, bool forward)
    {
        if (token is null) return data;
        var boundary = SortValue.FromAttribute(token.BoundaryValue, numericOrderKey: true);
        var result = new Dictionary<string, string[]>();
        foreach (var (id, list) in data)
        {
            result[id] = list.Where(raw =>
            {
                int c = SortValue.Compare(
                    SortValue.FromAttribute(N(raw), numericOrderKey: true), boundary);
                return forward ? c >= 0 : c <= 0;
            }).ToArray();
        }
        return result;
    }

    private static NumericFakeBackend NumericBackend(
        int pageSize, bool ascending, Dictionary<string, string[]> data)
        => new(pageSize, ascending, data);

    private static string[] RawValues(IEnumerable<Dictionary<string, JsonElement>> items)
        => items.Select(i => i[Sk].GetProperty("N").GetString()!).ToArray();

    private static Dictionary<string, JsonElement> NumItem(string raw, string id)
        => new(StringComparer.Ordinal)
        {
            [Sk] = N(raw),
            ["id"] = Av($"{{\"S\":\"{id}\"}}"),
        };

    private static JsonElement N(string raw) => Av($"{{\"N\":\"{raw}\"}}");

    private static JsonElement Av(string json)
    {
        using var d = JsonDocument.Parse(json);
        return d.RootElement.Clone();
    }

    /// <summary>Multi-range backend where each range's rows are pre-sorted by the
    /// order-preserving encoding (what a Cosmos <c>ORDER BY c._a2a$ord$sk</c>
    /// yields), then paginated into fixed-size pages.</summary>
    private sealed class NumericFakeBackend
    {
        private readonly Dictionary<string, List<Dictionary<string, JsonElement>>> _data;
        private readonly int _pageSize;

        public NumericFakeBackend(int pageSize, bool ascending, Dictionary<string, string[]> data)
        {
            _pageSize = pageSize;
            _data = new Dictionary<string, List<Dictionary<string, JsonElement>>>();
            foreach (var (id, raws) in data)
            {
                int idx = 0;
                var items = raws.Select(r => NumItem(r, id + (idx++))).ToList();
                items.Sort((a, b) =>
                {
                    int c = SortValue.Compare(
                        SortValue.FromItem(a, Sk, numericOrderKey: true),
                        SortValue.FromItem(b, Sk, numericOrderKey: true));
                    return ascending ? c : -c;
                });
                _data[id] = items;
            }

            Ranges = _data.Keys.OrderBy(k => k, StringComparer.Ordinal)
                .Select((k, i) => new PkRange(k, i.ToString("D4"), (i + 1).ToString("D4")))
                .ToList();
        }

        public IReadOnlyList<PkRange> Ranges { get; }

        public Task<PartitionPage> Fetch(string rangeId, string? continuation, CancellationToken ct)
        {
            var all = _data[rangeId];
            int start = continuation is null ? 0 : int.Parse(continuation);
            var slice = all.Skip(start).Take(_pageSize).ToList();
            int next = start + slice.Count;
            string? nextCont = next < all.Count ? next.ToString() : null;
            return Task.FromResult(new PartitionPage(slice, nextCont));
        }
    }
}
