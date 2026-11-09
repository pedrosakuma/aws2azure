namespace Aws2Azure.Core.Azure;

/// <summary>
/// Base exception for caller-controlled request-content validation failures
/// raised while <see cref="HttpClient"/> is streaming a request body.
/// These failures are not Azure endpoint-health signals and must not count
/// against the backend circuit breaker.
/// </summary>
public abstract class RequestContentValidationException : IOException
{
    protected RequestContentValidationException(string message)
        : base(message)
    {
    }
}
