using Aws2Azure.Amqp.Connection;

namespace Aws2Azure.UnitTests.Amqp;

public sealed class ResourceSlotTests
{
    [Fact]
    public async Task GetOrCreateAsync_concurrent_callers_share_single_resource()
    {
        var slot = new ResourceSlot<FakeResource>(nameof(FakeResource), static resource => resource.IsClosed);
        var createCalls = 0;

        var callers = Enumerable.Range(0, 32)
            .Select(_ => slot.GetOrCreateAsync(
                0,
                async static (_, _) =>
                {
                    await Task.Yield();
                    return new FakeResource();
                },
                CancellationToken.None))
            .ToArray();

        var resources = await Task.WhenAll(callers);
        createCalls = resources.Distinct(ReferenceEqualityComparer.Instance).Count();

        Assert.Equal(1, createCalls);
        await slot.DisposeAsync();
    }

    [Fact]
    public async Task GetOrCreateAsync_replaces_closed_resource()
    {
        var slot = new ResourceSlot<FakeResource>(nameof(FakeResource), static resource => resource.IsClosed);
        var first = await slot.GetOrCreateAsync(0, static (_, _) => Task.FromResult(new FakeResource()), CancellationToken.None);

        first.IsClosed = true;
        var second = await slot.GetOrCreateAsync(0, static (_, _) => Task.FromResult(new FakeResource()), CancellationToken.None);

        Assert.NotSame(first, second);
        Assert.True(first.Disposed);
        await slot.DisposeAsync();
    }

    [Fact]
    public async Task DisposeAsync_disposes_existing_resource_and_blocks_reuse()
    {
        var slot = new ResourceSlot<FakeResource>(nameof(FakeResource), static resource => resource.IsClosed);
        var resource = await slot.GetOrCreateAsync(0, static (_, _) => Task.FromResult(new FakeResource()), CancellationToken.None);

        await slot.DisposeAsync();

        Assert.True(resource.Disposed);
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            slot.GetOrCreateAsync(0, static (_, _) => Task.FromResult(new FakeResource()), CancellationToken.None));
    }

    private sealed class FakeResource : IAsyncDisposable
    {
        public bool IsClosed { get; set; }
        public bool Disposed { get; private set; }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            IsClosed = true;
            return ValueTask.CompletedTask;
        }
    }
}
