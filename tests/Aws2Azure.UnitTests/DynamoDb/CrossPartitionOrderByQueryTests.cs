using System;
using System.Collections.Generic;
using System.Globalization;
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
/// Coverage for the client-side cross-partition ORDER BY executor (#481): the
/// k-way merge core, comparator, resume-filter SQL, and continuation-token
/// codec. The merge is driven by a scripted <see cref="PartitionPageFetcher"/>
/// (no live Cosmos), and pagination is exercised end-to-end by re-running the
/// merge with a backend filtered to the boundary on each page — exactly what
/// the real per-range resume SQL does server-side.
/// </summary>
public sealed class CrossPartitionOrderByQueryTests
{
    private const string Sk = "sk";

    // ---- comparator -----------------------------------------------------

    [Fact]
    public void SortValue_orders_across_cosmos_types()
    {
        var undef = SortValue.Missing;
        var nul = SortValue.FromAttribute(Av("{\"NULL\":true}"));
        var bln = SortValue.FromAttribute(Av("{\"BOOL\":false}"));
        var num = SortValue.FromAttribute(Av("{\"N\":\"3\"}"));
        var str = SortValue.FromAttribute(Av("{\"S\":\"a\"}"));

        Assert.True(SortValue.Compare(undef, nul) < 0);
        Assert.True(SortValue.Compare(nul, bln) < 0);
        Assert.True(SortValue.Compare(bln, num) < 0);
        Assert.True(SortValue.Compare(num, str) < 0);
    }

    [Fact]
    public void SortValue_orders_numbers_by_ieee754_and_strings_ordinal()
    {
        Assert.True(SortValue.Compare(Num(2), Num(10)) < 0);
        Assert.True(SortValue.Compare(Num(-5), Num(0)) < 0);
        Assert.Equal(0, SortValue.Compare(Num(7), Num(7)));
        Assert.True(SortValue.Compare(Str("Z"), Str("a")) < 0); // ordinal: 'Z'(90) < 'a'(97)
        Assert.True(SortValue.Compare(Str("apple"), Str("banana")) < 0);
    }

    // ---- resume filter SQL ----------------------------------------------

    [Fact]
    public void BuildResumeFilter_ascending_number_appends_higher_type_guards()
    {
        var p = new List<CosmosSqlParameter>();
        var sql = BuildResumeFilter("c[\"sk\"]", forward: true, Av("{\"N\":\"5\"}"), "@rv0", p);

        // Number type index = 3 → guards for String/Array/Object (4,5,6).
        Assert.Equal(
            "(c[\"sk\"] >= @rv0 OR IS_STRING(c[\"sk\"]) OR IS_ARRAY(c[\"sk\"]) OR IS_OBJECT(c[\"sk\"]))",
            sql);
        Assert.Single(p);
        Assert.Equal("@rv0", p[0].Name);
    }

    [Fact]
    public void BuildResumeFilter_descending_string_appends_lower_type_guards()
    {
        var p = new List<CosmosSqlParameter>();
        var sql = BuildResumeFilter("c[\"sk\"]", forward: false, Av("{\"S\":\"m\"}"), "@rv0", p);

        // String type index = 4 → DESC guards for types before it (3..0).
        Assert.Equal(
            "(c[\"sk\"] <= @rv0 OR IS_NUMBER(c[\"sk\"]) OR IS_BOOLEAN(c[\"sk\"]) OR IS_NULL(c[\"sk\"]) OR NOT IS_DEFINED(c[\"sk\"]))",
            sql);
    }

    // ---- continuation-token codec ---------------------------------------

