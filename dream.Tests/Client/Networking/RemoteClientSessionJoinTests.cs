using OpenTK.Mathematics;
using Dream.Client.Replication;
using Dream.Client.World;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using System.Threading.Channels;
using Xunit;
using Dream.Client.Networking;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Multiplayer.Transport;
using Dream.Server;
using Dream.Simulation.Players;

namespace Dream.Tests.Client.Networking;

// The client's join supervision: a retryable JoinRejected re-sends the join with backoff, any other
// rejection or a missing JoinAccepted ends the session with a reason. Time is a manual
// TimeProvider, so the 45 s timeout and the backoff run without real waits.
public class RemoteClientSessionJoinTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private const string SessionId = "session-a";

    [Fact]
    public async Task RetryRejection_ResendsTheJoinWithBackoff_AndJoinsWhenAccepted()
    {
        ManualTimerTimeProvider clock = new();
        ScriptedServerConnection server = new(attempt => attempt < 3
            ? new JoinRejectedMessage(DedicatedServerHost.JoinRetryRejectionReason)
            : Accepted());
        await using RemoteClientSession session = CreateSession(server, clock);
        session.StartBackgroundReceive();
        await session.SendJoinRequestAsync();

        // First rejection: the retry waits 1 s, and nothing is re-sent before then.
        await WaitUntilAsync(() => clock.HasTimerDueIn(TimeSpan.FromSeconds(1)), "the first backoff");
        Assert.Equal(1, server.JoinRequestCount);
        clock.Advance(TimeSpan.FromMilliseconds(999));
        await Task.Delay(50);
        Assert.Equal(1, server.JoinRequestCount);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        await WaitUntilAsync(() => server.JoinRequestCount == 2, "the first retry");

        // Second rejection: the backoff doubles.
        await WaitUntilAsync(() => clock.HasTimerDueIn(TimeSpan.FromSeconds(2)), "the second backoff");
        clock.Advance(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => session.IsJoined, "the join");

        Assert.Equal(3, server.JoinRequestCount);
        Assert.Equal(2, session.JoinRetryCount);
        Assert.True(session.IsConnected);
        Assert.Null(session.DisconnectReason);
        List<IServerToClientMessage> inbound = Drain(session);
        Assert.Equal(2, inbound.OfType<JoinRejectedMessage>().Count());
        Assert.IsType<JoinAcceptedMessage>(inbound[^1]);
    }

    [Fact]
    public async Task NonRetryRejection_EndsTheSessionWithTheReason_WithoutResending()
    {
        ManualTimerTimeProvider clock = new();
        ScriptedServerConnection server = new(_ => new JoinRejectedMessage("Display name 'player-a' is already claimed by another active player."));
        await using RemoteClientSession session = CreateSession(server, clock);
        session.StartBackgroundReceive();
        await session.SendJoinRequestAsync();

        await WaitUntilAsync(() => !session.IsConnected, "the session end");

        Assert.Equal(
            "Join rejected: Display name 'player-a' is already claimed by another active player.",
            session.DisconnectReason);
        Assert.False(session.IsJoined);
        clock.Advance(TimeSpan.FromSeconds(60));
        await Task.Delay(50);
        Assert.Equal(1, server.JoinRequestCount);
        Assert.Equal(0, session.JoinRetryCount);
        // The rejection still reaches the owner's inbound queue.
        Assert.IsType<JoinRejectedMessage>(Assert.Single(Drain(session)));
    }

    [Fact]
    public async Task RetryRejections_PastTheRetryLimit_EndTheSession()
    {
        ManualTimerTimeProvider clock = new();
        ScriptedServerConnection server = new(_ => new JoinRejectedMessage(DedicatedServerHost.JoinRetryRejectionReason));
        await using RemoteClientSession session = CreateSession(server, clock);
        session.StartBackgroundReceive();
        await session.SendJoinRequestAsync();

        for (int retry = 0; retry < RemoteClientSession.MaxJoinRetries; retry++)
        {
            TimeSpan backoff = RemoteClientSession.JoinRetryBaseDelay * (1 << retry);
            await WaitUntilAsync(() => clock.HasTimerDueIn(backoff), $"backoff {retry + 1}");
            clock.Advance(backoff);
            int expected = retry + 2;
            await WaitUntilAsync(() => server.JoinRequestCount == expected, $"retry {retry + 1}");
        }

        await WaitUntilAsync(() => !session.IsConnected, "the session end");
        Assert.Equal(RemoteClientSession.MaxJoinRetries + 1, server.JoinRequestCount);
        Assert.Equal(RemoteClientSession.MaxJoinRetries, session.JoinRetryCount);
        Assert.Equal(
            $"Join rejected after {RemoteClientSession.MaxJoinRetries + 1} attempts: {DedicatedServerHost.JoinRetryRejectionReason}",
            session.DisconnectReason);
    }

    [Fact]
    public async Task JoinTimeout_EndsTheSession_WhenTheServerNeverAnswers()
    {
        ManualTimerTimeProvider clock = new();
        ScriptedServerConnection server = new(_ => null);
        await using RemoteClientSession session = CreateSession(server, clock);
        session.StartBackgroundReceive();
        await session.SendJoinRequestAsync();

        await WaitUntilAsync(() => clock.HasTimerDueIn(RemoteClientSession.JoinTimeout), "the join deadline");
        clock.Advance(RemoteClientSession.JoinTimeout - TimeSpan.FromMilliseconds(1));
        await Task.Delay(50);
        Assert.True(session.IsConnected);
        Assert.Null(session.DisconnectReason);

        clock.Advance(TimeSpan.FromMilliseconds(1));
        await WaitUntilAsync(() => !session.IsConnected, "the session end");
        Assert.Equal("Join timed out: the server did not accept the join within 45 s.", session.DisconnectReason);
        Assert.False(session.IsJoined);
        Assert.Equal(1, server.JoinRequestCount);
    }

    [Fact]
    public async Task JoinTimeout_CountsFromTheFirstRequest_AcrossRetries()
    {
        ManualTimerTimeProvider clock = new();
        ScriptedServerConnection server = new(attempt => attempt == 1
            ? new JoinRejectedMessage(DedicatedServerHost.JoinRetryRejectionReason)
            : null);
        await using RemoteClientSession session = CreateSession(server, clock);
        session.StartBackgroundReceive();
        await session.SendJoinRequestAsync();

        await WaitUntilAsync(() => clock.HasTimerDueIn(TimeSpan.FromSeconds(1)), "the backoff");
        clock.Advance(TimeSpan.FromSeconds(1));
        await WaitUntilAsync(() => server.JoinRequestCount == 2, "the retry");
        await WaitUntilAsync(() => clock.HasTimerDueIn(RemoteClientSession.JoinTimeout - TimeSpan.FromSeconds(1)), "the remaining deadline");
        clock.Advance(RemoteClientSession.JoinTimeout - TimeSpan.FromSeconds(1));

        await WaitUntilAsync(() => !session.IsConnected, "the session end");
        Assert.StartsWith("Join timed out", session.DisconnectReason);
    }

    [Fact]
    public async Task JoinAccepted_StopsTheTimeout()
    {
        ManualTimerTimeProvider clock = new();
        ScriptedServerConnection server = new(_ => Accepted());
        await using RemoteClientSession session = CreateSession(server, clock);
        session.StartBackgroundReceive();
        await session.SendJoinRequestAsync();

        await WaitUntilAsync(() => session.IsJoined, "the join");
        // The join deadline timer goes; since NETR-2 the only timer left is the server-silence
        // watchdog's, due 8 s after the join (not at the 45 s join deadline).
        await WaitUntilAsync(
            () => clock.TimerCount == 1 && clock.HasTimerDueIn(RemoteClientSession.ServerSilenceTimeout),
            "the deadline timer to go");
        Assert.False(clock.HasTimerDueIn(RemoteClientSession.JoinTimeout));

        // Five minutes, well past the join deadline, with the server sending a frame every second
        // (it sends snapshots at 32-64 Hz; a silent one is the watchdog's business).
        for (int second = 1; second <= 300; second++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            long before = session.InboundFrameCount;
            server.Queue(new ServerHelloMessage("test-server", TransportEnvelope.CurrentProtocolVersion, (ulong)second));
            await WaitUntilAsync(() => session.InboundFrameCount > before, $"frame {second}");
        }

        await Task.Delay(50);
        Assert.True(session.IsConnected);
        Assert.Null(session.DisconnectReason);
    }

    // N3: while a send is stalled the keepalive sees no recent send every half interval. It used to
    // release the send-loop semaphore each time, so the count grew without bound and the loop spun
    // once per release after the send completed.
    [Fact]
    public async Task Keepalive_DuringAStalledSend_DoesNotPileUpSendSignals()
    {
        ScriptedServerConnection server = new(_ => null);
        await using RemoteClientSession session = CreateSession(server, TimeProvider.System);
        server.Queue(new ServerHelloMessage("test-server", TransportEnvelope.CurrentProtocolVersion, 0));
        session.StartBackgroundReceive();
        server.StallCommandSends();

        session.QueueCommandFrame(new PlayerCommandFrame(
            new PlayerSessionId(SessionId),
            1,
            new PlayerModeSwitchIntent(PlayerInteractionMode.Building),
            new PlayerMoveIntent(0f, 0f, 0f),
            new PlayerLookIntent(0f, 0f),
            PrimaryUsePressed: false));
        await WaitUntilAsync(() => session.IsSendInFlight && server.StalledSendCount == 1, "the stalled send");

        // Several keepalive checks (every half interval) while the send is stuck.
        await Task.Delay(RemoteClientSession.KeepaliveInterval * 4);
        Assert.True(session.IsSendInFlight);
        Assert.True(session.PendingSendSignalCount <= 1, $"{session.PendingSendSignalCount} send signals queued");

        server.ReleaseSends();
        Assert.True(await session.FlushOutboundAsync(Timeout), "the send loop did not drain");
        Assert.Equal(0, session.PendingSendSignalCount);
        Assert.True(session.IsConnected);
    }

    // The client-side path of an unsupported world, minus the PlayScene shell (a GL scene that cannot be
    // built in a test): the join-accepted travels the real wire, and the owner applies it and reacts the
    // way PlayScene.ApplyReplicatedMessage does (catch UnsupportedWorldException -> EndSessionWithReason).
    // Not covered: PlayScene itself making that call and the scene then leaving play.
    [Fact]
    public async Task JoinAcceptedWithAnUnsupportedWorld_EndsTheSessionWithAReasonNamingTheGenerator()
    {
        ManualTimerTimeProvider clock = new();
        WorldDescriptorWire wire = WorldDescriptorWire.From(WorldDescriptor.Default) with { GeneratorKey = "nope" };
        ScriptedServerConnection server = new(_ => new JoinAcceptedMessage(SessionId, 1, 0, null, World: wire));
        await using RemoteClientSession session = CreateSession(server, clock);
        session.StartBackgroundReceive();
        await session.SendJoinRequestAsync();
        await WaitUntilAsync(() => session.IsJoined, "the join");
        JoinAcceptedMessage accepted = Assert.IsType<JoinAcceptedMessage>(Assert.Single(Drain(session)));
        using ClientWorldSession client = new(20, new Vector3(5f, 1.5f, 5f), new SceneEventHub());

        try
        {
            ClientJoinAcceptedApplier.ApplyToRuntime(client, accepted);
            Assert.Fail("The unsupported world was applied.");
        }
        catch (Exception ex) when (ex is UnsupportedWorldException or InvalidDataException)
        {
            session.EndSessionWithReason(ex.Message, RemoteClientSession.UnsupportedWorldLogEvent);
        }

        Assert.False(session.IsConnected);
        Assert.NotNull(session.DisconnectReason);
        Assert.Contains("nope", session.DisconnectReason, StringComparison.Ordinal);
        Assert.Equal(20, client.Map.Width);
    }

    private static JoinAcceptedMessage Accepted() => new(SessionId, 1, 0, null);

    private static RemoteClientSession CreateSession(ScriptedServerConnection server, TimeProvider clock)
    {
        return new RemoteClientSession(
            server,
            new TransportJsonCodec(),
            new LengthPrefixedMessageFramer(),
            new RemoteClientOptions("127.0.0.1", 5057, SessionId, "player-a", "player-a"),
            clock);
    }

    private static List<IServerToClientMessage> Drain(RemoteClientSession session)
    {
        List<IServerToClientMessage> messages = [];
        while (session.TryDequeueInbound(out IServerToClientMessage? message) && message != null)
        {
            messages.Add(message);
        }

        return messages;
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

    // A fake server end: answers each join request with whatever the script returns for that
    // attempt (1-based; null sends nothing), and can stall command sends until released.
    private sealed class ScriptedServerConnection(Func<int, IServerToClientMessage?> joinReply) : ITransportConnection
    {
        private readonly Channel<byte[]> _inbound = Channel.CreateUnbounded<byte[]>();
        private readonly TransportJsonCodec _codec = new();
        private readonly LengthPrefixedMessageFramer _framer = new();
        private TaskCompletionSource _sendGate = Open();
        private int _joinRequests;
        private int _stalledSends;
        private ulong _sequence;

        public string ConnectionId => "join-test";

        public int JoinRequestCount => Volatile.Read(ref _joinRequests);

        public int StalledSendCount => Volatile.Read(ref _stalledSends);

        public void StallCommandSends() => Volatile.Write(ref _sendGate, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

        public void ReleaseSends() => Volatile.Read(ref _sendGate).TrySetResult();

        public void Queue(IServerToClientMessage message)
        {
            TransportEnvelope envelope = new(TransportEnvelope.CurrentProtocolVersion, message.MessageKind, Interlocked.Increment(ref _sequence));
            byte[] payload = message switch
            {
                ServerHelloMessage hello => _codec.Encode(envelope, hello),
                JoinAcceptedMessage accepted => _codec.Encode(envelope, accepted),
                JoinRejectedMessage rejected => _codec.Encode(envelope, rejected),
                _ => throw new InvalidOperationException()
            };
            _inbound.Writer.TryWrite(_framer.Frame(payload));
        }

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

        public async ValueTask SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
        {
            Assert.True(_framer.TryDeframe(payload.Span, out byte[]? message, out _));
            if (message!.Length == 0)
            {
                return;
            }

            IProtocolMessage decoded = _codec.Decode(message).Message;
            if (decoded is PlayerCommandMessage)
            {
                Task gate = Volatile.Read(ref _sendGate).Task;
                if (!gate.IsCompleted)
                {
                    Interlocked.Increment(ref _stalledSends);
                    await gate.WaitAsync(cancellationToken);
                }
            }

            if (decoded is ClientJoinRequestMessage)
            {
                int attempt = Interlocked.Increment(ref _joinRequests);
                if (joinReply(attempt) is IServerToClientMessage reply)
                {
                    Queue(reply);
                }
            }
        }

        public ValueTask DisposeAsync()
        {
            _inbound.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }

        private static TaskCompletionSource Open()
        {
            TaskCompletionSource open = new(TaskCreationOptions.RunContinuationsAsynchronously);
            open.SetResult();
            return open;
        }
    }
}

// A TimeProvider whose clock and timers move only on Advance. Timer callbacks run on the calling
// thread, outside the lock.
internal sealed class ManualTimerTimeProvider : TimeProvider
{
    private readonly object _sync = new();
    private readonly List<ManualTimer> _timers = [];
    private long _now = TimeSpan.TicksPerDay;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp()
    {
        lock (_sync)
        {
            return _now;
        }
    }

    public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddTicks(GetTimestamp());

    public int TimerCount
    {
        get
        {
            lock (_sync)
            {
                return _timers.Count(timer => timer.Due >= 0);
            }
        }
    }

    public bool HasTimerDueIn(TimeSpan dueIn)
    {
        lock (_sync)
        {
            long due = _now + dueIn.Ticks;
            return _timers.Any(timer => timer.Due == due);
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ManualTimer timer = new(this, callback, state);
        lock (_sync)
        {
            _timers.Add(timer);
        }

        timer.Change(dueTime, period);
        return timer;
    }

    public void Advance(TimeSpan by)
    {
        List<ManualTimer> fired = [];
        lock (_sync)
        {
            _now += by.Ticks;
            foreach (ManualTimer timer in _timers)
            {
                if (timer.Due >= 0 && timer.Due <= _now)
                {
                    fired.Add(timer);
                    timer.Due = timer.Period > 0 ? timer.Due + timer.Period : -1;
                }
            }
        }

        foreach (ManualTimer timer in fired)
        {
            timer.Fire();
        }
    }

    private sealed class ManualTimer(ManualTimerTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        public long Due = -1;
        public long Period;

        public void Fire() => callback(state);

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._sync)
            {
                if (!owner._timers.Contains(this))
                {
                    return false;
                }

                Due = dueTime == Timeout.InfiniteTimeSpan ? -1 : owner._now + dueTime.Ticks;
                Period = period == Timeout.InfiniteTimeSpan ? 0 : period.Ticks;
                return true;
            }
        }

        public void Dispose()
        {
            lock (owner._sync)
            {
                owner._timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
