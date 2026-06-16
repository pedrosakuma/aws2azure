namespace Aws2Azure.Amqp.Connection;

/// <summary>
/// Lazy, thread-safe owner for a pooled AMQP resource such as a sender,
/// receiver, or management link.
/// </summary>
internal sealed class ResourceSlot<T> : IAsyncDisposable where T : class, IAsyncDisposable
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly string _objectName;
    private readonly Func<T, bool> _isClosed;
    private T? _resource;
    private int _disposed;

    public ResourceSlot(string objectName, Func<T, bool> isClosed, T? initialResource = null)
    {
        _objectName = objectName;
        _isClosed = isClosed;
        _resource = initialResource;
    }

    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    public async Task<T> GetOrCreateAsync<TState>(
        TState state,
        Func<TState, CancellationToken, Task<T>> createAsync,
        CancellationToken cancellationToken)
    {
        var existing = Volatile.Read(ref _resource);
        if (existing is not null && !_isClosed(existing)) return existing;
        ThrowIfDisposed();

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            existing = _resource;
            if (existing is not null && !_isClosed(existing)) return existing;

            if (existing is not null)
            {
                try { await existing.DisposeAsync().ConfigureAwait(false); }
                catch { /* swallow during eviction */ }
                Volatile.Write(ref _resource, null);
            }

            var created = await createAsync(state, cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _resource, created);
            return created;
        }
        finally
        {
            _lock.Release();
        }
    }

    public T? TryGetExisting()
    {
        if (Volatile.Read(ref _disposed) != 0) return null;
        return Volatile.Read(ref _resource);
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(_objectName);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try
        {
            await _lock.WaitAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return;
        }
        try
        {
            var resource = Interlocked.Exchange(ref _resource, null);
            if (resource is not null)
                await resource.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
            _lock.Dispose();
        }
    }
}
