using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Aws2Azure.Core.Buffers;
using Aws2Azure.Modules.DynamoDb.Expressions;
using Aws2Azure.Modules.DynamoDb.Internal;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.Modules.DynamoDb.Operations;

/// <summary>
/// Client-side cross-partition ORDER BY execution for a composite Global
/// Secondary Index <c>Query</c> (#481).
///
/// <para><b>Why this exists.</b> The proxy serves a GSI query as a
/// cross-partition Cosmos query (there is no single partition-key scope —
/// a GSI hash differs from the base table key). The Cosmos <i>gateway</i>
/// happily concatenates <i>unordered</i> cross-partition results, and serves
/// <i>ordered single-partition</i> results, but it refuses to serve an
/// <i>ordered cross-partition</i> query in one request ("The provided cross
/// partition query can not be directly served by the gateway") — it returns a
/// query plan and expects the client to fan out across physical partition key
/// ranges and merge-sort. A composite GSI query (<c>ORDER BY c.&lt;gsiSort&gt;</c>)
/// is exactly that case. The emulator runs a single partition and so masked the
/// gap (#480 real-Azure validation surfaced it).</para>
///
/// <para><b>Strategy (single ORDER BY column).</b> Because a composite GSI has
/// exactly one sort key, the full SDK <c>OrderByContinuationToken</c> machinery
/// (per-partition backend tokens + <c>_rid</c> tie-break + three-way
/// Before/Target/After resume filters) collapses to a much simpler,
/// provably-correct scheme:</para>
/// <list type="number">
///   <item>List the physical partition key ranges
///   (<c>GET /dbs/{db}/colls/{coll}/pkranges</c>).</item>
///   <item>Run the ordered query against each range individually
///   (<c>x-ms-documentdb-partitionkeyrangeid</c>) — single-partition ORDER BY
///   <i>is</i> served by the gateway. Each range streams its rows already
///   sorted.</item>
///   <item>k-way merge across ranges by the sort value (Cosmos comparison
///   semantics), tie-broken by the range's <c>minInclusive</c> so the emit
///   order is deterministic and stable across calls.</item>
/// </list>
///
/// <para><b>Continuation.</b> A min-heap merge emits every row with sort value
/// strictly less than the boundary value <c>V</c> before <c>V</c> itself, and
/// the <c>V</c>-block order is stable across calls. So a page boundary is fully
/// described by <c>(V, direction, skip)</c> where <c>skip</c> is how many rows
/// with value <c>== V</c> were already returned. Resuming re-queries every range
/// with <c>c.&lt;gsiSort&gt; &gt;= V</c> (ASC) and drops the first <c>skip</c>
/// rows whose value equals <c>V</c>. No per-partition backend token and no
/// <c>_rid</c> ordering is required. The token rides the existing opaque
/// <c>__a2a_continuation</c> sentinel that <see cref="QueryHandler"/> already
/// uses for <c>LastEvaluatedKey</c>.</para>
///
/// <para><b>Correctness precondition.</b> The <c>skip</c> resume is exact iff the
/// global emit order of a <c>V</c>-block is <i>deterministic and stable across
/// the two requests</i>. It is, because: (a) across ranges the merge tie-break is
/// the range's <c>minInclusive</c> ordinal (constant), and (b) within a range the
/// per-range <c>ORDER BY c.&lt;gsiSort&gt;</c> returns equal-key rows in Cosmos's
/// physical index (<c>_rid</c>) order, which is identical for two identical-shape
/// queries over unchanged data. This is the same physical-order stability the
/// official SDK's <c>_rid</c>-based resume depends on. The one residual
/// divergence is a <i>concurrent insert/delete inside the exact boundary-value
/// block between two page fetches</i>, which can shift the <c>skip</c> alignment
/// by the net change — a narrow eventual-consistency window (DynamoDB pagination
/// exhibits analogous behaviour under concurrent writes). Recorded in the gap
/// doc's <c>behavior_differences</c>.</para>
///
/// <para><b>Footprint.</b> Seeding the heap requires one in-flight page per
/// overlapping range, so peak memory is O(ranges × page × item size) — bounded
/// by <see cref="PerPartitionPageSize"/>. This is the documented cost of the
/// opt-in <c>EnableGlobalSecondaryIndexQueries</c> feature.</para>
///
/// <para><b>Known divergences.</b> The merge comparator orders the sort value
/// as Cosmos does (strings by ordinal). A Number sort key is compared by its
/// stored order-preserving encoding (<c>_a2a$ord$&lt;attr&gt;</c>, #482) so
/// high-precision values that the storage layer keeps in the
/// <c>{"_a2a:N":...}</c> envelope (i.e. values that do not survive a double
/// round-trip) still sort numerically — the per-range <c>ORDER BY</c> targets
/// that field and the client recomputes the same encoding from each row's
/// <c>{"N":…}</c> value. Items written before the field existed lack it and
/// are excluded from ordered results (an <c>IS_DEFINED</c> membership guard)
/// until rewritten. Binary (<c>B</c>) sort keys are always envelope-stored and
/// so are rejected upstream in <see cref="QueryHandler"/> before reaching this
/// executor.</para>
/// </summary>
internal static class CrossPartitionOrderByQuery
{
    /// <summary>
    /// Per-range page size for the partition-targeted fan-out. Kept modest so
    /// the seeded-heap peak (one page per overlapping range) stays within the
    /// sidecar budget. Pagination across DynamoDB pages restarts from the
    /// boundary filter, so this does not cap the total result set.
    /// </summary>
    internal const int PerPartitionPageSize = 100;

