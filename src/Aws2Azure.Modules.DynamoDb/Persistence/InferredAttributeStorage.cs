using System;
using System.Buffers;
using System.Buffers.Text;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Aws2Azure.Modules.DynamoDb.Internal;
using Aws2Azure.Modules.DynamoDb.Operations;

namespace Aws2Azure.Modules.DynamoDb.Persistence;

/// <summary>
/// DynamoDB ↔ Cosmos document persistence using <b>type inference</b>
/// driven by <see cref="JsonValueKind"/>, replacing the v1 byte-for-byte
/// item-envelope.
///
/// <para><b>Cosmos doc shape</b> (flat):</para>
/// <code>
/// {
///   "id":   "&lt;formatted sort-key&gt;",
///   "pk":   "&lt;formatted partition-key&gt;",
///   "_a2a": "item",
///   "&lt;attrName&gt;": &lt;inferred-or-envelope value&gt;,
///   ...
/// }
/// </code>
///
/// <para><b>Encoding rules</b> (DDB AttributeValue → Cosmos JSON):</para>
/// <list type="bullet">
///   <item><c>{S:v}</c> → bare string <c>"v"</c>.</item>
///   <item><c>{N:v}</c> → bare JSON number <c>v</c> if it round-trips
///     through <see cref="decimal"/> losslessly; otherwise envelope
///     <c>{"_a2a:N":"v"}</c> (preserves DDB 38-digit precision).</item>
///   <item><c>{BOOL:v}</c> → bare <c>true</c>/<c>false</c>.</item>
///   <item><c>{NULL:true}</c> → bare <c>null</c>.</item>
///   <item><c>{M:{...}}</c> → bare JSON object (recurse).</item>
///   <item><c>{L:[...]}</c> → bare JSON array (recurse).</item>
///   <item><c>{B:"&lt;b64&gt;"}</c> → envelope <c>{"_a2a:B":"&lt;b64&gt;"}</c>
///     (disambiguates from S).</item>
///   <item><c>{SS:[...]}</c> → envelope <c>{"_a2a:SS":[...]}</c>.</item>
///   <item><c>{NS:[...]}</c> → envelope <c>{"_a2a:NS":[...]}</c>.</item>
///   <item><c>{BS:[...]}</c> → envelope <c>{"_a2a:BS":[...]}</c>.</item>
/// </list>
///
/// <para><b>Decoding rules</b> (Cosmos JSON → DDB AttributeValue):</para>
/// <list type="bullet">
///   <item><see cref="JsonValueKind.String"/> → <c>{S:v}</c>.</item>
///   <item><see cref="JsonValueKind.Number"/> → <c>{N:rawText}</c>.</item>
///   <item><see cref="JsonValueKind.True"/>/<see cref="JsonValueKind.False"/>
///     → <c>{BOOL:v}</c>.</item>
///   <item><see cref="JsonValueKind.Null"/> → <c>{NULL:true}</c>.</item>
///   <item><see cref="JsonValueKind.Object"/>: if single property named
///     <c>_a2a:N</c>/<c>_a2a:B</c>/<c>_a2a:SS</c>/<c>_a2a:NS</c>/<c>_a2a:BS</c>
///     unwrap to corresponding typed attribute value; else <c>{M:...}</c>.</item>
///   <item><see cref="JsonValueKind.Array"/> → <c>{L:[...]}</c> (each
///     element recursively decoded).</item>
/// </list>
///
/// <para><b>Reserved top-level attribute names</b> in <c>PutItem</c>/
/// <c>UpdateItem</c> input: <c>id</c>, <c>pk</c>, <c>_a2a</c>, and any
/// name starting with <c>_a2a:</c>. <see cref="IsReservedTopLevelName"/>
/// returns true for these.</para>
/// </summary>
internal static partial class InferredAttributeStorage
{
    // Reserved Cosmos doc top-level property names. These collide with
    // routing/discriminator metadata or with envelope-tag syntax and must
    // be rejected at write time, never round-tripped.
    //
    // <para>Design note on naming: Cosmos requires the document
    // identifier field to be named exactly <c>id</c>. The partition-key
    // path is configurable per-collection — we use <c>/_a2a_pk</c> so
    // the much more common DDB attribute name <c>pk</c> stays available
    // for user data. <c>id</c> is the only DDB attr name that collides
    // with a Cosmos hard requirement; PutItem / UpdateItem shadow-encode
    // such an attribute under the <see cref="ShadowPrefix"/> namespace
    // ("_a2a$id") so the user's <c>id</c> attribute round-trips losslessly
    // without clobbering the routing field.</para>
    public const string IdProperty = "id";
    public const string PkProperty = "_a2a_pk";
    public const string DiscriminatorProperty = "_a2a";
    public const string DiscriminatorValueItem = "item";

    // Shadow-encoding namespace for DDB attribute names that collide
    // with reserved Cosmos doc property names (currently only "id").
    // Distinct from the envelope-tag prefix ("_a2a:") so encoder/decoder
    // can disambiguate purely by character.
    public const string ShadowPrefix = "_a2a$";
    public const string ShadowEncodedIdName = "_a2a$id";

    // Envelope tag prefix. All five ambiguous-type tags live under this
    // namespace so detection is a single substring check.
    public const string EnvelopeTagPrefix = "_a2a:";
    public const string EnvelopeTagN = "_a2a:N";
    public const string EnvelopeTagB = "_a2a:B";
    public const string EnvelopeTagSS = "_a2a:SS";
    public const string EnvelopeTagNS = "_a2a:NS";
    public const string EnvelopeTagBS = "_a2a:BS";

