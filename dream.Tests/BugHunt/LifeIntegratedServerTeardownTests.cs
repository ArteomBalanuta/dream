using System.Net;
using Dream.Client.Startup;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Transport;
using Dream.Server;
using Dream.Server.Hosting;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Xunit;

namespace Dream.Tests.BugHunt;

// BH-LIFE-1 (Critical). IntegratedLocalServerHandle.DisposeAsync awaited the server's run task
// first and tore the server down afterwards, in a plain statement sequence. Any exception out of a
// tick faults that task -- DedicatedServerHost.AdvanceTime and DedicatedServerService
// .AdvanceOnceAsync have no catch, and RunAsync's only catch is OperationCanceledException -- and
// the await's only catch was OperationCanceledException too, so the fault rethrew and skipped the
// service disposal, the shutdown source and, worst of all, host.Dispose(): the queued persistence
// writes, the final full checkpoint, the world lease and the listening socket were all abandoned
// while the client carried on to the title screen.
//
// The teardown now lives in a finally, so it runs on all three outcomes, exactly once.
public sealed class LifeIntegratedServerTeardownTests
{
    public static TheoryData<string> RunTaskOutcomes => new() { "completed", "cancelled", "faulted" };

    [Theory]
    [MemberData(nameof(RunTaskOutcomes))]
    public async Task EveryRunTaskOutcome_TearsTheServerDownExactlyOnce(string outcome)
    {
        CountingAuthority authority = new();
        CountingTransport transport = new();
        using DedicatedServerHost host = new(authority, EmptyRouter());
        DedicatedServerService service = new(host, transport, new TransportJsonCodec(), new LengthPrefixedMessageFramer());
        CancellationTokenSource shutdown = new();

        IntegratedLocalServerHandle handle = CreateHandle(host, service, shutdown, RunTask(outcome));

        await handle.DisposeAsync().AsTask().WaitAsync(IntegratedLocalServerHandle.RunTaskShutdownTimeout + TimeSpan.FromSeconds(20));

        // The host went down: it is the only thing that drains the write queue, writes the final
        // checkpoint and releases the world lease. Exactly once, not zero times.
        Assert.Equal(1, authority.DisposeCount);
        // The service went down too, so the listening socket is released. The transport's disposal
        // is not guarded by a once-flag, so a teardown that ran twice would show 2 here.
        Assert.Equal(1, transport.DisposeCount);
        // And the shutdown source was disposed.
        Assert.Throws<ObjectDisposedException>(() => _ = shutdown.Token);
    }

    // A faulted run task must not surface from the stop: PlaySessionEndTransition's callers only
    // ever logged it, and rethrowing it here is what skipped the teardown.
    [Fact]
    public async Task AFaultedRunTask_DoesNotSurfaceFromTheStop()
    {
        CountingAuthority authority = new();
        CountingTransport transport = new();
        using DedicatedServerHost host = new(authority, EmptyRouter());
        DedicatedServerService service = new(host, transport, new TransportJsonCodec(), new LengthPrefixedMessageFramer());
        CancellationTokenSource shutdown = new();

        IntegratedLocalServerHandle handle = CreateHandle(host, service, shutdown, RunTask("faulted"));

        // No throw, and the teardown happened.
        await handle.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(1, authority.DisposeCount);
        Assert.Equal(1, transport.DisposeCount);
    }

