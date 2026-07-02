using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text.Json;

namespace Aws2Azure.Modules.DynamoDb.Expressions;

/// <summary>
/// A compiled DynamoDB <c>ProjectionExpression</c>: the set of document paths to
/// retain from each item. Supports top-level attributes (<c>a</c>), nested map
/// members (<c>a.b</c>) and list indices (<c>a[0]</c>), following DynamoDB
/// semantics — a projected map keeps only the referenced members, a projected
/// list is compacted to the referenced elements, and a path that does not exist
/// (or whose type does not match) is silently omitted.
/// </summary>
internal sealed class Projection
{
    private readonly ProjectionNode _root;

    private Projection(ProjectionNode root, IReadOnlyList<string> rootNames, bool hasNestedPaths)
    {
        _root = root;
        RootNames = rootNames;
        HasNestedPaths = hasNestedPaths;
    }

    /// <summary>Distinct top-level (root) attribute names in first-seen order.
    /// Used for index-projection coverage checks.</summary>
    public IReadOnlyList<string> RootNames { get; }

    /// <summary>True when at least one path descends below the top level. When
    /// false, <see cref="Apply"/> uses an allocation-free whole-attribute copy.</summary>
    public bool HasNestedPaths { get; }

    /// <summary>
    /// Builds a projection from parsed document paths. Rejects overlapping paths
    /// (for example <c>a</c> and <c>a.b</c>, or a duplicate) with an
    /// <see cref="ExpressionSyntaxException"/>, mirroring DynamoDB.
    /// </summary>
    public static Projection FromDocumentPaths(IReadOnlyList<DocumentPath> paths)
    {
        var root = new ProjectionNode();
        var rootNames = new List<string>();
        var seenRoots = new HashSet<string>(StringComparer.Ordinal);
        var hasNested = false;

        foreach (var path in paths)
        {
            var node = root;
            var segments = path.Segments;
            for (var i = 0; i < segments.Count; i++)
            {
                if (node.KeepWhole)
                {
                    // A shorter path already terminates at this node.
                    throw Overlap(path);
                }

                node = segments[i] switch
                {
                    AttributePathSegment a => node.GetOrAddAttrChild(a.Name),
                    IndexPathSegment idx => node.GetOrAddIndexChild(idx.Index),
                    _ => throw Overlap(path),
                };
            }

            if (node.KeepWhole || node.HasChildren)
            {
                // Duplicate path, or a longer path already descends from here.
                throw Overlap(path);
            }
            node.KeepWhole = true;

            if (segments.Count > 1)
            {
                hasNested = true;
            }

            var rootName = path.Root;
            if (seenRoots.Add(rootName))
            {
                rootNames.Add(rootName);
            }
        }

        return new Projection(root, rootNames, hasNested);
    }

    /// <summary>
    /// Builds a projection from a flat list of top-level attribute names (for
    /// example an index projection). Never nested; duplicates are ignored.
    /// </summary>
    public static Projection FromTopLevelNames(IReadOnlyList<string> names)
    {
        var root = new ProjectionNode();
        var rootNames = new List<string>();
        var seenRoots = new HashSet<string>(StringComparer.Ordinal);

        foreach (var name in names)
        {
            var child = root.GetOrAddAttrChild(name);
            child.KeepWhole = true;
            if (seenRoots.Add(name))
            {
                rootNames.Add(name);
            }
        }

        return new Projection(root, rootNames, hasNestedPaths: false);
    }

    /// <summary>
    /// Returns a new item containing only the projected paths. The input item is
    /// a DynamoDB item map (attribute name → typed AttributeValue JSON).
    /// </summary>
    public Dictionary<string, JsonElement> Apply(Dictionary<string, JsonElement> item)
    {
        if (_root.AttrChildren is null)
        {
            return new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        }

        // Pre-size to the projected root count: at most one entry per top-level
        // child, so the result never resizes (avoids the 3→7→17 grow churn).
        var result = new Dictionary<string, JsonElement>(_root.AttrChildren.Count, StringComparer.Ordinal);

        foreach (var (name, node) in _root.AttrChildren)
        {
            if (!item.TryGetValue(name, out var av))
            {
                continue;
            }

            if (node.KeepWhole)
            {
                // Whole top-level attribute — reference the original value, no copy.
                result[name] = av;
                continue;
            }

            if (ApplyNode(node, av) is { } pruned)
            {
                result[name] = pruned;
            }
        }

        return result;
    }

    private static JsonElement? ApplyNode(ProjectionNode node, JsonElement av)
    {
        if (node.KeepWhole)
        {
            return av;
        }

        if (BuildPruned(node, av) is { } pruned)
        {
            return Materialize(pruned);
        }
        return null;
    }