    [Theory]
    [InlineData("{\"N\":\"42\"}", true, 3)]
    [InlineData("{\"S\":\"hello\"}", false, 0)]
    public void Token_round_trips_through_continuation_key(string avJson, bool forward, int skip)
    {
        var token = new OrderByToken(Av(avJson), forward, skip);
        var key = BuildTokenKey(token);

        // The encoded key rides the same opaque sentinel QueryHandler uses,
        // so DecodeToken must recover it from an ExclusiveStartKey element.
        var esk = WrapAsElement(key);
        var decoded = DecodeToken(esk);

        Assert.NotNull(decoded);
        Assert.Equal(forward, decoded!.Forward);
        Assert.Equal(skip, decoded.Skip);
        Assert.Equal(
            SortValue.FromAttribute(Av(avJson)).CosmosTypeIndex,
            SortValue.FromAttribute(decoded.BoundaryValue).CosmosTypeIndex);
        Assert.Equal(0, SortValue.Compare(
            SortValue.FromAttribute(Av(avJson)), SortValue.FromAttribute(decoded.BoundaryValue)));
    }

    [Fact]
    public void DecodeToken_returns_null_when_no_start_key()
        => Assert.Null(DecodeToken(null));

    [Fact]
    public void DecodeToken_rejects_foreign_token()
    {
        var esk = WrapBase64("{\"x\":\"someother\"}");
        Assert.Throws<FormatException>(() => DecodeToken(esk));
    }

    // ---- merge: single page ---------------------------------------------

    [Fact]
    public async Task Merge_orders_rows_across_partitions()
    {
        // Three ranges, each locally sorted ASC; globally interleaved.
        var backend = new FakeBackend(pageSize: 2, ascending: true, new()
        {
            ["A"] = Nums(1, 4, 9),
            ["B"] = Nums(2, 5, 7),
            ["C"] = Nums(3, 6, 8),
        });

        var result = await MergeAsync(
            backend.Ranges, Sk, forward: true, scanCap: 100, token: null,
            backend.Fetch, residualFilter: null, projection: null, CancellationToken.None);

        Assert.Equal(new[] { 1d, 2, 3, 4, 5, 6, 7, 8, 9 }, Values(result.Items));
        Assert.Equal(9, result.Scanned);
        Assert.Equal(9, result.Matched);
        Assert.Null(result.Next);
    }

    [Fact]
    public async Task Merge_orders_descending()
    {
        var backend = new FakeBackend(pageSize: 2, ascending: false, new()
        {
            ["A"] = Nums(9, 4, 1),
            ["B"] = Nums(7, 5, 2),
        });

        var result = await MergeAsync(
            backend.Ranges, Sk, forward: false, scanCap: 100, token: null,
            backend.Fetch, residualFilter: null, projection: null, CancellationToken.None);

        Assert.Equal(new[] { 9d, 7, 5, 4, 2, 1 }, Values(result.Items));
        Assert.Null(result.Next);
    }

    [Fact]
    public async Task Merge_stops_at_scanCap_and_emits_boundary_token()
    {
        var backend = new FakeBackend(pageSize: 10, ascending: true, new()
        {
            ["A"] = Nums(1, 3, 5, 7),
            ["B"] = Nums(2, 4, 6, 8),
        });

        var result = await MergeAsync(
            backend.Ranges, Sk, forward: true, scanCap: 3, token: null,
            backend.Fetch, residualFilter: null, projection: null, CancellationToken.None);

        Assert.Equal(new[] { 1d, 2, 3 }, Values(result.Items));
        Assert.Equal(3, result.Scanned);
        Assert.NotNull(result.Next);
        Assert.Equal(0, SortValue.Compare(SortValue.FromAttribute(result.Next!.BoundaryValue), Num(3)));
        Assert.Equal(1, result.Next.Skip); // one row == boundary value already returned
    }

    [Fact]
    public async Task Merge_skips_already_returned_boundary_rows_on_resume()
    {
        // Duplicate sort value 5 spread across two ranges; first page ends mid-block.
        var data = new Dictionary<string, List<Dictionary<string, JsonElement>>>
        {
            ["A"] = Nums(5, 5, 8),
            ["B"] = Nums(1, 5, 9),
        };

        var all = await DrainAllPages(data, forward: true, scanCapPerPage: 3);

        // Every row appears exactly once, globally ordered.
        Assert.Equal(new[] { 1d, 5, 5, 5, 8, 9 }, all);
    }