    // A run task that never returns at all -- a wedged tick rather than a faulted one -- is
    // abandoned at RunTaskShutdownTimeout and the world is still saved. Teardown must not hang:
    // this is on the thread PlaySessionEndTransition stops the server from.
    [Fact]
    public async Task AWedgedRunTask_IsAbandonedAtTheBound_AndTheWorldIsStillSaved()
    {
        CountingAuthority authority = new();
        CountingTransport transport = new();
        using DedicatedServerHost host = new(authority, EmptyRouter());
        DedicatedServerService service = new(host, transport, new TransportJsonCodec(), new LengthPrefixedMessageFramer());
        CancellationTokenSource shutdown = new();
        TaskCompletionSource wedged = new(TaskCreationOptions.RunContinuationsAsynchronously);

        IntegratedLocalServerHandle handle = CreateHandle(host, service, shutdown, wedged.Task);

        long start = Environment.TickCount64;
        await handle.DisposeAsync().AsTask().WaitAsync(IntegratedLocalServerHandle.RunTaskShutdownTimeout + TimeSpan.FromSeconds(30));
        TimeSpan waited = TimeSpan.FromMilliseconds(Environment.TickCount64 - start);

        Assert.True(
            waited < IntegratedLocalServerHandle.RunTaskShutdownTimeout + TimeSpan.FromSeconds(15),
            $"the stop took {waited.TotalSeconds:0.##} s against a bound of {IntegratedLocalServerHandle.RunTaskShutdownTimeout.TotalSeconds:0} s");
        Assert.Equal(1, authority.DisposeCount);
        Assert.Equal(1, transport.DisposeCount);
        wedged.TrySetResult();
    }

    // A handle whose start failed carries no host, service or shutdown source at all; its disposal
    // must still be a no-op rather than a null dereference inside the new finally.
    [Fact]
    public async Task AHandleThatNeverStarted_DisposesNothing()
    {
        IntegratedLocalServerHandle handle = new(
            host: null,
            service: null,
            shutdown: null,
            runTask: null,
            isReady: false,
            boundPort: 0,
            errorMessage: "port in use",
            listenOptions: new LocalServerListenOptions(IPAddress.Loopback, 0, false),
            connectionOptions: IntegratedLocalServerHandle.DefaultConnectionOptions);

        await handle.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.False(handle.IsReady);
    }

    private static Task RunTask(string outcome)
    {
        return outcome switch
        {
            "completed" => Task.CompletedTask,
            "cancelled" => Task.FromCanceled(new CancellationToken(canceled: true)),
            // What an exception out of a tick leaves behind.
            "faulted" => Task.FromException(new InvalidOperationException("tick faulted")),
            _ => throw new ArgumentOutOfRangeException(nameof(outcome))
        };
    }

    private static IntegratedLocalServerHandle CreateHandle(
        DedicatedServerHost host,
        DedicatedServerService service,
        CancellationTokenSource shutdown,
        Task runTask)
    {
        return new IntegratedLocalServerHandle(
            host,
            service,
            shutdown,
            runTask,
            isReady: true,
            boundPort: 1,
            errorMessage: null,
            listenOptions: new LocalServerListenOptions(IPAddress.Loopback, 0, false),
            connectionOptions: IntegratedLocalServerHandle.DefaultConnectionOptions);
    }

    private static ProtocolRouter EmptyRouter() =>
        new(Array.Empty<IServerMessageHandler>(), Array.Empty<IClientMessageHandler>());

    // Stands in for the runtime host DedicatedServerHost owns. Nothing here is called: the test
    // only needs its disposal counted, which is what host.Dispose() does to its authority (and the
    // real one is what drains the write queue and writes the final checkpoint).
    private sealed class CountingAuthority : IAuthoritativeSessionHost, IDisposable
    {
        private int _disposeCount;

        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public JoinAcceptedMessage JoinSession(string requestedSessionId, string displayName) =>
            throw new NotSupportedException();

        public AuthoritativeActionResult ProcessAction(PlayerActionMessage message) =>
            throw new NotSupportedException();

        public ServerSnapshotEnvelope ExportSnapshot(bool includeWorldSnapshot = false) =>
            throw new NotSupportedException();

        public void Dispose() => Interlocked.Increment(ref _disposeCount);
    }

    // The listening socket. Its disposal carries no once-flag, in the fake as in TcpServerTransport,
    // so a teardown that ran twice is visible as a count of 2.
    private sealed class CountingTransport : IServerTransport
    {
        private int _disposeCount;

        public int Port => 1;

        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public void Start()
        {
        }

        public ValueTask<ITransportConnection> AcceptClientAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCount);
            return ValueTask.CompletedTask;
        }
    }
}
