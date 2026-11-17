using System.Net.Sockets;
using Xunit;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Transport;
using Dream.Server;

namespace Dream.Tests.BugHunt;

// BH-SRV-1: a peer that resets the TCP handshake before the accept completes fails one accept.
// The accept loop used to let that exception out, which ended the loop for good: the server went
// on ticking but never accepted another player, and the exception surfaced at shutdown. A
// per-connection failure must close only that connection, as the connection loops already do.
public sealed class SrvAcceptLoopTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task ATransientAcceptError_IsLoggedAndTheLoopKeepsAccepting()
    {
        using DedicatedServerHost host = DedicatedServerHost.CreateRuntimeAttached(persistence: null);
        ScriptedAcceptTransport transport = new(new SocketException((int)SocketError.ConnectionReset), failures: 2);
        await using DedicatedServerService service = new(
            host,
            transport,
            new TransportJsonCodec(),
            new LengthPrefixedMessageFramer());

        using CancellationTokenSource cancellation = new();
        Task run = service.RunAsync(cancellation.Token);

        // The loop survived both failures and is waiting on the next accept.
        Assert.True(await transport.ReachedAttemptAsync(3, Deadline), "the accept loop stopped after a transient error");

        await cancellation.CancelAsync();
        await run.WaitAsync(Deadline);
    }

    [Fact]
    public async Task AnErrorThatSaysTheListenerIsGone_StopsTheLoop_WithoutSpinning()
    {
        using DedicatedServerHost host = DedicatedServerHost.CreateRuntimeAttached(persistence: null);
        ScriptedAcceptTransport transport = new(new SocketException((int)SocketError.NotSocket), failures: int.MaxValue);
        await using DedicatedServerService service = new(
            host,
            transport,
            new TransportJsonCodec(),
            new LengthPrefixedMessageFramer());

        using CancellationTokenSource cancellation = new();
        Task run = service.RunAsync(cancellation.Token);

        // A dead listener is not retried: one attempt, then the error is reported.
        await Task.Delay(TimeSpan.FromMilliseconds(500));
        Assert.Equal(1, transport.Attempts);

        await cancellation.CancelAsync();
        await Assert.ThrowsAsync<SocketException>(() => run.WaitAsync(Deadline));
    }

    // Fails the first `failures` accepts with the given error, then waits for cancellation.
    private sealed class ScriptedAcceptTransport(SocketException error, int failures) : IServerTransport
    {
        private readonly SemaphoreSlim _attempted = new(0, int.MaxValue);
        private int _attempts;

        public int Attempts => Volatile.Read(ref _attempts);

        public int Port => 0;

        public void Start()
        {
        }

        public async ValueTask<ITransportConnection> AcceptClientAsync(CancellationToken cancellationToken = default)
        {
            int attempt = Interlocked.Increment(ref _attempts);
            _attempted.Release();
            if (attempt <= failures)
            {
                throw error;
            }

            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }

        public async Task<bool> ReachedAttemptAsync(int attempt, TimeSpan timeout)
        {
            using CancellationTokenSource deadline = new(timeout);
            try
            {
                while (Attempts < attempt)
                {
                    await _attempted.WaitAsync(deadline.Token);
                }
            }
            catch (OperationCanceledException)
            {
                return false;
            }

            return true;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