    private const string TokenDiscriminator =
        DynamoDbPersistedFormatContract.OrderedContinuationDiscriminator;

    /// <summary>One physical partition key range.</summary>
    internal readonly record struct PkRange(string Id, string MinInclusive, string MaxExclusive);

    /// <summary>One fan-out page: the materialized items plus the range's
    /// continuation for the next page (<see langword="null"/> when drained).</summary>
    internal readonly record struct PartitionPage(
        List<Dictionary<string, JsonElement>> Items, string? Continuation);

    /// <summary>Fetches the next page for a partition range. Abstracted so the
    /// merge core is unit-testable without a live Cosmos backend.</summary>
    internal delegate Task<PartitionPage> PartitionPageFetcher(
        string rangeId, string? continuation, CancellationToken ct);

    /// <summary>Resume boundary captured in (and recovered from) the
    /// continuation token.</summary>
    internal sealed record OrderByToken(JsonElement BoundaryValue, bool Forward, int Skip);

    /// <summary>Outcome of one merged page.</summary>
    internal sealed record MergeResult(
        List<Dictionary<string, JsonElement>> Items, int Scanned, int Matched, OrderByToken? Next);

    // ---- Entry point ----------------------------------------------------

    public static async Task ExecuteAsync(
        HttpContext ctx,
        QueryRequest req,
        string gsiHashName,
        string gsiSortName,
        bool numericOrderKey,
        bool forward,
        FilterPushdownResult hashPush,
        FilterPushdownResult skPush,
        FilterPushdownResult userPush,
        Expressions.ConditionNode? residualFilter,
        Projection? projection,
        CosmosClient cosmos,
        CancellationToken ct)
    {
        OrderByToken? token;
        try
        {
            token = DecodeToken(req.ExclusiveStartKey);
        }
        catch (FormatException ex)
        {
            await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException",
                "The provided starting key is invalid: " + ex.Message).ConfigureAwait(false);
            return;
        }

