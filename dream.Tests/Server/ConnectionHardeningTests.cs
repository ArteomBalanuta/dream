using System.Threading.Channels;
using Xunit;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Transport;
using Dream.Server;
using Dream.Simulation.Runtime;
using static Dream.Tests.Server.ServerConnectionTestFixtures;

namespace Dream.Tests.Server;

// M0-C (B6, B7, B8): repeated joins are rate limited, the connection count is capped, and a
// taken-over client is told why before its socket closes.
public sealed class ConnectionHardeningTests
{
    [Fact]
    public void AJoinedConnection_RepeatingItsJoin_IsAnsweredOncePerInterval()
    {
        ManualTimeProvider clock = new();
        DedicatedServerHost host = DedicatedServerHost.CreateRuntimeAttached(deferJoinsToTick: false);
        host.TimeProvider = clock;
        try
        {
            int accepted = 0;
            for (int i = 0; i < 1000; i++)
            {
                IReadOnlyList<IServerToClientMessage> replies =
                    host.HandleClientMessage("c1", new ClientJoinRequestMessage("session-a", "Player A"));
                accepted += replies.Count(reply => reply is JoinAcceptedMessage);
            }

            // The first join, then one repeat; the other 998 are dropped.
            Assert.True(accepted <= 2, $"{accepted} joins were answered");
            clock.Advance(DedicatedServerHost.RepeatJoinInterval + TimeSpan.FromSeconds(1));
            IReadOnlyList<IServerToClientMessage> later =
                host.HandleClientMessage("c1", new ClientJoinRequestMessage("session-a", "Player A"));
            Assert.Contains(later, reply => reply is JoinAcceptedMessage);
        }
        finally
        {
            host.Dispose();
        }
    }

    [Fact]
    public async Task ConnectionOverTheCap_IsClosedRightAfterAccept()
    {
        DedicatedServerHost host = DedicatedServerHost.CreateRuntimeAttached(deferJoinsToTick: true);
        QueuedServerTransport listener = new();
        ServerConnectionOptions options = new() { MaxConnections = 1 };
        await using DedicatedServerService service = new(host, listener, new TransportJsonCodec(), new LengthPrefixedMessageFramer(), options);
        GatedTransport first = new("first", recordFrames: false);
        GatedTransport second = new("second", recordFrames: false);
        listener.Offer(first);
        listener.Offer(second);
        using CancellationTokenSource cancel = new();
        try
        {
            await service.AcceptNextClientAsync();
            Task<ServerConnection> refused = service.AcceptNextClientAsync(cancel.Token).AsTask();
            DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (!second.IsDisposed)
            {
                Assert.True(DateTime.UtcNow < deadline, "the connection over the cap was not closed");
                await Task.Delay(5);
            }

            Assert.False(first.IsDisposed);
            Assert.Equal(1, service.ConnectionCount);
            Assert.False(refused.IsCompleted);
        }
        finally
        {
            cancel.Cancel();
            host.Dispose();
        }
    }

    [Fact]
    public async Task TakenOverConnection_IsToldWhy_BeforeItsTransportCloses()
    {
        ManualTimeProvider clock = new();
        ServerConnectionOptions options = new() { TimeProvider = clock };
        DedicatedServerHost host = DedicatedServerHost.CreateRuntimeAttached(deferJoinsToTick: true);
        QueuedServerTransport listener = new();
        await using DedicatedServerService service = new(host, listener, new TransportJsonCodec(), new LengthPrefixedMessageFramer(), options);
        GatedTransport oldTransport = new("connection-old");
        listener.Offer(oldTransport);
        listener.Offer(new GatedTransport("connection-new", recordFrames: false));
        try
        {
            await service.AcceptNextClientAsync();
            await service.AcceptNextClientAsync();
            host.HandleClientMessage("connection-old", new ClientJoinRequestMessage("session-a", "Player A"));
            await AdvanceUntilAsync(service, () => host.IsConnectionJoined("connection-old"));

            clock.Advance(DedicatedServerHost.TakeoverSilence + TimeSpan.FromSeconds(0.5));
            host.HandleClientMessage("connection-new", new ClientJoinRequestMessage("session-a", "Player A"));
            await AdvanceUntilAsync(service, () => oldTransport.IsDisposed);

            TransportJsonCodec codec = new();
            LengthPrefixedMessageFramer framer = new();
            List<IProtocolMessage> received = [];
            foreach (byte[] frame in oldTransport.Frames)
            {
                Assert.True(framer.TryDeframe(frame, out byte[]? body, out _));
                received.Add(codec.Decode(body!).Message);
            }

            JoinRejectedMessage rejected = Assert.Single(received.OfType<JoinRejectedMessage>());
            Assert.Equal(DedicatedServerHost.OpenedElsewhereRejectionReason, rejected.Reason);
        }
        finally
        {
            host.Dispose();
        }
    }