    /// <summary>
    /// Prunes a typed AttributeValue against a projection node, returning a small
    /// CLR tree (or null when nothing matched). The tree is serialized once per
    /// top-level attribute by <see cref="Materialize"/>, avoiding a re-parse at
    /// every nesting level.
    /// </summary>
    private static Pruned? BuildPruned(ProjectionNode node, JsonElement av)
    {
        if (node.KeepWhole)
        {
            return new Pruned(av);
        }

        if (av.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        // Map descent: {"M": { name: av, ... }}
        if (node.AttrChildren is not null
            && av.TryGetProperty("M", out var mapEl)
            && mapEl.ValueKind == JsonValueKind.Object)
        {
            List<KeyValuePair<string, Pruned>>? members = null;
            foreach (var (name, child) in node.AttrChildren)
            {
                if (mapEl.TryGetProperty(name, out var childAv)
                    && BuildPruned(child, childAv) is { } prunedChild)
                {
                    (members ??= new List<KeyValuePair<string, Pruned>>())
                        .Add(new KeyValuePair<string, Pruned>(name, prunedChild));
                }
            }
            return members is null ? null : new Pruned(members);
        }

        // List descent: {"L": [ av, ... ]} — compacted to matched indices in
        // ascending order (DynamoDB drops unprojected elements).
        if (node.IndexChildren is not null
            && av.TryGetProperty("L", out var listEl)
            && listEl.ValueKind == JsonValueKind.Array)
        {
            var length = listEl.GetArrayLength();
            List<Pruned>? elements = null;
            foreach (var (index, child) in node.IndexChildren)
            {
                if (index < length
                    && BuildPruned(child, listEl[index]) is { } prunedChild)
                {
                    (elements ??= new List<Pruned>()).Add(prunedChild);
                }
            }
            return elements is null ? null : new Pruned(elements);
        }

        return null;
    }

    private static JsonElement Materialize(Pruned pruned)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            WritePruned(writer, pruned);
        }

        // Clone into a self-contained, dispose-free document (GC-backed, not
        // ArrayPool) so the transient parse buffer is returned to the pool. A
        // no-Clone variant that keeps the Parse(ReadOnlyMemory) document alive
        // measures cheaper here but leaks its pooled metadata DB, depleting
        // ArrayPool<byte>.Shared for the whole read path — a false economy the
        // alloc microbench cannot see (a warm-pool rent counts as zero bytes).
        using var doc = JsonDocument.Parse(buffer.WrittenMemory);
        return doc.RootElement.Clone();
    }

    private static void WritePruned(Utf8JsonWriter writer, Pruned pruned)
    {
        if (pruned.Whole is { } whole)
        {
            whole.WriteTo(writer);
            return;
        }

        if (pruned.MapMembers is { } members)
        {
            writer.WriteStartObject();
            writer.WritePropertyName("M");
            writer.WriteStartObject();
            foreach (var member in members)
            {
                writer.WritePropertyName(member.Key);
                WritePruned(writer, member.Value);
            }
            writer.WriteEndObject();
            writer.WriteEndObject();
            return;
        }

        // List
        writer.WriteStartObject();
        writer.WritePropertyName("L");
        writer.WriteStartArray();
        foreach (var element in pruned.ListElements!)
        {
            WritePruned(writer, element);
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static ExpressionSyntaxException Overlap(DocumentPath path)
        => new(0, $"Two document paths overlap with each other; " +
                  $"must remove or rewrite one of these paths; path: {path.Display}");

    /// <summary>Node in the projection trie. A node either terminates a path
    /// (<see cref="KeepWhole"/>) or descends via attribute and/or index children.</summary>
    private sealed class ProjectionNode
    {
        public bool KeepWhole { get; set; }
        public Dictionary<string, ProjectionNode>? AttrChildren { get; private set; }
        public SortedDictionary<int, ProjectionNode>? IndexChildren { get; private set; }

        public bool HasChildren => AttrChildren is not null || IndexChildren is not null;

        public ProjectionNode GetOrAddAttrChild(string name)
        {
            AttrChildren ??= new Dictionary<string, ProjectionNode>(StringComparer.Ordinal);
            if (!AttrChildren.TryGetValue(name, out var child))
            {
                child = new ProjectionNode();
                AttrChildren[name] = child;
            }
            return child;
        }

        public ProjectionNode GetOrAddIndexChild(int index)
        {
            // SortedDictionary keeps ascending index order for list compaction.
            IndexChildren ??= new SortedDictionary<int, ProjectionNode>();
            if (!IndexChildren.TryGetValue(index, out var child))
            {
                child = new ProjectionNode();
                IndexChildren[index] = child;
            }
            return child;
        }
    }

    /// <summary>A pruned subtree awaiting serialization: either a whole typed
    /// AttributeValue, a map of pruned members, or a compacted list.</summary>
    private readonly struct Pruned
    {
        public Pruned(JsonElement whole)
        {
            Whole = whole;
            MapMembers = null;
            ListElements = null;
        }

        public Pruned(List<KeyValuePair<string, Pruned>> mapMembers)
        {
            Whole = null;
            MapMembers = mapMembers;
            ListElements = null;
        }

        public Pruned(List<Pruned> listElements)
        {
            Whole = null;
            MapMembers = null;
            ListElements = listElements;
        }

        public JsonElement? Whole { get; }
        public List<KeyValuePair<string, Pruned>>? MapMembers { get; }
        public List<Pruned>? ListElements { get; }
    }
}
