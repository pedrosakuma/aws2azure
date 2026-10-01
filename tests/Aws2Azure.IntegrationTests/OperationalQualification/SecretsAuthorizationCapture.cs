using System.Globalization;

namespace Aws2Azure.IntegrationTests.OperationalQualification;

internal sealed class SecretsAuthorizationCapture
{
    internal const int EventLimit = 64;
    internal const int LineLimit = 512;
    internal const string Category = "Aws2Azure.Modules.SecretsManager.SecretsManagerServiceModule";
    private readonly object _gate = new();
    private readonly List<SecretsAuthorizationEvent> _events = [];
    private readonly int[] _pending = new int[2];
    private readonly bool[] _closed = new bool[2];
    private long _rejected;
    private long _dropped;

    internal void Observe(string? line, bool standardError)
    {
        var stream = standardError ? 1 : 0;
        lock (_gate)
        {
            var eventId = _pending[stream];
            _pending[stream] = 0;
            if (line is null)
            {
                _closed[stream] = true;
                if (eventId != 0) _rejected++;
                return;
            }

            if (eventId != 0)
            {
                var entry = Parse(line, eventId);
                if (entry is null) _rejected++;
                else if (_events.Count == EventLimit) _dropped++;
                else _events.Add(entry);
            }

            // The simple console formatter writes a header and indented payload
            // on the same stream. Never pair stdout with stderr or scan substrings.
            _pending[stream] = line switch
            {
                "warn: " + Category + "[5]" => 5,
                "warn: " + Category + "[6]" => 6,
                _ => 0,
            };
        }
    }

    internal SecretsAuthorizationEvidence Snapshot()
    {
        lock (_gate)
        {
            return new()
            {
                Events = _events.ToArray(),
                RejectedCandidates = _rejected,
                DroppedEvents = _dropped,
                StreamsClosed = _closed[0] && _closed[1],
            };
        }
    }

    private static SecretsAuthorizationEvent? Parse(string line, int eventId)
    {
        const string tokenPrefix = "      Secrets Manager Entra token acquisition failed. ";
        const string vaultPrefix = "      Secrets Manager Key Vault authorization failed. ";
        var prefix = eventId == 5 ? tokenPrefix : vaultPrefix;
        if (line.Length > LineLimit || !line.StartsWith(prefix, StringComparison.Ordinal))
            return null;
        var fields = line[prefix.Length..].Split(' ');
        if (fields.Length != 4
            || !fields[0].StartsWith("Operation=", StringComparison.Ordinal)
            || !fields[1].StartsWith("RequestId=", StringComparison.Ordinal)
            || !fields[2].StartsWith(eventId == 5 ? "TokenStatus=" : "UpstreamStatus=", StringComparison.Ordinal)
            || !fields[3].StartsWith("UpstreamRequestId=", StringComparison.Ordinal))
            return null;
        var operation = fields[0]["Operation=".Length..];
        var requestId = fields[1]["RequestId=".Length..];
        var statusText = fields[2][(eventId == 5 ? "TokenStatus=".Length : "UpstreamStatus=".Length)..];
        var upstreamId = fields[3]["UpstreamRequestId=".Length..];
        if (operation is not ("CreateSecret" or "DescribeSecret" or "GetSecretValue"
            or "PutSecretValue" or "UpdateSecret" or "ListSecrets" or "DeleteSecret"
            or "TagResource" or "UntagResource")
            || !IsRequestId(requestId)
            || statusText.Length != 3
            || !int.TryParse(statusText, NumberStyles.None, CultureInfo.InvariantCulture, out var status)
            || status is < 400 or > 599
            || (eventId == 6 && status is not (401 or 403))
            || (eventId == 5 ? upstreamId != "unavailable" : !IsGuidOrUnavailable(upstreamId)))
            return null;
        return new(DateTimeOffset.UtcNow, eventId, eventId == 5 ? "entra" : "key_vault",
            operation, requestId, status, upstreamId);
    }

    private static bool IsGuidOrUnavailable(string value) =>
        value == "unavailable"
        || (value.Length == 36 && Guid.TryParseExact(value, "D", out _))
        || (value.Length == 32 && Guid.TryParseExact(value, "N", out _));

    private static bool IsRequestId(string value)
    {
        if (IsGuidOrUnavailable(value)) return true;
        if (value.Length != 22 || value[13] != ':') return false;
        for (var index = 0; index < value.Length; index++)
        {
            if (index == 13) continue;
            var c = value[index];
            if (!(c is >= '0' and <= '9' || c >= 'A' && c <= (index < 13 ? 'V' : 'F')))
                return false;
        }
        return true;
    }
}

internal sealed record SecretsAuthorizationEvent(
    DateTimeOffset ObservedAtUtc, int EventId, string Source, string Operation,
    string RequestId, int UpstreamStatus, string UpstreamRequestId);

internal sealed class SecretsAuthorizationEvidence
{
    public int EventLimit => SecretsAuthorizationCapture.EventLimit;
    public string Scope => "allowlisted_simple_console_events_5_6_arrival_time_not_provider_time";
    public SecretsAuthorizationEvent[] Events { get; init; } = [];
    public long RejectedCandidates { get; init; }
    public long DroppedEvents { get; init; }
    public bool Truncated => DroppedEvents != 0;
    public bool StreamsClosed { get; init; }
}