        // The boundary value's direction must agree with this request's
        // direction — a client must not flip ScanIndexForward mid-pagination.
        if (token is not null && token.Forward != forward)
        {
            await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException",
                "The provided starting key does not match the query ScanIndexForward.").ConfigureAwait(false);
            return;
        }

        // For a high-precision numeric sort key (#482) ordering targets the
        // stored order-preserving `_a2a$ord$<attr>` string field, not the raw
        // attribute (whose {"_a2a:N":…} envelope Cosmos orders as an object).
        // The client comparator recomputes the same encoding from each row's
        // {"N":…} value, so per-range SQL order and client merge order agree.
        string? orderPath = numericOrderKey
            ? Expressions.CosmosPathTranslator.Translate(new Expressions.DocumentPath(
                new[] { new Expressions.AttributePathSegment(
                    Persistence.InferredAttributeStorage.OrderKeyPropertyPrefix + gsiSortName) }))
            : null;

        // Build the per-range resume filter (only on a continued page).
        string? resumeSql = null;
        List<CosmosSqlParameter>? resumeParams = null;
        if (token is not null)
        {
            var sortPath = orderPath ?? Expressions.CosmosPathTranslator.Translate(
                new Expressions.DocumentPath(new[] { new Expressions.AttributePathSegment(gsiSortName) }));
            resumeParams = new List<CosmosSqlParameter>(1);
            resumeSql = BuildResumeFilter(sortPath, forward, token.BoundaryValue, "@rv0", resumeParams, numericOrderKey);
        }

        var (sql, sqlParams) = QueryHandler.BuildGsiSql(
            gsiHashName, gsiSortName, forward, hashPush, skPush, userPush,
            emitOrderBy: true, resumeFilterSql: resumeSql, resumeParams: resumeParams,
            orderByPathOverride: orderPath);

        var collLink = "dbs/" + cosmos.DatabaseName + "/colls/" + req.TableName;
        var collUri = "/" + collLink + "/docs";

        using var queryBody = CosmosQueryBody.Build(sql, sqlParams);
        var bodyMem = queryBody.WrittenMemory;

        IReadOnlyList<PkRange> ranges;
        try
        {
            ranges = await GetPartitionKeyRangesAsync(cosmos, collLink, ct).ConfigureAwait(false);
        }
        catch (CosmosFeedException ex)
        {
            if (ex.NotFound)
            {
                await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ResourceNotFoundException",
                    $"Table not found: {req.TableName}").ConfigureAwait(false);
                return;
            }
            using (ex.Response)
            {
                await CosmosOpsShared.WriteCosmosErrorAsync(ctx, ex.Response!, ct).ConfigureAwait(false);
            }
            return;
        }

        // The fetcher sends the (immutable) query body to a single physical
        // range. The body buffer outlives every fetch (disposed below).
        Task<PartitionPage> Fetch(string rangeId, string? continuation, CancellationToken token2)
            => FetchPartitionPageAsync(cosmos, collLink, collUri, bodyMem, rangeId, continuation, token2);

        int scanCap = req.Limit is int lim && lim > 0 ? lim : MaxScanCap;

        MergeResult result;
        try
        {
            result = await MergeAsync(
                ranges, gsiSortName, numericOrderKey, forward, scanCap, token, Fetch, residualFilter, projection, ct)
                .ConfigureAwait(false);
        }
        catch (CosmosFeedException ex)
        {
            using (ex.Response)
            {
                if (ex.NotFound)
                {
                    await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ResourceNotFoundException",
                        $"Table not found: {req.TableName}").ConfigureAwait(false);
                }
                else
                {
                    await CosmosOpsShared.WriteCosmosErrorAsync(ctx, ex.Response!, ct).ConfigureAwait(false);
                }
            }
            return;
        }
        catch (ConditionEvaluationException ex)
        {
            await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException", ex.Message).ConfigureAwait(false);
            return;
        }

        DynamoDbMetrics.RecordReadTransformPath(DynamoDbMetrics.OpQuery, DynamoDbMetrics.PathMaterialized);
        var response = new QueryResponse
        {
            Items = result.Items,
            Count = result.Matched,
            ScannedCount = result.Scanned,
            LastEvaluatedKey = result.Next is null ? null : BuildTokenKey(result.Next),
        };
        await CosmosOpsShared.WriteJsonAsync(ctx, 200, response, QueryJsonContext.Default.QueryResponse)
            .ConfigureAwait(false);
    }

    /// <summary>Unbounded-page scan ceiling (no <c>Limit</c>): one merged page
    /// returns at most this many evaluated rows, then yields a continuation —
    /// mirrors <see cref="QueryHandler"/>'s single-page batch bound.</summary>
    private const int MaxScanCap = 1000;

    // ---- Merge core (unit-testable) -------------------------------------

    /// <summary>
    /// k-way merges the per-range ordered streams into one DynamoDB page. Pulls
    /// at most <paramref name="scanCap"/> evaluated rows (DynamoDB's pre-filter
    /// <c>Limit</c> semantics), applies the in-process residual filter and
    /// projection, and returns the boundary token when more rows remain.
    /// </summary>
    internal static async Task<MergeResult> MergeAsync(
        IReadOnlyList<PkRange> ranges,
        string gsiSortName,
        bool numericOrderKey,
        bool forward,
        int scanCap,
        OrderByToken? token,
        PartitionPageFetcher fetch,
        Expressions.ConditionNode? residualFilter,
        Projection? projection,
        CancellationToken ct)
    {
        var cursors = new List<Cursor>(ranges.Count);
        foreach (var r in ranges)
        {
            var first = await fetch(r.Id, null, ct).ConfigureAwait(false);
            var cursor = new Cursor(r, gsiSortName, numericOrderKey, first);
            await cursor.PrimeAsync(fetch, ct).ConfigureAwait(false);
            if (!cursor.IsExhausted) cursors.Add(cursor);
        }

        SortValue boundary = token is null ? default : SortValue.FromAttribute(token.BoundaryValue, numericOrderKey);
        int toSkip = token?.Skip ?? 0;

        var items = new List<Dictionary<string, JsonElement>>();
        int scanned = 0;
        int matched = 0;

        // Running tail counter: rows scanned this page whose value equals the
        // current maximum scanned value. Because each cursor is monotonic and
        // the merge is ordered, this ends as the count of trailing rows that
        // share the final boundary value.
        SortValue runValue = default;
        bool runValueSet = false;
        int runCount = 0;

        while (cursors.Count > 0 && scanned < scanCap)
        {
            int min = 0;
            for (int i = 1; i < cursors.Count; i++)
            {
                if (Compare(cursors[i].HeadValue, cursors[min].HeadValue, cursors[i].Range.MinInclusive,
                        cursors[min].Range.MinInclusive, forward) < 0)
                {
                    min = i;
                }
            }

            var c = cursors[min];
            var head = c.Head;
            var headValue = c.HeadValue;

            // Skip rows already returned on a previous page: the resume filter
            // re-includes the boundary value's rows; drop the first `skip` of
            // them (in the same deterministic merge order).
            if (toSkip > 0 && token is not null)
            {
                if (SortValue.Compare(headValue, boundary) == 0)
                {
                    toSkip--;
                    await AdvanceAsync(c, cursors, min, fetch, ct).ConfigureAwait(false);
                    continue;
                }

                // The boundary block was shorter than expected (concurrent
                // delete) — stop skipping and emit from here.
                toSkip = 0;
            }

            scanned++;
            if (runValueSet && SortValue.Compare(headValue, runValue) == 0)
            {
                runCount++;
            }
            else
            {
                runValue = headValue;
                runValueSet = true;
                runCount = 1;
            }

            bool keep = residualFilter is null || ConditionEvaluator.Evaluate(residualFilter, head);
            if (keep)
            {
                matched++;
                items.Add(projection is null ? head : projection.Apply(head));
            }

            await AdvanceAsync(c, cursors, min, fetch, ct).ConfigureAwait(false);
        }

        OrderByToken? next = null;
        if (cursors.Count > 0 && runValueSet)
        {
            // More rows remain. The next page resumes from the final scanned
            // value, carrying the prior skip when the page did not advance past
            // the incoming boundary value.
            int carry = token is not null && SortValue.Compare(runValue, boundary) == 0 ? token.Skip : 0;
            next = new OrderByToken(runValue.ToAttributeClone(), forward, runCount + carry);
        }

        return new MergeResult(items, scanned, matched, next);
    }

    private static async Task AdvanceAsync(
        Cursor c, List<Cursor> cursors, int index, PartitionPageFetcher fetch, CancellationToken ct)
    {
        await c.AdvanceAsync(fetch, ct).ConfigureAwait(false);
        if (c.IsExhausted)
        {
            // Swap-remove (order irrelevant; tie-break uses range min, not list
            // position).
            cursors[index] = cursors[cursors.Count - 1];
            cursors.RemoveAt(cursors.Count - 1);
        }
    }

    private sealed class Cursor
    {
        private readonly string _sortName;
        private readonly bool _numericOrderKey;
        private List<Dictionary<string, JsonElement>> _page;
        private string? _continuation;
        private int _idx;

        public PkRange Range { get; }
        public bool IsExhausted { get; private set; }

        public Cursor(PkRange range, string sortName, bool numericOrderKey, PartitionPage first)
        {
            Range = range;
            _sortName = sortName;
            _numericOrderKey = numericOrderKey;
            _page = first.Items;
            _continuation = first.Continuation;
            _idx = 0;
        }

        public Dictionary<string, JsonElement> Head => _page[_idx];
        public SortValue HeadValue => SortValue.FromItem(_page[_idx], _sortName, _numericOrderKey);

        public async Task AdvanceAsync(PartitionPageFetcher fetch, CancellationToken ct)
        {
            _idx++;
            if (_idx < _page.Count) return;

            while (true)
            {
                if (_continuation is null)
                {
                    IsExhausted = true;
                    return;
                }

                var next = await fetch(Range.Id, _continuation, ct).ConfigureAwait(false);
                _page = next.Items;
                _continuation = next.Continuation;
                _idx = 0;
                if (_page.Count > 0) return;
                // Empty page with a continuation: keep draining.
            }
        }

        /// <summary>Drains leading empty pages so <see cref="Head"/> is valid (or
        /// marks the cursor exhausted). Cosmos may return an empty page that
        /// still carries a continuation.</summary>
        public async Task PrimeAsync(PartitionPageFetcher fetch, CancellationToken ct)
        {
            while (_page.Count == 0)
            {
                if (_continuation is null)
                {
                    IsExhausted = true;
                    return;
                }

                var next = await fetch(Range.Id, _continuation, ct).ConfigureAwait(false);
                _page = next.Items;
                _continuation = next.Continuation;
                _idx = 0;
            }
        }
    }

    // ---- Comparison ------------------------------------------------------

    /// <summary>Compares two heads for the min-heap: by sort value honouring
    /// direction, tie-broken by range <c>minInclusive</c> (ordinal, ascending,
    /// direction-independent) so the emit order is deterministic and stable
    /// across continuation calls.</summary>
    private static int Compare(SortValue a, SortValue b, string aMin, string bMin, bool forward)
    {
        int cmp = SortValue.Compare(a, b);
        if (!forward) cmp = -cmp;
        if (cmp != 0) return cmp;
        return string.CompareOrdinal(aMin, bMin);
    }

    /// <summary>
    /// A sort key value reduced to Cosmos ordering semantics. A composite GSI
    /// sort key is a single scalar (N/S/B) in practice; null/bool/missing are
    /// handled defensively, and any object/array storage shape lands in
    /// <see cref="Kind.Other"/>.
    /// </summary>
    internal readonly struct SortValue
    {
        // Cosmos cross-type order: Undefined < Null < Boolean < Number < String < (Array/Object).
        internal enum Kind { Undefined = 0, Null = 1, Boolean = 2, Number = 3, String = 4, Other = 5 }

        private readonly Kind _kind;
        private readonly double _num;
        private readonly string? _str;
        // For a #482 numeric order key: the raw DDB Number string, preserved so
        // the continuation token round-trips the exact value ({"N":raw}) while
        // comparison uses the order-preserving encoding held in _str.
        private readonly string? _rawNumber;

        private SortValue(Kind kind, double num, string? str, string? rawNumber = null)
        {
            _kind = kind;
            _num = num;
            _str = str;
            _rawNumber = rawNumber;
        }

        public static SortValue Missing => new(Kind.Undefined, 0, null);

        /// <summary>The Cosmos cross-type ordinal (0=Undefined … 6=Object) used
        /// to build the resume filter's type guards. <see cref="Kind.Other"/>
        /// maps to Object (6).</summary>
        public int CosmosTypeIndex => _kind == Kind.Other ? 6 : (int)_kind;

        /// <summary>Reads the sort attribute out of a materialized AttributeValue
        /// map (e.g. <c>{"N":"42"}</c> / <c>{"S":"foo"}</c>).</summary>
        public static SortValue FromItem(Dictionary<string, JsonElement> item, string sortName, bool numericOrderKey = false)
            => item.TryGetValue(sortName, out var av) ? FromAttribute(av, numericOrderKey) : Missing;

        /// <summary>Reads an AttributeValue element into a comparable value. When
        /// <paramref name="numericOrderKey"/> is set, a Number value is reduced to
        /// its order-preserving encoding (<see cref="KeyScalarCodec.TryEncodeNumberOrderKey"/>)
        /// and compared as a string, matching the per-range
        /// <c>ORDER BY c._a2a$ord$&lt;attr&gt;</c> (#482).</summary>
        public static SortValue FromAttribute(JsonElement av, bool numericOrderKey = false)
        {
            if (av.ValueKind != JsonValueKind.Object) return Missing;
            foreach (var prop in av.EnumerateObject())
            {
                switch (prop.Name)
                {
                    case "N":
                        var rawN = prop.Value.GetString();
                        if (numericOrderKey && rawN is not null
                            && KeyScalarCodec.TryEncodeNumberOrderKey(rawN, out var enc, out _))
                        {
                            return new SortValue(Kind.String, 0, enc, rawN);
                        }
                        return double.TryParse(rawN, NumberStyles.Float,
                            CultureInfo.InvariantCulture, out var n)
                            ? new SortValue(Kind.Number, n, null)
                            : new SortValue(Kind.Number, 0, null);
                    case "S":
                        return new SortValue(Kind.String, 0, prop.Value.GetString() ?? string.Empty);
                    case "B":
                        // Stored/compared as the base64 representation (string
                        // ordinal), matching how Cosmos orders the stored value.
                        return new SortValue(Kind.String, 0, prop.Value.GetString() ?? string.Empty);
                    case "NULL":
                        return new SortValue(Kind.Null, 0, null);
                    case "BOOL":
                        return new SortValue(Kind.Boolean,
                            prop.Value.ValueKind == JsonValueKind.True ? 1 : 0, null);
                    default:
                        return new SortValue(Kind.Other, 0, null);
                }
            }
            return Missing;
        }

        public static int Compare(SortValue a, SortValue b)
        {
            if (a._kind != b._kind) return ((int)a._kind).CompareTo((int)b._kind);
            return a._kind switch
            {
                Kind.Number => a._num.CompareTo(b._num),
                Kind.String => string.CompareOrdinal(a._str, b._str),
                Kind.Boolean => a._num.CompareTo(b._num),
                _ => 0,
            };
        }

        /// <summary>Re-materializes the value as an AttributeValue for the
        /// continuation token (only invoked for the boundary row, which always
        /// carries the sort attribute).</summary>
        public JsonElement ToAttributeClone()
        {
            var buffer = new System.Buffers.ArrayBufferWriter<byte>(32);
            using (var w = new Utf8JsonWriter(buffer))
            {
                w.WriteStartObject();
                if (_rawNumber is not null)
                {
                    // #482 numeric order key: preserve the exact raw Number so the
                    // resumed page re-encodes the identical boundary value.
                    w.WriteString("N", _rawNumber);
                }
                else
                {
                    switch (_kind)
                    {
                        case Kind.Number:
                            w.WriteString("N", _num.ToString("R", CultureInfo.InvariantCulture));
                            break;
                        case Kind.String:
                            w.WriteString("S", _str ?? string.Empty);
                            break;
                        case Kind.Boolean:
                            w.WriteBoolean("BOOL", _num != 0);
                            break;
                        case Kind.Null:
                            w.WriteBoolean("NULL", true);
                            break;
                        default:
                            w.WriteString("S", string.Empty);
                            break;
                    }
                }
                w.WriteEndObject();
            }
            using var doc = JsonDocument.Parse(buffer.WrittenMemory);
            return doc.RootElement.Clone();
        }
    }

    // ---- Resume filter ---------------------------------------------------

    /// <summary>
    /// Builds the boundary predicate substituted into each range's query on a
    /// continued page. For a single ASC column and boundary value <c>V</c> of
    /// Cosmos type index <c>i</c>:
    /// <c>(path &gt;= @rv OR IS_&lt;type&gt;(path) ... )</c> for every type that
    /// sorts after <c>i</c> (DESC mirrors with <c>&lt;=</c> and types before
    /// <c>i</c>). The type appendages keep the predicate correct if the column
    /// ever holds mixed JSON types (Cosmos relational operators yield no match
    /// across types). The bound is emitted as a typed SQL parameter.
    /// </summary>
    internal static string BuildResumeFilter(
        string sortPath, bool forward, JsonElement boundaryValue, string paramName,
        List<CosmosSqlParameter> parameters, bool numericOrderKey = false)
    {
        // #482 numeric order key: the boundary is the raw Number; compare the
        // stored order-preserving encoding against its encoded bound. The
        // encoded field is uniformly a string when defined (undefined rows are
        // excluded by the ORDER BY membership guard), so no cross-type guards
        // are needed.
        if (numericOrderKey)
        {
            string encoded = "1";
            if (boundaryValue.ValueKind == JsonValueKind.Object
                && boundaryValue.TryGetProperty("N", out var nEl)
                && nEl.GetString() is { } rawN
                && KeyScalarCodec.TryEncodeNumberOrderKey(rawN, out var enc, out _))
            {
                encoded = enc;
            }
            parameters.Add(new CosmosSqlParameter(paramName, CloneString(encoded)));
            return "(" + sortPath + (forward ? " >= " : " <= ") + paramName + ")";
        }

        var sv = SortValue.FromAttribute(boundaryValue);
        int typeIndex = sv.CosmosTypeIndex;

        var sb = new StringBuilder("(").Append(sortPath).Append(forward ? " >= " : " <= ").Append(paramName);
        AppendTypeGuards(sb, sortPath, typeIndex, forward);
        sb.Append(')');

        parameters.Add(new CosmosSqlParameter(paramName, BoundaryParamValue(boundaryValue)));
        return sb.ToString();
    }

    // Cosmos system-function names by type-order index (0..6).
    private static readonly string[] IsFunctionByIndex =
    {
        "NOT IS_DEFINED", "IS_NULL", "IS_BOOLEAN", "IS_NUMBER", "IS_STRING", "IS_ARRAY", "IS_OBJECT",
    };

    private static void AppendTypeGuards(StringBuilder sb, string path, int typeIndex, bool forward)
    {
        if (forward)
        {
            for (int i = typeIndex + 1; i < IsFunctionByIndex.Length; i++)
            {
                sb.Append(" OR ").Append(IsFunctionByIndex[i]).Append('(').Append(path).Append(')');
            }
        }
        else
        {
            for (int i = typeIndex - 1; i >= 0; i--)
            {
                sb.Append(" OR ").Append(IsFunctionByIndex[i]).Append('(').Append(path).Append(')');
            }
        }
    }

    /// <summary>Converts the boundary AttributeValue into a raw Cosmos value
    /// (number for N, string for S/B) for the SQL bound parameter.</summary>
    private static JsonElement BoundaryParamValue(JsonElement boundaryValue)
    {
        if (boundaryValue.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in boundaryValue.EnumerateObject())
            {
                switch (prop.Name)
                {
                    case "N":
                        var raw = prop.Value.GetString() ?? "0";
                        try
                        {
                            using var numDoc = JsonDocument.Parse(raw);
                            return numDoc.RootElement.Clone();
                        }
                        catch (JsonException)
                        {
                            return CloneString("0");
                        }
                    case "S":
                    case "B":
                        return CloneString(prop.Value.GetString() ?? string.Empty);
                    default:
                        return CloneString(string.Empty);
                }
            }
        }
        return CloneString(string.Empty);
    }

    private static JsonElement CloneString(string s)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>(s.Length + 2);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStringValue(s);
        }
        using var doc = JsonDocument.Parse(buffer.WrittenMemory);
        return doc.RootElement.Clone();
    }

    // ---- Continuation token codec ---------------------------------------

    internal static OrderByToken? DecodeToken(JsonElement? exclusiveStartKey)
    {
        var raw = QueryHandler.ExtractContinuation(exclusiveStartKey);
        if (string.IsNullOrEmpty(raw)) return null;

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(raw);
        }
        catch (JsonException ex)
        {
            throw new FormatException("continuation token payload is not valid JSON.", ex);
        }
        using (doc)
        {
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new FormatException("continuation token payload must be an object.");
        if (!root.TryGetProperty("x", out var disc)
            || disc.ValueKind != JsonValueKind.String
            || disc.GetString() != TokenDiscriminator)
            throw new FormatException("continuation token is not a GSI ordered-query token.");
        if (!root.TryGetProperty("v", out var v) || v.ValueKind != JsonValueKind.Object)
            throw new FormatException("continuation token is missing its boundary value.");
        var fwd = true;
        if (root.TryGetProperty("f", out var f))
        {
            if (f.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new FormatException("continuation token direction must be boolean.");
            fwd = f.GetBoolean();
        }
        var skip = 0;
        if (root.TryGetProperty("n", out var n))
        {
            if (!n.TryGetInt32(out skip) || skip < 0)
                throw new FormatException(
                    "continuation token duplicate count must be a nonnegative integer.");
        }
        return new OrderByToken(v.Clone(), fwd, skip);
        }
    }

    internal static Dictionary<string, JsonElement> BuildTokenKey(OrderByToken token)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>(64);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("x", TokenDiscriminator);
            w.WritePropertyName("v");
            token.BoundaryValue.WriteTo(w);
            w.WriteBoolean("f", token.Forward);
            w.WriteNumber("n", token.Skip);
            w.WriteEndObject();
        }
        var json = Encoding.UTF8.GetString(buffer.WrittenSpan);
        return QueryHandler.BuildContinuationKey(json);
    }

    // ---- Cosmos REST ----------------------------------------------------

    /// <summary>Lists every physical partition key range of the collection.
    /// A composite GSI query fans out across all of them (a GSI hash is not the
    /// container partition key, so the query spans the whole collection).</summary>
    internal static async Task<IReadOnlyList<PkRange>> GetPartitionKeyRangesAsync(
        CosmosClient cosmos, string collLink, CancellationToken ct)
    {
        var ranges = new List<PkRange>();
        var uri = "/" + collLink + "/pkranges";
        string? continuation = null;
        do
        {
            var headers = new List<KeyValuePair<string, string>>(2)
            {
                new("x-ms-max-item-count", "-1"),
            };
            if (!string.IsNullOrEmpty(continuation))
            {
                headers.Add(new KeyValuePair<string, string>("x-ms-continuation", continuation));
            }

            var resp = await cosmos.SendAsync(
                HttpMethod.Get, "pkranges", collLink, uri, content: null, headers, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                throw new CosmosFeedException(resp, resp.StatusCode == System.Net.HttpStatusCode.NotFound);
            }

            using (resp)
            {
                using var body = await CosmosOpsShared.ReadCosmosRawBodyAsync(resp.Content, ct).ConfigureAwait(false);
                ParsePartitionKeyRanges(body.WrittenMemory.Span, ranges);

                continuation = null;
                if (resp.Headers.TryGetValues("x-ms-continuation", out var ctValues))
                {
                    foreach (var v in ctValues) { continuation = v; break; }
                }
            }
        }
        while (!string.IsNullOrEmpty(continuation));

        return ranges;
    }

    private static void ParsePartitionKeyRanges(ReadOnlySpan<byte> json, List<PkRange> sink)
    {
        var reader = new Utf8JsonReader(json);
        using var doc = JsonDocument.ParseValue(ref reader);
        if (!doc.RootElement.TryGetProperty("PartitionKeyRanges", out var arr)
            || arr.ValueKind != JsonValueKind.Array)
        {
            return;
        }
        foreach (var el in arr.EnumerateArray())
        {
            string id = el.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? string.Empty : string.Empty;
            string min = el.TryGetProperty("minInclusive", out var minEl) ? minEl.GetString() ?? string.Empty : string.Empty;
            string max = el.TryGetProperty("maxExclusive", out var maxEl) ? maxEl.GetString() ?? string.Empty : string.Empty;
            if (id.Length != 0) sink.Add(new PkRange(id, min, max));
        }
    }

    private static async Task<PartitionPage> FetchPartitionPageAsync(
        CosmosClient cosmos, string collLink, string collUri, ReadOnlyMemory<byte> body,
        string rangeId, string? continuation, CancellationToken ct)
    {
        var headers = new List<KeyValuePair<string, string>>(4)
        {
            new("x-ms-documentdb-isquery", "true"),
            new("x-ms-max-item-count", PerPartitionPageSize.ToString(CultureInfo.InvariantCulture)),
            new("x-ms-documentdb-partitionkeyrangeid", rangeId),
        };
        if (!string.IsNullOrEmpty(continuation))
        {
            headers.Add(new KeyValuePair<string, string>("x-ms-continuation", continuation));
        }

        var resp = await cosmos.SendAsync(
            HttpMethod.Post, "docs", collLink, collUri, body, "application/query+json", headers, ct)
            .ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            throw new CosmosFeedException(resp, resp.StatusCode == System.Net.HttpStatusCode.NotFound);
        }

        using (resp)
        {
            var items = await CosmosOpsShared.ReadAndExtractItemsAsync(
                resp.Content, DynamoDbMetrics.OpQuery, ct).ConfigureAwait(false);
            string? next = null;
            if (resp.Headers.TryGetValues("x-ms-continuation", out var ctValues))
            {
                foreach (var v in ctValues) { next = v; break; }
            }
            return new PartitionPage(items, next);
        }
    }

    /// <summary>Carries a failed Cosmos feed/query response up to the entry
    /// point so it can emit the native DynamoDB error.</summary>
    internal sealed class CosmosFeedException : Exception
    {
        public HttpResponseMessage? Response { get; }
        public bool NotFound { get; }

        public CosmosFeedException(HttpResponseMessage response, bool notFound)
        {
            Response = response;
            NotFound = notFound;
        }
    }
}
