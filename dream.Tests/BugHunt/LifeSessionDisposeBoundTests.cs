using Dream.Client.Networking;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Multiplayer.Transport;
using Xunit;

namespace Dream.Tests.BugHunt;

// BH-LIFE-2. The finding as filed -- that a pending NetworkStream.ReadAsync ignores its
// cancellation token, so DisposeAsync's `await _receiveLoopTask` never returns -- does not hold on
// .NET 10: a probe against a real loopback peer that neither sends nor closes showed the pending
// receive ending with OperationCanceledException within milliseconds of the cancel. The second
// test here records that.
//
// What remains, and what the first test pins, is that PlayScene.Lifetime.DisposeRemoteClient joins
// DisposeAsync synchronously on the render thread, and this process has no other thread that could
// close the window if that join did not return. So every wait in DisposeAsync is now bounded by
// RemoteClientSession.LoopShutdownTimeout, and a transport whose receive does ignore its token --
// what the finding assumed, and what some future transport could do -- no longer freezes the game
// on its last frame.
public sealed class LifeSessionDisposeBoundTests
{
    private const string SessionId = "session-life2";

    [Fact]
    public async Task DisposeAsync_ReturnsWithinItsBound_WhenAReceiveIgnoresCancellation()
    {
        StuckTransport transport = new(honourCancellation: false);
        RemoteClientSession session = CreateSession(transport);
        session.StartBackgroundReceive();
        await transport.ReceiveStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        long start = Environment.TickCount64;
        // Without the bound this await never completes and the test fails here with a timeout --
        // which is exactly the freeze being guarded against.
        await session.DisposeAsync().AsTask().WaitAsync(RemoteClientSession.LoopShutdownTimeout + TimeSpan.FromSeconds(20));
        TimeSpan waited = TimeSpan.FromMilliseconds(Environment.TickCount64 - start);

        Assert.True(
            waited < RemoteClientSession.LoopShutdownTimeout + TimeSpan.FromSeconds(10),
            $"DisposeAsync took {waited.TotalSeconds:0.##} s against a bound of {RemoteClientSession.LoopShutdownTimeout.TotalSeconds:0} s");
        // The teardown still finished its job: the transport was closed exactly once.
        Assert.Equal(1, transport.DisposeCount);
    }

    // The real transports -- and the fake ones the existing watchdog tests use -- do honour the
    // token, so the normal teardown is nowhere near the bound.
    [Fact]
    public async Task DisposeAsync_ReturnsAtOnce_WhenTheReceiveHonoursCancellation()
    {
        StuckTransport transport = new(honourCancellation: true);
        RemoteClientSession session = CreateSession(transport);
        session.StartBackgroundReceive();
        await transport.ReceiveStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        long start = Environment.TickCount64;
        await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30));
        TimeSpan waited = TimeSpan.FromMilliseconds(Environment.TickCount64 - start);

        Assert.True(
            waited < RemoteClientSession.LoopShutdownTimeout,
            $"a cancellable receive should end at once, not in {waited.TotalSeconds:0.##} s");
        Assert.Equal(1, transport.DisposeCount);
    }

    // A second DisposeAsync does nothing: the teardown runs exactly once.
    [Fact]
    public async Task DisposeAsync_RunsTheTeardownExactlyOnce()
    {
        StuckTransport transport = new(honourCancellation: true);
        RemoteClientSession session = CreateSession(transport);
        session.StartBackgroundReceive();
        await transport.ReceiveStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30));
        await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(1, transport.DisposeCount);
    }

    private static RemoteClientSession CreateSession(ITransportConnection transport)
    {
        return new RemoteClientSession(
            transport,
            new TransportJsonCodec(),
            new LengthPrefixedMessageFramer(),
            new RemoteClientOptions("127.0.0.1", 5099, SessionId, "player-l", "player-l"));
    }

    // A peer that accepts and then says nothing. honourCancellation: false models the half-open
    // socket the finding described -- a receive the token cannot abort; true models what .NET
    // actually does. Either way the transport's own dispose ends the receive, as a closed socket
    // does.
    private sealed class StuckTransport : ITransportConnection
    {
        private readonly TaskCompletionSource<int> _receive = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly bool _honourCancellation;
        private int _disposeCount;

        public StuckTransport(bool honourCancellation)
        {
            _honourCancellation = honourCancellation;
        }

        public string ConnectionId => "bh-life2";

        public TaskCompletionSource ReceiveStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public async ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReceiveStarted.TrySetResult();
            if (!_honourCancellation)
            {
                // Nothing but the transport's own close ends this.
                return await _receive.Task;
            }

            using CancellationTokenRegistration registration = cancellationToken.Register(
                static state => ((TaskCompletionSource<int>)state!).TrySetCanceled(), _receive);
            return await _receive.Task;
        }

        public ValueTask SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
        {
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCount);
            // A closed socket ends a pending receive with a zero-length read.
            _receive.TrySetResult(0);
            return ValueTask.CompletedTask;
        }
    }
}
