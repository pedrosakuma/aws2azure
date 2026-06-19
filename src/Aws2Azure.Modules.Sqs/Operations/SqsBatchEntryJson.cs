using System.Collections.Generic;
using System.Text.Json;

namespace Aws2Azure.Modules.Sqs.Operations;

/// <summary>
/// Shared parsing for the AWS-JSON wire form of SQS batch operations
/// (<c>SendMessageBatch</c>, <c>DeleteMessageBatch</c>,
/// <c>ChangeMessageVisibilityBatch</c>).
///
/// <para>Every SQS batch request carries the same envelope shape — a top-level
/// <c>Entries</c> array whose elements are objects, each with a required string
/// <c>Id</c> (the batch entry id) plus operation-specific fields. This helper
/// owns that common envelope walk so the per-operation handlers only supply the
/// element-specific field extraction.</para>
///
/// <para>Behaviour contract (preserved across all callers): a malformed body —
/// null/empty, non-JSON, missing/non-array <c>Entries</c>, a non-object element,
/// a missing/non-string <c>Id</c>, or any per-entry validation failure — fails
/// the <em>whole</em> batch by returning <c>null</c>. An empty <c>Entries</c>
/// array yields an empty list.</para>
/// </summary>
internal static class SqsBatchEntryJson
{
    /// <summary>
    /// Per-entry factory: given the entry object element and its validated
    /// string <paramref name="id"/>, extract the operation-specific fields into
    /// <paramref name="value"/>. Returns <c>false</c> to reject the entry (which
    /// fails the entire batch).
    /// </summary>
    internal delegate bool EntryFactory<T>(JsonElement entry, string id, out T value);

    /// <summary>
    /// Parses the <c>Entries</c> array from an AWS-JSON batch body, delegating
    /// per-entry field extraction to <paramref name="factory"/>. Returns the
    /// parsed entries, or <c>null</c> if the body is malformed or any entry is
    /// rejected. Pass a static method group for <paramref name="factory"/> so the
    /// delegate is cached (no per-call allocation).
    /// </summary>
    internal static List<T>? Parse<T>(string? jsonBody, EntryFactory<T> factory)
    {
        if (string.IsNullOrEmpty(jsonBody))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(jsonBody);
            if (!doc.RootElement.TryGetProperty("Entries", out var arr) ||
                arr.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var entries = new List<T>(arr.GetArrayLength());
            foreach (var e in arr.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object)
                {
                    return null;
                }

                if (!e.TryGetProperty("Id", out var idEl) || idEl.ValueKind != JsonValueKind.String)
                {
                    return null;
                }

                if (!factory(e, idEl.GetString()!, out var value))
                {
                    return null;
                }

                entries.Add(value);
            }

            return entries;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
