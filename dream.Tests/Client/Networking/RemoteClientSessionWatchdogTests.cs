using System.Threading.Channels;
using Xunit;
using Dream.Client.Networking;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Multiplayer.Transport;
using Dream.Simulation.Players;
using Dream.Simulation.World;

namespace Dream.Tests.Client.Networking;

// NETR-2: the server-silence watchdog. A joined session that receives no frame for
// RemoteClientSession.ServerSilenceTimeout (8 s) ends with ServerSilenceDisconnectReason. Time is
// a ManualTimerTimeProvider, so the 8 s run without real waits; the keepalive runs on real time.
public class RemoteClientSessionWatchdogTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private const string SessionId = "session-w";

    [Fact]
    public void Timeout_IsEightSeconds_AndTheReasonSaysSo()
    {
        Assert.Equal(TimeSpan.FromSeconds(8), RemoteClientSession.ServerSilenceTimeout);
        Assert.Equal("Connection lost: no data from the server for 8 s", RemoteClientSession.ServerSilenceDisconnectReason);
    }

    [Fact]
    public async Task Fires_AfterEightSecondsOfSilence_AfterTheJoin()
    {
        ManualTimerTimeProvider clock = new();
        SilentServerConnection server = new();
        await using RemoteClientSession session = CreateSession(server, clock);
        await JoinAsync(session, server, clock);

        clock.Advance(RemoteClientSession.ServerSilenceTimeout - TimeSpan.FromMilliseconds(1));
        await Task.Delay(50);
        Assert.True(session.IsConnected);
        Assert.Null(session.DisconnectReason);

        clock.Advance(TimeSpan.FromMilliseconds(1));
        await WaitUntilAsync(() => !session.IsConnected, "the watchdog to end the session");
        Assert.Equal(RemoteClientSession.ServerSilenceDisconnectReason, session.DisconnectReason);
        Assert.True(session.IsJoined);
    }

    [Fact]
    public async Task DoesNotFire_WhileSnapshotsFlow()
    {
        ManualTimerTimeProvider clock = new();
        SilentServerConnection server = new();
        await using RemoteClientSession session = CreateSession(server, clock);
        await JoinAsync(session, server, clock);

        // 40 s of fake time, one snapshot per second: never 8 s without data.
        for (int second = 1; second <= 40; second++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            long before = session.InboundFrameCount;
            server.Queue(Snapshot((ulong)second));
            await WaitUntilAsync(() => session.InboundFrameCount > before, $"snapshot {second}");
            Assert.True(session.IsConnected, session.DisconnectReason);
        }

        await Task.Delay(50);
        Assert.True(session.IsConnected, session.DisconnectReason);
        Assert.Null(session.DisconnectReason);

        // Then the snapshots stop: 8 s after the last one, the session ends.
        // The watchdog is parked on its next wait (due at most 8 s after the last snapshot).
        await WaitUntilAsync(() => clock.TimerCount == 1, "the watchdog's next wait");
        clock.Advance(RemoteClientSession.ServerSilenceTimeout);
        await WaitUntilAsync(() => !session.IsConnected, "the watchdog to end the session");
        Assert.Equal(RemoteClientSession.ServerSilenceDisconnectReason, session.DisconnectReason);
    }

    // Empty frames from the server are data too: any complete frame resets the watchdog.
    [Fact]
    public async Task DoesNotFire_WhileTheServerSendsOnlyEmptyFrames()
    {
        ManualTimerTimeProvider clock = new();
        SilentServerConnection server = new();
        await using RemoteClientSession session = CreateSession(server, clock);
        await JoinAsync(session, server, clock);

        for (int second = 1; second <= 20; second++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            long before = session.InboundFrameCount;
            server.QueueEmptyFrame();
            await WaitUntilAsync(() => session.InboundFrameCount > before, $"empty frame {second}");
        }

        await Task.Delay(50);
        Assert.True(session.IsConnected, session.DisconnectReason);
    }

    // The client's own keepalives prove nothing about the server, so they must not reset it.
    [Fact]
    public async Task OutboundKeepalives_DoNotResetIt()
    {
        ManualTimerTimeProvider clock = new();
        SilentServerConnection server = new();
        await using RemoteClientSession session = CreateSession(server, clock);
        await JoinAsync(session, server, clock);

        // The keepalive runs on real time: wait until at least two went out after the join.
        await WaitUntilAsync(() => server.KeepaliveCount >= 2, "two keepalives");
        Assert.True(session.SentKeepaliveCount >= 2);

        // The watchdog's deadline is still 8 s after the join.
        Assert.True(clock.HasTimerDueIn(RemoteClientSession.ServerSilenceTimeout), "the watchdog deadline moved");
        clock.Advance(RemoteClientSession.ServerSilenceTimeout);
        await WaitUntilAsync(() => !session.IsConnected, "the watchdog to end the session");
        Assert.Equal(RemoteClientSession.ServerSilenceDisconnectReason, session.DisconnectReason);
    }

    // Before JoinAccepted the join supervisor's 45 s timeout applies; the watchdog is not armed.
    [Fact]
    public async Task IsNotArmed_BeforeTheJoin()
    {
        ManualTimerTimeProvider clock = new();
        SilentServerConnection server = new();
        await using RemoteClientSession session = CreateSession(server, clock);
        server.Queue(new ServerHelloMessage("test-server", TransportEnvelope.CurrentProtocolVersion, 0));
        session.StartBackgroundReceive();
        await WaitUntilAsync(() => session.InboundFrameCount == 1, "the hello");

        clock.Advance(TimeSpan.FromSeconds(30));
        await Task.Delay(50);
        Assert.Equal(0, clock.TimerCount);
        Assert.True(session.IsConnected);
        Assert.Null(session.DisconnectReason);
    }

    [Fact]
    public async Task Dispose_StopsTheArmedWatchdog_WithoutAReason()
    {
        ManualTimerTimeProvider clock = new();
        SilentServerConnection server = new();
        RemoteClientSession session = CreateSession(server, clock);
        await JoinAsync(session, server, clock);

        await session.DisposeAsync().AsTask().WaitAsync(Timeout);
        clock.Advance(TimeSpan.FromMinutes(1));
        await Task.Delay(50);
        Assert.Null(session.DisconnectReason);
    }

    private static async Task JoinAsync(RemoteClientSession session, SilentServerConnection server, ManualTimerTimeProvider clock)
    {
        server.Queue(new ServerHelloMessage("test-server", TransportEnvelope.CurrentProtocolVersion, 0));
        server.Queue(new JoinAcceptedMessage(SessionId, 1, 0, null));
        session.StartBackgroundReceive();
        await WaitUntilAsync(() => session.IsJoined, "the join");
        await WaitUntilAsync(() => clock.HasTimerDueIn(RemoteClientSession.ServerSilenceTimeout), "the watchdog to arm");
    }

    private static ServerSnapshotMessage Snapshot(ulong tick) => new(new ServerSnapshotEnvelope(
        tick,
        new Dictionary<string, ulong>(),
        Array.Empty<ActorSnapshot>(),
        PlayerInteractionMode.Building,
        null,
        Array.Empty<WorldMutationDelta>(),
        Array.Empty<DamageEventDelta>()));

    private static RemoteClientSession CreateSession(SilentServerConnection server, TimeProvider clock)
    {
        return new RemoteClientSession(
            server,
            new TransportJsonCodec(),
            new LengthPrefixedMessageFramer(),
            new RemoteClientOptions("127.0.0.1", 5057, SessionId, "player-w", "player-w"),
            clock);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        DateTime deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"Timed out waiting for {what}.");
            await Task.Delay(2);
        }
    }

    // A fake server end that sends only what the test queues and counts the client's keepalives.
    private sealed class SilentServerConnection : ITransportConnection
    {
        private readonly Channel<byte[]> _inbound = Channel.CreateUnbounded<byte[]>();
        private readonly TransportJsonCodec _codec = new();
        private readonly LengthPrefixedMessageFramer _framer = new();
        private int _keepalives;
        private ulong _sequence;

        public string ConnectionId => "watchdog-test";

        public int KeepaliveCount => Volatile.Read(ref _keepalives);

        public void Queue(IServerToClientMessage message)
        {
            TransportEnvelope envelope = new(TransportEnvelope.CurrentProtocolVersion, message.MessageKind, Interlocked.Increment(ref _sequence));
            byte[] payload = message switch
            {
                ServerHelloMessage hello => _codec.Encode(envelope, hello),
                JoinAcceptedMessage accepted => _codec.Encode(envelope, accepted),
                ServerSnapshotMessage snapshot => _codec.Encode(envelope, snapshot),
                _ => throw new InvalidOperationException()
            };
            _inbound.Writer.TryWrite(_framer.Frame(payload));
        }

        public void QueueEmptyFrame() => _inbound.Writer.TryWrite(_framer.Frame(Array.Empty<byte>()));

        public async ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            try
            {
                byte[] frame = await _inbound.Reader.ReadAsync(cancellationToken);
                frame.CopyTo(buffer);
                return frame.Length;
            }
            catch (ChannelClosedException)
            {
                return 0;
            }
        }

        public ValueTask SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
        {
            Assert.True(_framer.TryDeframe(payload.Span, out byte[]? message, out _));
            if (message!.Length == 0)
            {
                Interlocked.Increment(ref _keepalives);
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            _inbound.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }
}