    [Fact]
    public async Task ConnectionOverTheCap_GetsOneServerFullRejection_BeforeItCloses()
    {
        DedicatedServerHost host = DedicatedServerHost.CreateRuntimeAttached(deferJoinsToTick: true);
        QueuedServerTransport listener = new();
        ServerConnectionOptions options = new() { MaxConnections = 1 };
        await using DedicatedServerService service = new(host, listener, new TransportJsonCodec(), new LengthPrefixedMessageFramer(), options);
        GatedTransport second = new("second");
        listener.Offer(new GatedTransport("first", recordFrames: false));
        listener.Offer(second);
        using CancellationTokenSource cancel = new();
        try
        {
            await service.AcceptNextClientAsync();
            _ = service.AcceptNextClientAsync(cancel.Token).AsTask();
            DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (!second.IsDisposed)
            {
                Assert.True(DateTime.UtcNow < deadline, "the connection over the cap was not closed");
                await Task.Delay(5);
            }

            JoinRejectedMessage rejected = Assert.Single(Decode(second.Frames).OfType<JoinRejectedMessage>());
            Assert.Equal("Server is full.", rejected.Reason);
        }
        finally
        {
            cancel.Cancel();
            host.Dispose();
        }
    }

    [Fact]
    public async Task UnjoinedConnection_IsClosedAtTheJoinDeadline_AndAJoinedOneIsNot()
    {
        ManualTimeProvider clock = new();
        ServerConnectionOptions options = new() { TimeProvider = clock, IdleTimeout = TimeSpan.Zero };
        Assert.Equal(TimeSpan.FromSeconds(15), options.JoinDeadline);
        DedicatedServerHost host = DedicatedServerHost.CreateRuntimeAttached(deferJoinsToTick: true);
        QueuedServerTransport listener = new();
        await using DedicatedServerService service = new(host, listener, new TransportJsonCodec(), new LengthPrefixedMessageFramer(), options);
        GatedTransport joined = new("joined", recordFrames: false);
        GatedTransport silent = new("silent", recordFrames: false);
        listener.Offer(joined);
        listener.Offer(silent);
        try
        {
            await service.AcceptNextClientAsync();
            await service.AcceptNextClientAsync();
            host.HandleClientMessage("joined", new ClientJoinRequestMessage("session-a", "Player A"));
            await AdvanceUntilAsync(service, () => host.IsConnectionJoined("joined"));

            clock.Advance(options.JoinDeadline - TimeSpan.FromSeconds(1));
            await service.AdvanceOnceAsync(SimulationClock.FixedDeltaSeconds);
            Assert.False(silent.IsDisposed);

            clock.Advance(TimeSpan.FromSeconds(2));
            await AdvanceUntilAsync(service, () => silent.IsDisposed);
            Assert.False(joined.IsDisposed);
            Assert.Equal(1, service.ConnectionCount);
        }
        finally
        {
            host.Dispose();
        }
    }

    [Fact]
    public async Task TakeoverClose_DoesNotBlockTheTick_WhenTheOldPeerStopsReading()
    {
        ManualTimeProvider clock = new();
        ServerConnectionOptions options = new() { TimeProvider = clock };
        DedicatedServerHost host = DedicatedServerHost.CreateRuntimeAttached(deferJoinsToTick: true);
        QueuedServerTransport listener = new();
        await using DedicatedServerService service = new(host, listener, new TransportJsonCodec(), new LengthPrefixedMessageFramer(), options);
        GatedTransport oldTransport = new("connection-old");
        listener.Offer(oldTransport);
        listener.Offer(new GatedTransport("connection-new", recordFrames: false));
        try
        {
            await service.AcceptNextClientAsync();
            await service.AcceptNextClientAsync();
            host.HandleClientMessage("connection-old", new ClientJoinRequestMessage("session-a", "Player A"));
            await AdvanceUntilAsync(service, () => host.IsConnectionJoined("connection-old"));

            oldTransport.Close();
            clock.Advance(DedicatedServerHost.TakeoverSilence + TimeSpan.FromSeconds(0.5));
            host.HandleClientMessage("connection-new", new ClientJoinRequestMessage("session-a", "Player A"));
            await AdvanceUntilAsync(service, () => service.ConnectionCount == 1);

            // Removed on the tick, but its bounded flush is still running in the background.
            Assert.False(oldTransport.IsDisposed);
            DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (!oldTransport.IsDisposed)
            {
                Assert.True(DateTime.UtcNow < deadline, "the taken-over transport was never closed");
                await Task.Delay(5);
            }
        }
        finally
        {
            host.Dispose();
        }
    }

    private static async Task AdvanceUntilAsync(DedicatedServerService service, Func<bool> condition)
    {
        for (int i = 0; i < 200 && !condition(); i++)
        {
            await service.AdvanceOnceAsync(SimulationClock.FixedDeltaSeconds);
            await Task.Delay(2);
        }

        Assert.True(condition());
    }

    private sealed class QueuedServerTransport : IServerTransport
    {
        private readonly Channel<ITransportConnection> _pending = Channel.CreateUnbounded<ITransportConnection>();

        public int Port => 0;

        public void Offer(ITransportConnection connection) => _pending.Writer.TryWrite(connection);

        public void Start()
        {
        }

        public ValueTask<ITransportConnection> AcceptClientAsync(CancellationToken cancellationToken = default)
        {
            return _pending.Reader.ReadAsync(cancellationToken);
        }

        public ValueTask DisposeAsync()
        {
            _pending.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }
}
