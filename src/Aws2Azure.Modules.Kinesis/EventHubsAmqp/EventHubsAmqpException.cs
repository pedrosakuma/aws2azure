namespace Aws2Azure.Modules.Kinesis.EventHubsAmqp;

internal enum EventHubsAmqpFailureKind
{
    Unknown = 0,
    Transient,
    Throttled,
    Auth,
    ClientFatal,
    ServerFatal,
    Redirect,
}

internal sealed class EventHubsAmqpException : Exception
{
    public EventHubsAmqpException(
        string message,
        Exception innerException,
        EventHubsAmqpFailureKind kind,
        string? condition = null,
        string? description = null)
        : base(message, innerException)
    {
        Kind = kind;
        Condition = condition;
        Description = description;
    }

    public EventHubsAmqpFailureKind Kind { get; }
    public string? Condition { get; }
    public string? Description { get; }
}