    [Fact]
    public async Task Merge_full_pagination_reconstructs_entire_set_exactly_once()
    {
        var rng = new Random(1234);
        var data = new Dictionary<string, List<Dictionary<string, JsonElement>>>();
        var expected = new List<double>();
        foreach (var id in new[] { "A", "B", "C", "D" })
        {
            var vals = Enumerable.Range(0, 25).Select(_ => (double)rng.Next(0, 20)).ToList();
            vals.Sort();
            expected.AddRange(vals);
            data[id] = vals.Select(v => NItem(v)).ToList();
        }
        expected.Sort();

        var all = await DrainAllPages(data, forward: true, scanCapPerPage: 7);

        Assert.Equal(expected, all);
    }

    [Fact]
    public async Task Merge_applies_residual_filter_and_projection()
    {
        var backend = new FakeBackend(pageSize: 10, ascending: true, new()
        {
            ["A"] = Nums(1, 2, 3, 4),
        });

        // residual filter: sk > 2  (parsed condition over the materialized map)
        var residual = ParseFilter("sk > :v", new Dictionary<string, JsonElement>
        {
            [":v"] = Av("{\"N\":\"2\"}"),
        });

        var result = await MergeAsync(
            backend.Ranges, Sk, forward: true, scanCap: 100, token: null,
            backend.Fetch, residual, projection: Projection.FromTopLevelNames(new[] { Sk }), CancellationToken.None);

        Assert.Equal(new[] { 3d, 4 }, Values(result.Items));
        Assert.Equal(4, result.Scanned); // pre-filter
        Assert.Equal(2, result.Matched); // post-filter
        Assert.All(result.Items, i => Assert.DoesNotContain("id", i.Keys)); // projected to sk only
    }

    [Fact]
    public async Task Merge_primes_empty_first_page_with_continuation()
    {
        // Range B's first page is empty but carries a continuation: the cursor
        // must drain forward before joining the heap (no row dropped).
        var backend = new EmptyFirstPageBackend();
        var result = await MergeAsync(
            backend.Ranges, Sk, forward: true, scanCap: 100, token: null,
            backend.Fetch, residualFilter: null, projection: null, CancellationToken.None);

        Assert.Equal(new[] { 1d, 2, 3, 4 }, Values(result.Items));
    }

    // ---- helpers --------------------------------------------------------

    private static async Task<List<double>> DrainAllPages(
        Dictionary<string, List<Dictionary<string, JsonElement>>> data, bool forward, int scanCapPerPage)
    {
        var collected = new List<double>();
        OrderByToken? token = null;
        for (int guard = 0; guard < 10_000; guard++)
        {
            // Each page simulates the server-side resume SQL: filter each range
            // to rows at/after (ASC) the boundary value.
            var filtered = ApplyResumeFilter(data, token, forward);
            var backend = new FakeBackend(pageSize: 4, ascending: forward, filtered);

            var result = await MergeAsync(
                backend.Ranges, Sk, forward, scanCapPerPage, token,
                backend.Fetch, residualFilter: null, projection: null, CancellationToken.None);

            collected.AddRange(Values(result.Items));
            if (result.Next is null) return collected;
            token = result.Next;
        }
        throw new Xunit.Sdk.XunitException("pagination did not terminate");
    }

    private static Dictionary<string, List<Dictionary<string, JsonElement>>> ApplyResumeFilter(
        Dictionary<string, List<Dictionary<string, JsonElement>>> data, OrderByToken? token, bool forward)
    {
        if (token is null) return data;
        var boundary = SortValue.FromAttribute(token.BoundaryValue);
        var result = new Dictionary<string, List<Dictionary<string, JsonElement>>>();
        foreach (var (id, list) in data)
        {
            result[id] = list.Where(it =>
            {
                int c = SortValue.Compare(SortValue.FromItem(it, Sk), boundary);
                return forward ? c >= 0 : c <= 0;
            }).ToList();
        }
        return result;
    }

    private static double[] Values(IEnumerable<Dictionary<string, JsonElement>> items)
        => items.Select(i => double.Parse(
            i[Sk].GetProperty("N").GetString()!, CultureInfo.InvariantCulture)).ToArray();