    // Pre-encoded property names — avoids JS-escape work and per-call
    // allocations on the hot path. IdPropEncoded is still consumed by the
    // read path (Cosmos → DDB); the remaining write-path names moved to the
    // dual-back-end TokenName block below.
    private static readonly JsonEncodedText IdPropEncoded = JsonEncodedText.Encode(IdProperty);
    private static readonly JsonEncodedText ItemPropEncoded = JsonEncodedText.Encode("Item");
    private static readonly JsonEncodedText TagS = JsonEncodedText.Encode(AttributeValueTypes.String);
    private static readonly JsonEncodedText TagN = JsonEncodedText.Encode(AttributeValueTypes.Number);
    private static readonly JsonEncodedText TagBool = JsonEncodedText.Encode(AttributeValueTypes.Bool);
    private static readonly JsonEncodedText TagNull = JsonEncodedText.Encode(AttributeValueTypes.Null);
    private static readonly JsonEncodedText TagM = JsonEncodedText.Encode(AttributeValueTypes.Map);
    private static readonly JsonEncodedText TagL = JsonEncodedText.Encode(AttributeValueTypes.List);
    private static readonly JsonEncodedText TagB = JsonEncodedText.Encode(AttributeValueTypes.Binary);
    private static readonly JsonEncodedText TagSS = JsonEncodedText.Encode(AttributeValueTypes.StringSet);
    private static readonly JsonEncodedText TagNS = JsonEncodedText.Encode(AttributeValueTypes.NumberSet);
    private static readonly JsonEncodedText TagBS = JsonEncodedText.Encode(AttributeValueTypes.BinarySet);

    // Write-path constant names/values, pre-encoded for BOTH back-ends
    // (escaped JSON text + verbatim UTF-8) so the shared ITokenWriter walk can
    // target either the JSON-text or the CosmosBinary writer (#332/#335).
    private static readonly TokenName IdPropName = new(IdProperty);
    private static readonly TokenName PkPropName = new(PkProperty);
    private static readonly TokenName DiscPropName = new(DiscriminatorProperty);
    private static readonly TokenName DiscValueItemName = new(DiscriminatorValueItem);
    private static readonly TokenName ShadowIdPropName = new(ShadowEncodedIdName);
    private static readonly TokenName EnvelopeNName = new(EnvelopeTagN);
    private static readonly TokenName EnvelopeBName = new(EnvelopeTagB);
    private static readonly TokenName EnvelopeSSName = new(EnvelopeTagSS);
    private static readonly TokenName EnvelopeNSName = new(EnvelopeTagNS);
    private static readonly TokenName EnvelopeBSName = new(EnvelopeTagBS);

    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        SkipValidation = false,
    };

    /// <summary>
    /// True if <paramref name="name"/> is a reserved Cosmos doc top-level
    /// property name — i.e. it collides with routing (<see cref="IdProperty"/>,
    /// <see cref="PkProperty"/>), the discriminator (<see cref="DiscriminatorProperty"/>),
    /// any envelope tag (<see cref="EnvelopeTagPrefix"/>...), or any
    /// shadow-encoded name (<see cref="ShadowPrefix"/>...). PutItem /
    /// UpdateItem must reject attributes that target these names so the
    /// read path can safely skip them. The only DDB attribute name a
    /// caller would naturally pick that lands here is <c>id</c>; for
    /// that case the encoder transparently shadow-encodes the attribute
    /// rather than rejecting the write.
    /// </summary>
    public static bool IsReservedTopLevelName(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        if (name == IdProperty || name == PkProperty || name == DiscriminatorProperty)
            return true;
        // Any name in the _a2a namespace (envelope tags, shadow names,
        // future reserved props) — keep the user out of it entirely so
        // we have freedom to extend the schema without breaking writes.
        return name.StartsWith(DiscriminatorProperty, StringComparison.Ordinal);
    }

    /// <summary>
    /// True if <paramref name="name"/> is a Cosmos-internal system field that
    /// Cosmos injects at the document root on every read (<c>_rid</c>,
    /// <c>_self</c>, <c>_etag</c>, <c>_ts</c>, <c>_attachments</c>, and on some
    /// response shapes <c>_lsn</c>/<c>_metadata</c>). These are storage
    /// metadata, never user data, and must be stripped from every DynamoDB
    /// read response (#203).
    /// </summary>
    /// <remarks>
    /// Read-only by design: unlike <see cref="IsReservedTopLevelName"/> this is
    /// NOT consulted by the encoder, so a user attribute literally named e.g.
    /// <c>_etag</c> is still written verbatim and then stripped on read.
    /// Disambiguating that rare collision requires namespacing user attributes
    /// (see #203); the allowlist is intentionally exact — never a <c>_</c>
    /// wildcard — to avoid eating legitimate user attributes such as <c>ttl</c>.
    /// </remarks>
    public static bool IsCosmosSystemField(string name) => name switch
    {
        "_rid" or "_self" or "_etag" or "_ts" or "_attachments" or "_lsn" or "_metadata" => true,
        _ => false,
    };

    /// <summary>
    /// True if the attribute name can be written directly at the root
    /// without shadow-encoding. The only collision the encoder rewrites
    /// transparently is <c>id</c>; every other reserved name is a hard
    /// validation failure (those would be names the user actively chose
    /// to put under the <c>_a2a</c> namespace).
    /// </summary>
    public static bool IsShadowEncodableName(string name)
        => string.Equals(name, IdProperty, StringComparison.Ordinal);

}
