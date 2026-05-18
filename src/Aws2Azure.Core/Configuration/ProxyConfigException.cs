namespace Aws2Azure.Core.Configuration;

/// <summary>
/// Thrown by <see cref="ProxyConfigValidator"/> when configuration is
/// missing required fields, contains duplicates, or otherwise can't be
/// safely served. Startup catches this and exits non-zero.
/// </summary>
public sealed class ProxyConfigException : Exception
{
    public ProxyConfigException(string message) : base(message)
    {
    }

    public ProxyConfigException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
