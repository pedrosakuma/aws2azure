using Aws2Azure.Amqp.Connection;
using Aws2Azure.Amqp.ServiceBus;
using Aws2Azure.UnitTests.Amqp.Transport;

namespace Aws2Azure.UnitTests.Amqp.ServiceBus;

/// <summary>
/// Tests for <see cref="ServiceBusAmqpPool"/>. Uses an in-memory factory
/// that hands back real <see cref="ServiceBusAmqpConnection"/> instances
/// driven by the broker simulator — same fixture as the connection
/// tests, just wrapped behind the pool's factory interface.
/// </summary>
public sealed class ServiceBusAmqpPoolTests
{
    private static AmqpConnectionSettings DefaultSettings() => new()
    {
        ContainerId = "test-client",
        Hostname = "ns.servicebus.windows.net",
        IdleTimeout = TimeSpan.Zero,
    };

    [Fact]
    public async Task GetReceiverAsync_creates_connection_and_receiver_on_first_call()
    {
        await using var factory = new FakeFactory(DefaultSettings());
        await using var pool = new ServiceBusAmqpPool(factory);

        var receiver = await pool
            .GetReceiverAsync(ServiceBusAmqpEndpoint.Tls("ns.servicebus.windows.net"), "RootKey", "secret", "queue-a")
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("queue-a", receiver.QueueName);
        Assert.Equal(1, factory.CreateCallCount);
        Assert.Equal(1, pool.ConnectionCount);
    }

    [Fact]
    public async Task GetReceiverAsync_reuses_connection_across_queues_under_same_key()
    {
        await using var factory = new FakeFactory(DefaultSettings());
        await using var pool = new ServiceBusAmqpPool(factory);

        var r1 = await pool.GetReceiverAsync(ServiceBusAmqpEndpoint.Tls("ns.servicebus.windows.net"), "Root", "k", "queue-a")
            .WaitAsync(TimeSpan.FromSeconds(10));
        var r2 = await pool.GetReceiverAsync(ServiceBusAmqpEndpoint.Tls("ns.servicebus.windows.net"), "Root", "k", "queue-b")
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.NotSame(r1, r2);
        Assert.Equal(1, factory.CreateCallCount);
        Assert.Equal(1, pool.ConnectionCount);
    }

    [Fact]
    public async Task GetReceiverAsync_returns_cached_receiver_on_repeat_call()
    {
        await using var factory = new FakeFactory(DefaultSettings());
        await using var pool = new ServiceBusAmqpPool(factory);

        var r1 = await pool.GetReceiverAsync(ServiceBusAmqpEndpoint.Tls("ns.servicebus.windows.net"), "Root", "k", "queue-a")
            .WaitAsync(TimeSpan.FromSeconds(10));
        var r2 = await pool.GetReceiverAsync(ServiceBusAmqpEndpoint.Tls("ns.servicebus.windows.net"), "Root", "k", "queue-a")
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Same(r1, r2);
        Assert.Equal(1, factory.CreateCallCount);
    }

    [Fact]
    public async Task GetReceiverAsync_creates_separate_connections_per_key()
    {
        await using var factory = new FakeFactory(DefaultSettings());
        await using var pool = new ServiceBusAmqpPool(factory);

        await pool.GetReceiverAsync(ServiceBusAmqpEndpoint.Tls("ns.servicebus.windows.net"), "Root", "k", "q1")
            .WaitAsync(TimeSpan.FromSeconds(10));
        await pool.GetReceiverAsync(ServiceBusAmqpEndpoint.Tls("ns.servicebus.windows.net"), "Other", "k", "q1")
            .WaitAsync(TimeSpan.FromSeconds(10));
        await pool.GetReceiverAsync(ServiceBusAmqpEndpoint.Tls("ns2.servicebus.windows.net"), "Root", "k", "q1")
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(3, factory.CreateCallCount);
        Assert.Equal(3, pool.ConnectionCount);
    }

    [Fact]
    public async Task Namespace_lookup_is_case_insensitive()
    {
        await using var factory = new FakeFactory(DefaultSettings());
        await using var pool = new ServiceBusAmqpPool(factory);

        var r1 = await pool.GetReceiverAsync(ServiceBusAmqpEndpoint.Tls("ns.servicebus.windows.net"), "Root", "k", "q1")
            .WaitAsync(TimeSpan.FromSeconds(10));
        var r2 = await pool.GetReceiverAsync(ServiceBusAmqpEndpoint.Tls("NS.SERVICEBUS.WINDOWS.NET"), "Root", "k", "q1")
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Same(r1, r2);
        Assert.Equal(1, factory.CreateCallCount);
    }

    [Fact]
    public async Task InvalidateAsync_with_closeConnection_drops_slot_and_next_call_recreates()
    {
        await using var factory = new FakeFactory(DefaultSettings());
        await using var pool = new ServiceBusAmqpPool(factory);

        await pool.GetReceiverAsync(ServiceBusAmqpEndpoint.Tls("ns.servicebus.windows.net"), "Root", "k", "q1")
            .WaitAsync(TimeSpan.FromSeconds(10));
        await pool.InvalidateReceiverAsync(ServiceBusAmqpEndpoint.Tls("ns.servicebus.windows.net"), "Root", "q1", closeConnection: true);
        Assert.Equal(0, pool.ConnectionCount);

        await pool.GetReceiverAsync(ServiceBusAmqpEndpoint.Tls("ns.servicebus.windows.net"), "Root", "k", "q1")
            .WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(2, factory.CreateCallCount);
    }

