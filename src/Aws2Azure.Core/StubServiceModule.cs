using Aws2Azure.Core.Modules;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.Core;

/// <summary>
/// Minimal stub used until each real service module ships. Returns 501 and
/// declares all of its operations as <see cref="OperationStatus.Stub"/>.
/// </summary>
public sealed class StubServiceModule : IServiceModule
{
    private readonly string[] _hostPrefixes;

    public StubServiceModule(
        string serviceName,
        AwsErrorFormat errorFormat,
        IReadOnlyList<string> stubbedOperations,
        params string[] hostPrefixes)
    {
        ServiceName = serviceName;
        ErrorFormat = errorFormat;
        _hostPrefixes = hostPrefixes;

        var ops = new List<OperationCapability>(stubbedOperations.Count);
        foreach (var op in stubbedOperations)
        {
            ops.Add(new OperationCapability(op, OperationStatus.Stub));
        }
        Capabilities = new CapabilityMatrix(serviceName, ops);
    }

    public StubServiceModule(
        CapabilityMatrix capabilities,
        AwsErrorFormat errorFormat,
        params string[] hostPrefixes)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        ServiceName = capabilities.ServiceName;
        ErrorFormat = errorFormat;
        Capabilities = capabilities;
        _hostPrefixes = hostPrefixes;
    }

    public string ServiceName { get; }
    public CapabilityMatrix Capabilities { get; }
    public AwsErrorFormat ErrorFormat { get; }

    /// <summary>
    /// Stubs intentionally skip SigV4 validation so the bootstrap proxy can
    /// be exercised end-to-end with <c>curl</c>. Real Phase-1+ modules
    /// override this to <c>true</c>.
    /// </summary>
    public bool RequiresSigV4 => false;

    public bool MatchesHost(string host)
    {
        if (string.IsNullOrEmpty(host))
        {
            return false;
        }

        foreach (var prefix in _hostPrefixes)
        {
            if (host.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    public ValueTask HandleAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status501NotImplemented;
        context.Response.ContentType = "text/plain; charset=utf-8";
        return new ValueTask(context.Response.WriteAsync(
            $"aws2azure: routed to stub module '{ServiceName}' (not yet implemented)\n"));
    }
}