    private static List<Dictionary<string, JsonElement>> Nums(params double[] values)
        => values.Select(v => NItem(v)).ToList();

    private static Dictionary<string, JsonElement> NItem(double v, string id = "x")
        => new(StringComparer.Ordinal)
        {
            [Sk] = Av($"{{\"N\":\"{v.ToString(CultureInfo.InvariantCulture)}\"}}"),
            ["id"] = Av($"{{\"S\":\"{id}\"}}"),
        };

    private static SortValue Num(double v) => SortValue.FromAttribute(Av($"{{\"N\":\"{v}\"}}"));
    private static SortValue Str(string s) => SortValue.FromAttribute(Av($"{{\"S\":\"{s}\"}}"));

    private static JsonElement Av(string json)
    {
        using var d = JsonDocument.Parse(json);
        return d.RootElement.Clone();
    }

    private static JsonElement WrapAsElement(Dictionary<string, JsonElement> map)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>(128);
        using (var w = new System.Text.Json.Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            foreach (var (k, v) in map)
            {
                w.WritePropertyName(k);
                v.WriteTo(w);
            }
            w.WriteEndObject();
        }
        using var doc = JsonDocument.Parse(buffer.WrittenMemory);
        return doc.RootElement.Clone();
    }

    private static JsonElement WrapBase64(string innerJson)
    {
        var b64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(innerJson));
        return Av($"{{\"__a2a_continuation\":{{\"S\":\"{b64}\"}}}}");
    }

    private static ConditionNode ParseFilter(string expr, Dictionary<string, JsonElement> values)
        => ConditionExpressionParser.Parse(expr, null, values);

    /// <summary>Scripted multi-range backend that paginates each range's
    /// pre-sorted list into fixed-size pages (continuation = next index).</summary>
    private sealed class FakeBackend
    {
        private readonly Dictionary<string, List<Dictionary<string, JsonElement>>> _data;
        private readonly int _pageSize;

        public FakeBackend(int pageSize, bool ascending,
            Dictionary<string, List<Dictionary<string, JsonElement>>> data)
        {
            _pageSize = pageSize;
            _data = data;
            Ranges = data.Keys.OrderBy(k => k, StringComparer.Ordinal)
                .Select((k, i) => new PkRange(k, i.ToString("D4"), (i + 1).ToString("D4")))
                .ToList();
        }

        public IReadOnlyList<PkRange> Ranges { get; }

        public Task<PartitionPage> Fetch(string rangeId, string? continuation, CancellationToken ct)
        {
            var all = _data[rangeId];
            int start = continuation is null ? 0 : int.Parse(continuation, CultureInfo.InvariantCulture);
            var slice = all.Skip(start).Take(_pageSize).ToList();
            int next = start + slice.Count;
            string? nextCont = next < all.Count ? next.ToString(CultureInfo.InvariantCulture) : null;
            return Task.FromResult(new PartitionPage(slice, nextCont));
        }
    }

    /// <summary>Backend whose range "B" returns an empty first page that still
    /// carries a continuation, to exercise cursor priming.</summary>
    private sealed class EmptyFirstPageBackend
    {
        public IReadOnlyList<PkRange> Ranges { get; } = new[]
        {
            new PkRange("A", "0000", "0001"),
            new PkRange("B", "0001", "0002"),
        };

        public Task<PartitionPage> Fetch(string rangeId, string? continuation, CancellationToken ct)
        {
            if (rangeId == "A")
            {
                var items = new List<Dictionary<string, JsonElement>> { NItem(1), NItem(3) };
                return Task.FromResult(new PartitionPage(items, null));
            }

            // Range B: empty first page, then the real rows on the continued page.
            if (continuation is null)
            {
                return Task.FromResult(new PartitionPage(
                    new List<Dictionary<string, JsonElement>>(), "1"));
            }
            var b = new List<Dictionary<string, JsonElement>> { NItem(2), NItem(4) };
            return Task.FromResult(new PartitionPage(b, null));
        }
    }
}