    [Fact]
    public async Task InvalidateAsync_receiver_only_keeps_connection_warm()
    {
        await using var factory = new FakeFactory(DefaultSettings());
        await using var pool = new ServiceBusAmqpPool(factory);

        await pool.GetReceiverAsync(ServiceBusAmqpEndpoint.Tls("ns.servicebus.windows.net"), "Root", "k", "q1")
            .WaitAsync(TimeSpan.FromSeconds(10));
        await pool.InvalidateReceiverAsync(ServiceBusAmqpEndpoint.Tls("ns.servicebus.windows.net"), "Root", "q1", closeConnection: false);

        Assert.Equal(1, pool.ConnectionCount);

        await pool.GetReceiverAsync(ServiceBusAmqpEndpoint.Tls("ns.servicebus.windows.net"), "Root", "k", "q1")
            .WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, factory.CreateCallCount);
    }

    [Fact]
    public async Task Concurrent_first_callers_share_one_connection()
    {
        await using var factory = new FakeFactory(DefaultSettings());
        await using var pool = new ServiceBusAmqpPool(factory);

        var tasks = Enumerable.Range(0, 8).Select(_ =>
            pool.GetReceiverAsync(ServiceBusAmqpEndpoint.Tls("ns.servicebus.windows.net"), "Root", "k", "q1")).ToArray();
        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10));

        var receivers = tasks.Select(t => t.Result).ToArray();
        Assert.All(receivers, r => Assert.Same(receivers[0], r));
        Assert.Equal(1, factory.CreateCallCount);
    }

    [Fact]
    public async Task Disposed_pool_rejects_new_requests()
    {
        await using var factory = new FakeFactory(DefaultSettings());
        var pool = new ServiceBusAmqpPool(factory);
        await pool.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            pool.GetReceiverAsync(ServiceBusAmqpEndpoint.Tls("ns.servicebus.windows.net"), "Root", "k", "q1"));
    }

    [Fact]
    public async Task DisposeAsync_waits_for_in_flight_creator_and_disposes_result()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var inner = new FakeFactory(DefaultSettings());
        var factory = new GatedFactory(inner, gate.Task);
        var pool = new ServiceBusAmqpPool(factory);

        var inflight = Task.Run(() => pool.GetReceiverAsync(ServiceBusAmqpEndpoint.Tls("ns.servicebus.windows.net"), "Root", "k", "q1"));

        // Let the creator start and park inside CreateAsync.
        while (factory.CreateEntered == 0)
            await Task.Delay(10);

        // Start disposing; must not complete until the creator finishes.
        var disposeTask = pool.DisposeAsync().AsTask();
        await Task.Delay(50);
        Assert.False(disposeTask.IsCompleted);

        // Release the creator. Disposer should now drain and dispose the
        // connection that was just published into the slot.
        gate.SetResult();
        await disposeTask.WaitAsync(TimeSpan.FromSeconds(10));

        await Assert.ThrowsAsync<ObjectDisposedException>(() => inflight);
        Assert.Equal(1, factory.CreateCallCount);
    }

    [Fact]
    public async Task DisposeAsync_concurrent_with_publishers_leaves_no_orphan_slots_or_connections()
    {
        // Reproduces the add-after-snapshot dispose race: many parallel
        // GetReceiverAsync calls publish ConnectionSlots into the pool
        // dict while a DisposeAsync racing in parallel snapshots and
        // tears them down. Every connection the factory hands out must
        // be either (a) returned to a caller that then sees it disposed
        // via the post-publish ThrowIfDisposed re-check, or (b) drained
        // by DisposeAsync. None should leak.

        for (int iter = 0; iter < 5; iter++)
        {
            await using var factory = new TrackingFactory(DefaultSettings());
            var pool = new ServiceBusAmqpPool(factory);

            // Vary the (key, queue) tuples so we exercise both
            // _connections and _receivers publish paths.
            const int Parallelism = 32;
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var tasks = new Task[Parallelism];
            for (int i = 0; i < Parallelism; i++)
            {
                var keyName = $"key-{i % 4}";
                var queue = $"q-{i % 8}";
                tasks[i] = Task.Run(async () =>
                {
                    await start.Task.ConfigureAwait(false);
                    try
                    {
                        await pool.GetReceiverAsync(ServiceBusAmqpEndpoint.Tls("ns.servicebus.windows.net"), keyName, "k", queue);
                    }
                    catch (ObjectDisposedException) { /* expected when racing */ }
                });
            }

            // Fire all acquires and dispose roughly simultaneously.
            start.SetResult();
            var disposeTask = Task.Run(async () =>
            {
                // Tiny stagger so some acquires publish before dispose
                // starts and others after.
                await Task.Delay(1);
                await pool.DisposeAsync();
            });

            await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(30));
            await disposeTask.WaitAsync(TimeSpan.FromSeconds(30));

            // Invariant 1: dict drained.
            Assert.Equal(0, pool.ConnectionCount);

            // Invariant 2: no connection leaked. Every connection that
            // the factory created must have been disposed by either the
            // pool's drain or the publish-and-guard self-cleanup.
            Assert.Equal(factory.CreateCallCount, factory.DisposeCallCount);
        }
    }

    [Fact]
    public async Task GetReceiverAsync_after_dispose_does_not_publish_orphan_slot()
    {
        // Directly exercises the publish-and-guard path: after Dispose
        // sets the flag, a subsequent GetReceiverAsync may race ahead
        // and publish a fresh ConnectionSlot via GetOrAdd before the
        // pool-level ThrowIfDisposed catches it. The guard inside
        // GetOrCreateConnectionSlot must pull that slot back out so the
        // dict stays empty.

        await using var factory = new TrackingFactory(DefaultSettings());
        var pool = new ServiceBusAmqpPool(factory);
        await pool.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            pool.GetReceiverAsync(ServiceBusAmqpEndpoint.Tls("ns.servicebus.windows.net"), "Root", "k", "q1"));

        Assert.Equal(0, pool.ConnectionCount);
        Assert.Equal(0, factory.CreateCallCount); // bailed before opening anything
    }

    [Fact]
    public async Task TryGetExistingSessionReceiver_returns_null_when_no_slot_exists()
    {
        await using var factory = new FakeFactory(DefaultSettings());
        await using var pool = new ServiceBusAmqpPool(factory);

        // Connection never opened — no slot exists.
        var none = pool.TryGetExistingSessionReceiver(ServiceBusAmqpEndpoint.Tls("ns.servicebus.windows.net"), "Root", "fifo-q", "session-x");
        Assert.Null(none);
        Assert.Equal(0, factory.CreateCallCount);

        // Connection opened for a *different* session — slot for the
        // queried session-id still does not exist. Crucially, the
        // probe must not open a new session lock.
        await pool.GetSessionReceiverAsync(ServiceBusAmqpEndpoint.Tls("ns.servicebus.windows.net"), "Root", "k", "fifo-q", "session-1")
            .WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Null(pool.TryGetExistingSessionReceiver(ServiceBusAmqpEndpoint.Tls("ns.servicebus.windows.net"), "Root", "fifo-q", "session-other"));

        // Existing slot is returned.
        var cached = pool.TryGetExistingSessionReceiver(ServiceBusAmqpEndpoint.Tls("ns.servicebus.windows.net"), "Root", "fifo-q", "session-1");
        Assert.NotNull(cached);
        Assert.Equal("session-1", cached!.SessionId);
    }

    [Fact]
    public async Task TryGetExistingSessionReceiver_returns_null_after_invalidate()
    {
        await using var factory = new FakeFactory(DefaultSettings());
        await using var pool = new ServiceBusAmqpPool(factory);

        await pool.GetSessionReceiverAsync(ServiceBusAmqpEndpoint.Tls("ns.servicebus.windows.net"), "Root", "k", "fifo-q", "session-1")
            .WaitAsync(TimeSpan.FromSeconds(10));

        await pool.InvalidateSessionReceiverAsync(ServiceBusAmqpEndpoint.Tls("ns.servicebus.windows.net"), "Root", "fifo-q", "session-1");

        Assert.Null(pool.TryGetExistingSessionReceiver(ServiceBusAmqpEndpoint.Tls("ns.servicebus.windows.net"), "Root", "fifo-q", "session-1"));
    }

    [Fact]
    public async Task GetSessionReceiverAsync_creates_and_caches_per_session_id()
    {
        await using var factory = new FakeFactory(DefaultSettings());
        await using var pool = new ServiceBusAmqpPool(factory);

        var r1 = await pool.GetSessionReceiverAsync(ServiceBusAmqpEndpoint.Tls("ns.servicebus.windows.net"), "Root", "k", "fifo-q", "session-1")
            .WaitAsync(TimeSpan.FromSeconds(10));
        var r2 = await pool.GetSessionReceiverAsync(ServiceBusAmqpEndpoint.Tls("ns.servicebus.windows.net"), "Root", "k", "fifo-q", "session-1")
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Same(r1, r2);
        Assert.Equal("session-1", r1.SessionId);
        Assert.Equal(1, factory.CreateCallCount);
        Assert.Equal(1, pool.ConnectionCount);
    }

    [Fact]
    public async Task GetSessionReceiverAsync_creates_separate_links_for_different_sessions()
    {
        await using var factory = new FakeFactory(DefaultSettings());
        await using var pool = new ServiceBusAmqpPool(factory);

        var ra = await pool.GetSessionReceiverAsync(ServiceBusAmqpEndpoint.Tls("ns.servicebus.windows.net"), "Root", "k", "fifo-q", "session-a")
            .WaitAsync(TimeSpan.FromSeconds(10));
        var rb = await pool.GetSessionReceiverAsync(ServiceBusAmqpEndpoint.Tls("ns.servicebus.windows.net"), "Root", "k", "fifo-q", "session-b")
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.NotSame(ra, rb);
        Assert.Equal("session-a", ra.SessionId);
        Assert.Equal("session-b", rb.SessionId);
        Assert.Equal(1, factory.CreateCallCount);
    }

    [Fact]
    public async Task GetSessionReceiverAsync_session_id_is_case_sensitive()
    {
        await using var factory = new FakeFactory(DefaultSettings());
        await using var pool = new ServiceBusAmqpPool(factory);

        var r1 = await pool.GetSessionReceiverAsync(ServiceBusAmqpEndpoint.Tls("ns.servicebus.windows.net"), "Root", "k", "fifo-q", "Session-1")
            .WaitAsync(TimeSpan.FromSeconds(10));
        var r2 = await pool.GetSessionReceiverAsync(ServiceBusAmqpEndpoint.Tls("ns.servicebus.windows.net"), "Root", "k", "fifo-q", "session-1")
            .WaitAsync(TimeSpan.FromSeconds(10));

        // SB treats session ids as opaque byte-equal tokens; differing
        // casing must produce distinct cached receivers.
        Assert.NotSame(r1, r2);
    }

    [Fact]
    public async Task GetSessionReceiverAsync_queue_name_is_case_insensitive()
    {
        await using var factory = new FakeFactory(DefaultSettings());
        await using var pool = new ServiceBusAmqpPool(factory);

        var r1 = await pool.GetSessionReceiverAsync(ServiceBusAmqpEndpoint.Tls("ns.servicebus.windows.net"), "Root", "k", "Fifo-Q", "session-1")
            .WaitAsync(TimeSpan.FromSeconds(10));
        var r2 = await pool.GetSessionReceiverAsync(ServiceBusAmqpEndpoint.Tls("ns.servicebus.windows.net"), "Root", "k", "fifo-q", "session-1")
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Same(r1, r2);
    }

    [Fact]
    public async Task InvalidateSessionReceiverAsync_drops_cached_link_only()
    {
        await using var factory = new FakeFactory(DefaultSettings());
        await using var pool = new ServiceBusAmqpPool(factory);

        var r1 = await pool.GetSessionReceiverAsync(ServiceBusAmqpEndpoint.Tls("ns.servicebus.windows.net"), "Root", "k", "fifo-q", "session-1")
            .WaitAsync(TimeSpan.FromSeconds(10));

        await pool.InvalidateSessionReceiverAsync(ServiceBusAmqpEndpoint.Tls("ns.servicebus.windows.net"), "Root", "fifo-q", "session-1");

        Assert.Equal(1, pool.ConnectionCount);

        var r2 = await pool.GetSessionReceiverAsync(ServiceBusAmqpEndpoint.Tls("ns.servicebus.windows.net"), "Root", "k", "fifo-q", "session-1")
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.NotSame(r1, r2);
        Assert.Equal(1, factory.CreateCallCount);
    }

    [Fact]
    public async Task AcquireBrokerAssignedSessionReceiverAsync_resolves_and_caches_by_assigned_session_id()
    {
        await using var factory = new FakeFactory(DefaultSettings());
        await using var pool = new ServiceBusAmqpPool(factory);

        // FakeFactory's broker simulator returns "broker-assigned-session"
        // when the client asks for sessionId: null (no AssignedSessionByLink
        // override is set). Acquire path resolves that and adopts the
        // receiver into the pool keyed by the resolved id.
        var acquisition = await pool.AcquireBrokerAssignedSessionReceiverAsync(
                ServiceBusAmqpEndpoint.Tls("ns.servicebus.windows.net"),
                "Root", "k", "fifo-q", TimeSpan.FromSeconds(5))
            .WaitAsync(TimeSpan.FromSeconds(10));
        var r1 = Assert.IsType<ServiceBusReceiver>(acquisition.Receiver);

        Assert.Equal("broker-assigned-session", r1.SessionId);

        // A subsequent GetSessionReceiverAsync for the resolved id sees
        // the cached slot (no second OpenSessionReceiverAsync round-trip).
        var r2 = await pool.GetSessionReceiverAsync(ServiceBusAmqpEndpoint.Tls("ns.servicebus.windows.net"), "Root", "k", "fifo-q", "broker-assigned-session")
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Same(r1, r2);
        Assert.Equal(1, factory.CreateCallCount);
    }

    [Fact]
    public async Task AcquireBrokerAssignedSessionReceiverAsync_repeated_calls_open_fresh_sessions()
    {
        // Two acquires with the simulator's default behaviour both
        // resolve to "broker-assigned-session" — the second call's
        // freshly-opened receiver races with the cached slot, so the
        // acquire path disposes the new one and returns the cached
        // receiver. End state: still one slot, same receiver instance.
        await using var factory = new FakeFactory(DefaultSettings());
        await using var pool = new ServiceBusAmqpPool(factory);

        var first = await pool.AcquireBrokerAssignedSessionReceiverAsync(
                ServiceBusAmqpEndpoint.Tls("ns.servicebus.windows.net"),
                "Root", "k", "fifo-q", TimeSpan.FromSeconds(5))
            .WaitAsync(TimeSpan.FromSeconds(10));
        var second = await pool.AcquireBrokerAssignedSessionReceiverAsync(
                ServiceBusAmqpEndpoint.Tls("ns.servicebus.windows.net"),
                "Root", "k", "fifo-q", TimeSpan.FromSeconds(5))
            .WaitAsync(TimeSpan.FromSeconds(10));
        var r1 = Assert.IsType<ServiceBusReceiver>(first.Receiver);
        var r2 = Assert.IsType<ServiceBusReceiver>(second.Receiver);

        Assert.Same(r1, r2);
        Assert.Equal("broker-assigned-session", r1.SessionId);
        Assert.Equal(1, factory.CreateCallCount);
    }

    [Fact]
    public async Task AcquireBrokerAssignedSessionReceiverAsync_returns_empty_when_attach_binds_no_session()
    {
        await using var factory = new FakeFactory(
            DefaultSettings(),
            echoSessionFilterOnAttach: false);
        await using var pool = new ServiceBusAmqpPool(factory);

        var acquisition = await pool.AcquireBrokerAssignedSessionReceiverAsync(
                ServiceBusAmqpEndpoint.Tls("ns.servicebus.windows.net"),
                "Root", "k", "fifo-q", TimeSpan.FromSeconds(5))
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Null(acquisition.Receiver);
        Assert.Equal(0, pool.SessionReceiverCount);
    }

    [Fact]
    public async Task AcquireBrokerAssignedSessionReceiverAsync_returns_empty_for_null_assignment()
    {
        await using var factory = new FakeFactory(
            DefaultSettings(),
            echoNullSessionFilterOnAttach: true);
        await using var pool = new ServiceBusAmqpPool(factory);

        var acquisition = await pool.AcquireBrokerAssignedSessionReceiverAsync(
                ServiceBusAmqpEndpoint.Tls("ns.servicebus.windows.net"),
                "Root", "k", "fifo-q", TimeSpan.FromSeconds(5))
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Null(acquisition.Receiver);
        Assert.Equal(0, pool.SessionReceiverCount);
    }

    [Fact]
    public async Task AcquireBrokerAssignedSessionReceiverAsync_rejects_blank_arguments()
    {
        await using var factory = new FakeFactory(DefaultSettings());
        await using var pool = new ServiceBusAmqpPool(factory);

        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            pool.AcquireBrokerAssignedSessionReceiverAsync(
                ServiceBusAmqpEndpoint.Tls("ns.servicebus.windows.net"),
                "Root", "k", queueName: null!, TimeSpan.FromSeconds(5)));
        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            pool.AcquireBrokerAssignedSessionReceiverAsync(
                ServiceBusAmqpEndpoint.Tls("ns.servicebus.windows.net"),
                "Root", "k", queueName: "", TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task GetSessionReceiverAsync_rejects_null_or_empty_session_id()
    {
        await using var factory = new FakeFactory(DefaultSettings());
        await using var pool = new ServiceBusAmqpPool(factory);

        // ThrowIfNullOrWhiteSpace dispatches: null → ArgumentNullException,
        // empty/whitespace → ArgumentException. Either flavour is fine —
        // both derive from ArgumentException.
        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            pool.GetSessionReceiverAsync(ServiceBusAmqpEndpoint.Tls("ns.servicebus.windows.net"), "Root", "k", "q", sessionId: null!));
        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            pool.GetSessionReceiverAsync(ServiceBusAmqpEndpoint.Tls("ns.servicebus.windows.net"), "Root", "k", "q", sessionId: ""));
    }

    [Fact]
    public async Task SweepIdleSessionReceivers_is_noop_when_no_idle_timeout_configured()
    {
        // Default ctor => no sweeper; sweeping is a no-op even for an
        // open, long-idle session receiver (pre-#262 behaviour).
        await using var factory = new FakeFactory(DefaultSettings());
        await using var pool = new ServiceBusAmqpPool(factory);

        await pool.GetSessionReceiverAsync(ServiceBusAmqpEndpoint.Tls("ns.servicebus.windows.net"), "Root", "k", "fifo-q", "session-1")
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(0, await pool.SweepIdleSessionReceiversAsync());
        Assert.Equal(1, pool.SessionReceiverCount);
    }

    [Fact]
    public async Task Session_receiver_pool_enforces_hard_cap_and_releases_capacity_on_invalidation()
    {
        await using var factory = new FakeFactory(DefaultSettings());
        await using var pool = new ServiceBusAmqpPool(
            factory,
            sessionReceiverIdleTimeout: null,
            maxSessionReceiversPerConnection: 2);
        var endpoint = ServiceBusAmqpEndpoint.Tls("ns.servicebus.windows.net");

        await pool.GetSessionReceiverAsync(endpoint, "Root", "k", "fifo-q", "session-1")
            .WaitAsync(TimeSpan.FromSeconds(10));
        await pool.GetSessionReceiverAsync(endpoint, "Root", "k", "fifo-q", "session-2")
            .WaitAsync(TimeSpan.FromSeconds(10));

        await Assert.ThrowsAsync<SessionReceiverLimitExceededException>(() =>
            pool.GetSessionReceiverAsync(endpoint, "Root", "k", "fifo-q", "session-3"));
        Assert.Equal(2, pool.SessionReceiverCount);

        await pool.InvalidateSessionReceiverAsync(endpoint, "Root", "fifo-q", "session-1");
        var replacement = await pool
            .GetSessionReceiverAsync(endpoint, "Root", "k", "fifo-q", "session-3")
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("session-3", replacement.SessionId);
        Assert.Equal(2, pool.SessionReceiverCount);
    }

    [Fact]
    public async Task Session_receiver_pool_reclaims_closed_slot_at_capacity_without_idle_timeout()
    {
        await using var factory = new FakeFactory(DefaultSettings());
        await using var pool = new ServiceBusAmqpPool(
            factory,
            sessionReceiverIdleTimeout: null,
            maxSessionReceiversPerConnection: 1);
        var endpoint = ServiceBusAmqpEndpoint.Tls("ns.servicebus.windows.net");

        var closed = await pool.GetSessionReceiverAsync(
                endpoint, "Root", "k", "fifo-q", "session-1")
            .WaitAsync(TimeSpan.FromSeconds(10));
        await closed.DisposeAsync();

        var replacement = await pool.GetSessionReceiverAsync(
                endpoint, "Root", "k", "fifo-q", "session-2")
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("session-2", replacement.SessionId);
        Assert.Equal(1, pool.SessionReceiverCount);
        Assert.Equal(1, factory.CreateCallCount);
    }

    [Fact]
    public async Task Idle_eviction_is_opportunistic_and_does_not_create_a_background_timer()
    {
        await using var factory = new FakeFactory(DefaultSettings());
        await using var pool = new ServiceBusAmqpPool(
            factory,
            sessionReceiverIdleTimeout: TimeSpan.FromMinutes(5),
            timeProvider: new TimerRejectingClock());

        Assert.Equal(0, pool.SessionReceiverCount);
    }

    [Fact]
    public async Task SweepIdleSessionReceivers_evicts_links_idle_beyond_timeout_and_keeps_connection_warm()
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 6, 10, 0, 0, 0, TimeSpan.Zero));
        await using var factory = new FakeFactory(DefaultSettings());
        await using var pool = new ServiceBusAmqpPool(
            factory, sessionReceiverIdleTimeout: TimeSpan.FromMinutes(5),
            sweepInterval: TimeSpan.FromMinutes(1), timeProvider: clock);

        await pool.GetSessionReceiverAsync(ServiceBusAmqpEndpoint.Tls("ns.servicebus.windows.net"), "Root", "k", "fifo-q", "session-1")
            .WaitAsync(TimeSpan.FromSeconds(10));
        await pool.GetSessionReceiverAsync(ServiceBusAmqpEndpoint.Tls("ns.servicebus.windows.net"), "Root", "k", "fifo-q", "session-2")
            .WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(2, pool.SessionReceiverCount);

        // No time elapsed -> nothing idle.
        Assert.Equal(0, await pool.SweepIdleSessionReceiversAsync());
        Assert.Equal(2, pool.SessionReceiverCount);

        // Past the idle window -> both links evicted, connection warm.
        clock.Advance(TimeSpan.FromMinutes(6));
        Assert.Equal(2, await pool.SweepIdleSessionReceiversAsync());
        Assert.Equal(0, pool.SessionReceiverCount);
        Assert.Equal(1, pool.ConnectionCount);
        Assert.Equal(1, factory.CreateCallCount);

        // The session can be re-acquired on the still-warm connection.
        var reopened = await pool.GetSessionReceiverAsync(ServiceBusAmqpEndpoint.Tls("ns.servicebus.windows.net"), "Root", "k", "fifo-q", "session-1")
            .WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("session-1", reopened.SessionId);
        Assert.Equal(1, factory.CreateCallCount);
    }

    [Fact]
    public async Task SweepIdleSessionReceivers_keeps_links_within_idle_window()
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 6, 10, 0, 0, 0, TimeSpan.Zero));
        await using var factory = new FakeFactory(DefaultSettings());
        await using var pool = new ServiceBusAmqpPool(
            factory, sessionReceiverIdleTimeout: TimeSpan.FromMinutes(5), timeProvider: clock);

        await pool.GetSessionReceiverAsync(ServiceBusAmqpEndpoint.Tls("ns.servicebus.windows.net"), "Root", "k", "fifo-q", "session-1")
            .WaitAsync(TimeSpan.FromSeconds(10));

        clock.Advance(TimeSpan.FromMinutes(3));
        Assert.Equal(0, await pool.SweepIdleSessionReceiversAsync());
        Assert.Equal(1, pool.SessionReceiverCount);
    }

    [Fact]
    public async Task Session_receiver_lease_blocks_idle_eviction_until_operation_completes()
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 6, 10, 0, 0, 0, TimeSpan.Zero));
        await using var factory = new FakeFactory(DefaultSettings());
        await using var pool = new ServiceBusAmqpPool(
            factory,
            sessionReceiverIdleTimeout: TimeSpan.FromMilliseconds(1),
            timeProvider: clock);
        var endpoint = ServiceBusAmqpEndpoint.Tls("ns.servicebus.windows.net");

        await pool.GetSessionReceiverAsync(endpoint, "Root", "k", "fifo-q", "session-1")
            .WaitAsync(TimeSpan.FromSeconds(10));
        using (var lease = Assert.IsType<ServiceBusAmqpPool.SessionReceiverLease>(
                   pool.TryAcquireExistingSessionReceiver(
                       endpoint, "Root", "fifo-q", "session-1")))
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            Assert.Equal(0, await pool.SweepIdleSessionReceiversAsync());
            Assert.False(lease.Receiver.IsClosed);
        }

        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, await pool.SweepIdleSessionReceiversAsync());
        Assert.Equal(0, pool.SessionReceiverCount);
    }

    [Fact]
    public async Task Session_receiver_invalidation_defers_disposal_until_active_lease_drains()
    {
        await using var factory = new FakeFactory(DefaultSettings());
        await using var pool = new ServiceBusAmqpPool(factory);
        var endpoint = ServiceBusAmqpEndpoint.Tls("ns.servicebus.windows.net");
        await pool.GetSessionReceiverAsync(endpoint, "Root", "k", "fifo-q", "session-1")
            .WaitAsync(TimeSpan.FromSeconds(10));
        var lease = Assert.IsType<ServiceBusAmqpPool.SessionReceiverLease>(
            pool.TryAcquireExistingSessionReceiver(endpoint, "Root", "fifo-q", "session-1"));

        var invalidation = pool.InvalidateSessionReceiverAsync(
            endpoint, "Root", "fifo-q", "session-1");
        Assert.False(invalidation.IsCompleted);
        Assert.False(lease.Receiver.IsClosed);

        lease.Dispose();
        await invalidation.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(lease.Receiver.IsClosed);
        Assert.Equal(0, pool.SessionReceiverCount);
    }

    [Fact]
    public async Task TryGetExistingSessionReceiver_resets_idle_clock()
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 6, 10, 0, 0, 0, TimeSpan.Zero));
        await using var factory = new FakeFactory(DefaultSettings());
        await using var pool = new ServiceBusAmqpPool(
            factory, sessionReceiverIdleTimeout: TimeSpan.FromMinutes(5), timeProvider: clock);

        await pool.GetSessionReceiverAsync(ServiceBusAmqpEndpoint.Tls("ns.servicebus.windows.net"), "Root", "k", "fifo-q", "session-1")
            .WaitAsync(TimeSpan.FromSeconds(10));

        // Approach the idle edge, then a settle-path access (TryGet) resets
        // the activity clock — an actively-drained session survives.
        clock.Advance(TimeSpan.FromMinutes(4));
        Assert.NotNull(pool.TryGetExistingSessionReceiver(ServiceBusAmqpEndpoint.Tls("ns.servicebus.windows.net"), "Root", "fifo-q", "session-1"));

        clock.Advance(TimeSpan.FromMinutes(3));
        Assert.Equal(0, await pool.SweepIdleSessionReceiversAsync());
        Assert.Equal(1, pool.SessionReceiverCount);

        // Now leave it idle past the window -> evicted.
        clock.Advance(TimeSpan.FromMinutes(6));
        Assert.Equal(1, await pool.SweepIdleSessionReceiversAsync());
        Assert.Equal(0, pool.SessionReceiverCount);
    }

    [Fact]
    public async Task GetSessionReceiver_racing_the_sweeper_never_orphans_the_warm_connection()
    {
        // #262 Fix A regression: when the idle-TTL sweeper disposes a
        // session-receiver slot concurrently with GetSessionReceiverAsync,
        // the resulting ObjectDisposedException from the child slot must NOT
        // bubble to the connection-level catch (which would evict the whole
        // warm ConnectionSlot and force a fresh dial). The connection is
        // never re-created, so CreateCallCount stays 1 throughout; a buggy
        // build that orphans the connection would dial again (>1).
        var clock = new ManualClock(new DateTimeOffset(2026, 6, 10, 0, 0, 0, TimeSpan.Zero));
        await using var factory = new FakeFactory(DefaultSettings());
        await using var pool = new ServiceBusAmqpPool(
            factory, sessionReceiverIdleTimeout: TimeSpan.FromMilliseconds(1),
            timeProvider: clock);

        var endpoint = ServiceBusAmqpEndpoint.Tls("ns.servicebus.windows.net");
        await pool.GetSessionReceiverAsync(endpoint, "Root", "k", "fifo-q", "session-1")
            .WaitAsync(TimeSpan.FromSeconds(10));

        const int Iterations = 500;
        var getter = Task.Run(async () =>
        {
            for (var i = 0; i < Iterations; i++)
            {
                var r = await pool.GetSessionReceiverAsync(endpoint, "Root", "k", "fifo-q", "session-1")
                    .WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal("session-1", r.SessionId);
            }
        });
        var sweeper = Task.Run(async () =>
        {
            for (var i = 0; i < Iterations; i++)
            {
                clock.Advance(TimeSpan.FromSeconds(1));
                await pool.SweepIdleSessionReceiversAsync();
            }
        });

        await Task.WhenAll(getter, sweeper).WaitAsync(TimeSpan.FromSeconds(30));

        // The connection was reused for every re-open; it was never orphaned
        // and re-dialed despite the racing slot disposals.
        Assert.Equal(1, factory.CreateCallCount);
        Assert.Equal(1, pool.ConnectionCount);
    }

    /// <summary>
    /// Deterministic <see cref="TimeProvider"/> for idle-TTL tests.
    /// </summary>
    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        // Thread-safe so the concurrency regression test
        // (GetSessionReceiver racing the sweeper) can advance the clock
        // from one task while the pool reads it from another.
        private long _nowTicks = now.UtcTicks;

        public override DateTimeOffset GetUtcNow()
            => new(Volatile.Read(ref _nowTicks), TimeSpan.Zero);

        public void Advance(TimeSpan delta) => Interlocked.Add(ref _nowTicks, delta.Ticks);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            => new NoopTimer();

        private sealed class NoopTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }

    }

    private sealed class TimerRejectingClock : TimeProvider
    {
        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period) =>
            throw new InvalidOperationException("The AMQP pool must not create background timers.");
    }

    /// <summary>
    /// Wraps another factory and gates each <c>CreateAsync</c> on an
    /// external task so tests can interleave it with pool disposal.
    /// </summary>
    private sealed class GatedFactory : IServiceBusAmqpConnectionFactory
    {
        private readonly FakeFactory _inner;
        private readonly Task _gate;
        private int _createEntered;

        public GatedFactory(FakeFactory inner, Task gate)
        {
            _inner = inner;
            _gate = gate;
        }

        public int CreateEntered => Volatile.Read(ref _createEntered);
        public int CreateCallCount => _inner.CreateCallCount;

        public async Task<ServiceBusAmqpConnection> CreateAsync(
            ServiceBusAmqpEndpoint endpoint,
            string sasKeyName,
            string sasKey,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _createEntered);
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            return await _inner
                .CreateAsync(endpoint, sasKeyName, sasKey, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Factory that spins up a fresh in-process pipe pair + broker
    /// simulator per CreateAsync call and returns a live connection.
    /// Tracks call count so tests can assert on cache behaviour, and
    /// owns the broker simulators so cleanup is deterministic.
    /// </summary>
    private sealed class FakeFactory : IServiceBusAmqpConnectionFactory, IAsyncDisposable
    {
        private readonly AmqpConnectionSettings _settings;
        private readonly bool _echoSessionFilterOnAttach;
        private readonly bool _echoNullSessionFilterOnAttach;
        private readonly List<IAsyncDisposable> _peers = new();
        private int _createCallCount;

        public FakeFactory(
            AmqpConnectionSettings settings,
            bool echoSessionFilterOnAttach = true,
            bool echoNullSessionFilterOnAttach = false)
        {
            _settings = settings;
            _echoSessionFilterOnAttach = echoSessionFilterOnAttach;
            _echoNullSessionFilterOnAttach = echoNullSessionFilterOnAttach;
        }

        public int CreateCallCount => Volatile.Read(ref _createCallCount);

        public async Task<ServiceBusAmqpConnection> CreateAsync(
            ServiceBusAmqpEndpoint endpoint,
            string sasKeyName,
            string sasKey,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _createCallCount);
            var (client, server) = PipePairTransport.CreatePair();
            lock (_peers) _peers.Add(server);
            var broker = new ServiceBusBrokerSimulator(server)
            {
                EchoSessionFilterOnAttach = _echoSessionFilterOnAttach,
                EchoNullSessionFilterOnAttach = _echoNullSessionFilterOnAttach,
            };
            broker.Start();
            return await ServiceBusAmqpConnection
                .OpenAsync(client, new FakeTokenProvider(), _settings, cancellationToken)
                .ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            IAsyncDisposable[] snapshot;
            lock (_peers) { snapshot = _peers.ToArray(); _peers.Clear(); }
            foreach (var p in snapshot)
            {
                try { await p.DisposeAsync().ConfigureAwait(false); } catch { }
            }
        }
    }

    /// <summary>
    /// Like <see cref="FakeFactory"/> but counts <em>disposals</em> of
    /// the returned connections (via a tracking wrapper around the
    /// client-side transport — ServiceBusAmqpConnection always disposes
    /// the transport during its own DisposeAsync). Used by the
    /// dispose-race tests to assert no opened connection leaks.
    /// </summary>
    private sealed class TrackingFactory : IServiceBusAmqpConnectionFactory, IAsyncDisposable
    {
        private readonly AmqpConnectionSettings _settings;
        private readonly List<IAsyncDisposable> _peers = new();
        private int _createCallCount;
        private int _disposeCallCount;

        public TrackingFactory(AmqpConnectionSettings settings) => _settings = settings;

        public int CreateCallCount => Volatile.Read(ref _createCallCount);
        public int DisposeCallCount => Volatile.Read(ref _disposeCallCount);

        public async Task<ServiceBusAmqpConnection> CreateAsync(
            ServiceBusAmqpEndpoint endpoint,
            string sasKeyName,
            string sasKey,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _createCallCount);
            var (client, server) = PipePairTransport.CreatePair();
            lock (_peers) _peers.Add(server);
            var broker = new ServiceBusBrokerSimulator(server);
            broker.Start();
            var tracked = new CountingTransport(client, () => Interlocked.Increment(ref _disposeCallCount));
            return await ServiceBusAmqpConnection
                .OpenAsync(tracked, new FakeTokenProvider(), _settings, cancellationToken)
                .ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            IAsyncDisposable[] snapshot;
            lock (_peers) { snapshot = _peers.ToArray(); _peers.Clear(); }
            foreach (var p in snapshot)
            {
                try { await p.DisposeAsync().ConfigureAwait(false); } catch { }
            }
        }

        private sealed class CountingTransport : Aws2Azure.Amqp.Transport.IAmqpTransport
        {
            private readonly Aws2Azure.Amqp.Transport.IAmqpTransport _inner;
            private readonly Action _onDispose;
            private int _disposed;

            public CountingTransport(Aws2Azure.Amqp.Transport.IAmqpTransport inner, Action onDispose)
            { _inner = inner; _onDispose = onDispose; }

            public System.IO.Pipelines.PipeReader Input => _inner.Input;
            public System.IO.Pipelines.PipeWriter Output => _inner.Output;

            public async ValueTask DisposeAsync()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
                _onDispose();
                await _inner.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
