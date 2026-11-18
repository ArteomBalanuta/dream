using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using OpenTK.Mathematics;
using Xunit;
using Dream.Client.Replication;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Multiplayer.Transport;
using Dream.Server;
using Dream.Simulation.Enemies;
using Dream.Simulation.Persistence;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Tests.Utilities;

namespace Dream.Tests.Integration;

internal sealed record WireFrame(byte[] Payload, DecodedProtocolMessage Decoded, byte[] PayloadJson);

// The bound on a "wait until the server shows X" loop in the loopback tests.
//
// Wall clock on its own is the wrong measure here. The suite runs many of these classes at once,
// each with its own real server, its own sockets and its own SQLite file, and under that load the
// process can lose most of its CPU: a loop that IS getting somewhere - the server keeps ticking,
// frames keep arriving - can still run out of its 30 seconds and fail a test for a bug that is not
// there. That is measured, not hypothetical: standing up ~10 neighbouring WorldSessionRuntime
// instances in a neighbouring class failed this family 3 runs out of 3, where 4 runtimes was green.
// The suite was passing on the margin, not on the correctness.
//
// So the bound is in two parts, and neither of them is "be generous and hope":
//
//  - A no-progress window (WireLoopbackServer.Timeout, 30 s). The loop fails as soon as the thing
//    it is watching has not advanced for that long: the server stopped ticking, or no frame
//    arrived. That is the failure the deadline was written to catch - a wedged host, a tick that
//    deadlocks, a socket that stops delivering - and CPU starvation cannot fake it, because a
//    starved server still ticks and a starved socket still delivers, only slower. Every bit of
//    progress the loop observes pushes this window out again.
//  - A hard cap on the whole loop (HardTimeout). That catches the other real failure, a condition
//    that never becomes true while the server ticks on quite happily, and it is what keeps this
//    from being an unbounded wait: a stuck test still FAILS instead of hanging the suite. It is
//    reported up to six times slower than a flat 30 s would have reported it; nothing that used to
//    be caught escapes it.
internal struct LoopbackDeadline
{
    // Six times the no-progress window: far more than any CPU contention this suite has produced,
    // and still short enough that a genuinely stuck test reports inside a normal run.
    public static readonly TimeSpan HardTimeout = TimeSpan.FromSeconds(180);

    // A budget in PROGRESS, for a wait whose condition is known to need only a bounded number of
    // server ticks: an admin reply, a chat notice, a standing applied to a live connection - all of
    // which are a persistence round trip plus one tick. Ticks are the honest unit for those, and
    // they are starvation-proof by construction: a loaded machine spends the same ticks over more
    // seconds, it does not make the event need more ticks.
    //
    // It also fixes what the hard cap alone cost. The cap is a backstop for a condition that never
    // becomes true, and as the OPERATIVE deadline it is very expensive: one such wait burned the
    // full 180 s in a full-suite run and that was nearly the whole suite's slowdown. With a budget,
    // the same never-true condition fails after this many ticks, which on a healthy machine is
    // seconds. The cap stays underneath for anything the budget cannot judge.
    //
    // Two budgets, because the waits are two shapes and one value serves neither well. Measured: a
    // UntilAsync iteration is a tick plus a client drain plus a 2 ms delay, about 17 ms, so the loop
    // spends roughly 58 ticks a second on an idle machine - 4096 ticks cost 70 s, which is not a
    // cost bound worth having.
    //
    // WriteRoundTripTicks is for a wait that still has a persistence write in front of it (an admin
    // command's reply is sent by that write's own barrier read). The write is wall clock on the
    // worker, so the margin is for it: 1024 ticks is about 18 s idle, several times SQLite's 5 s
    // busy timeout, and more wall clock than that when the machine is loaded, since a starved loop
    // spends its ticks more slowly.
    //
    // AppliedTicks is for a wait whose write has ALREADY landed - the test observed the reply the
    // barrier read sent - so all that is left is the tick on which StepAccountStandings applies the
    // result to the live connections, plus the server reading one frame off the loopback socket.
    // That is one or two ticks; 512 is about 9 s idle, which is a large allowance for a loopback
    // read and keeps the failure cheap.
    public const long WriteRoundTripTicks = 1024;
    public const long AppliedTicks = 512;

    private readonly DateTime _hardDeadline;
    private readonly TimeSpan _noProgressWindow;
    private readonly long _startProgress;
    private readonly long _tickBudget;
    private DateTime _quietSince;
    private long _lastProgress;

    // progress is any counter that advances while the loop is getting somewhere: the server's tick
    // number, the client's received-frame count. maxTicks bounds the wait in that counter as well;
    // pass it only for a condition verified to need a bounded number of ticks, never for one on a
    // cadence (a 120 s trade timeout is about 7680 ticks) and never for one gated on the wall clock.
    public LoopbackDeadline(long progress, long? maxTicks = null)
    {
        _noProgressWindow = WireLoopbackServer.Timeout;
        DateTime now = DateTime.UtcNow;
        _hardDeadline = now + HardTimeout;
        _quietSince = now;
        _lastProgress = progress;
        _startProgress = progress;
        _tickBudget = maxTicks ?? 0;
    }

    // Fails the test when the loop has stopped getting anywhere: nothing advanced for the
    // no-progress window, the tick budget is spent, or the hard cap is up. Call it once per
    // iteration, before doing the work.
    public void AssertAlive(long progress, string what) => AssertAlive(progress, () => what);

    // The message is built only on failure, so a wait can name the state it was watching without
    // paying for the string on every pass.
    public void AssertAlive(long progress, Func<string> what)
    {
        DateTime now = DateTime.UtcNow;
        if (progress != _lastProgress)
        {
            _lastProgress = progress;
            _quietSince = now;
        }

        Assert.True(
            now - _quietSince < _noProgressWindow,
            $"never saw {what()}: nothing advanced for {_noProgressWindow.TotalSeconds:0} s, so the server is stuck, not slow.");
        if (_tickBudget > 0)
        {
            Assert.True(
                progress - _startProgress < _tickBudget,
                $"never saw {what()} within {_tickBudget} server ticks, which is not a loaded machine: this condition needs a couple of ticks once its write has landed.");
        }

        Assert.True(now < _hardDeadline, $"never saw {what()} within {HardTimeout.TotalSeconds:0} s.");
    }
}

// The real DedicatedServerService and a given host over loopback TCP, ticked by the test one fixed
// step at a time instead of by the service's own tick loop. Works with deferred joins (the
// production authoritative host): a join is applied by the next tick.
internal sealed class WireLoopbackServer : IAsyncDisposable
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    // The options a wire test uses unless it brings its own, and why they move the idle timeout off
    // ServerConnectionOptions.Default's 10 s.
    //
    // The server closes a connection that has sent no complete frame for IdleTimeout. A real client
    // never trips that: RemoteClientSession runs a keepalive task that writes an empty frame every
    // second for exactly this reason. WireLoopbackClient is a hand-written wire client with no
    // keepalive, so it sends nothing at all while the test is waiting for something - and a wait
    // that takes longer than 10 s therefore gets its own connection closed underneath it, after
    // which the thing it is waiting for can NEVER arrive. The test then runs to its cap and fails
    // for a bug that is not there.
    //
    // That is how Admin_Mute_SilencesThePlayer_AcrossARejoin_UntilUnmuted failed a full-suite run
    // at the default gate while passing in 2 s alone: the muted notice needs a couple of ticks, but
    // once a loaded machine stretched those ticks past 10 s the muted player's connection was gone.
    // Reproduced here by holding the wait open deliberately: the failure reports "no connection
    // context" for a client that had joined.
    //
    // So it is a harness artifact, not server behaviour worth preserving in these tests, and it is
    // raised past LoopbackDeadline.HardTimeout so no bounded wait can outlive the connection it is
    // waiting on. Nothing loses coverage: every test that means to exercise the idle close builds
    // its own ServerConnectionOptions and passes them to StartAsync (Wave8IdleTimeoutClientStall,
    // CoalesceOverflowResync, Wave10TransferInteraction, and the Server/ connection suites), so none
    // of them reads this.
    public static readonly ServerConnectionOptions WireClientOptions =
        PerTickSnapshots.Options with { IdleTimeout = LoopbackDeadline.HardTimeout + TimeSpan.FromSeconds(60) };

    private readonly Dictionary<WireLoopbackClient, ServerConnection> _connections = [];

    private WireLoopbackServer(DedicatedServerHost host, DedicatedServerService service)
    {
        Host = host;
        Service = service;
    }

    public DedicatedServerHost Host { get; }

    public DedicatedServerService Service { get; }

    public int Port => Service.Port;

    public WorldSessionRuntime Runtime => Host.Runtime!;

    public static Task<WireLoopbackServer> StartAsync(DedicatedServerHost host, ServerConnectionOptions? options = null)
    {
        DedicatedServerService service = new(
            host,
            new TcpServerTransport(IPAddress.Loopback, 0),
            new TransportJsonCodec(),
            new LengthPrefixedMessageFramer(),
            // Tests step the server one tick at a time and wait for that tick's snapshot, and a
            // keepalive-less wire client must not be idle-closed while a wait runs: see
            // WireClientOptions.
            options ?? WireClientOptions);
        service.Start();
        return Task.FromResult(new WireLoopbackServer(host, service));
    }

    public async Task<WireLoopbackClient> ConnectAndJoinAsync(
        string sessionId,
        int? receiveBufferSize = null,
        bool awaitPrefetchBeforeTicking = false,
        string? asName = null,
        string? withTokenOf = null)
    {
        Task<ServerConnection> accept = Service.AcceptOnThreadPoolAsync();
        WireLoopbackClient client = await WireLoopbackClient.ConnectAsync(Port, sessionId, receiveBufferSize);
        client.JoinNameOverride = asName;
        client.JoinTokenOverride = withTokenOf == null ? null : Dream.Tests.Support.TestAccountTokens.For(withTokenOf);
        ServerConnection connection = await accept.WaitAsync(Timeout);
        _connections[client] = connection;
        await client.HandshakeAsync();
        DateTime deadline = DateTime.UtcNow + Timeout;
        if (awaitPrefetchBeforeTicking)
        {
            // A join is deferred, tick after tick, until its player's persisted-state load finished,
            // and each tick moves the scripted enemies. Waiting for the load (which starts when the
            // server reads the join request) makes the first tick apply the join, so the join lands
            // on the same tick however loaded the machine is.
            string playerName = WorldSessionRuntime.ResolveJoinPlayerName(sessionId, $"Name {sessionId}");
            // The prefetch is keyed by (name, account) on a host with accounts.
            Dream.Simulation.Characters.AccountKey? account = Runtime.HasAccounts ? Dream.Tests.Support.TestAccountTokens.KeyFor(playerName) : null;
            while (Runtime.GetPersistedPlayerLoadState(playerName, account) != PersistedPlayerLoadState.Ready)
            {
                Assert.True(DateTime.UtcNow < deadline, $"{sessionId}'s persisted state never loaded");
                await Task.Delay(1);
            }
        }

        LoopbackDeadline joining = new(Ticks);
        while (!Host.IsConnectionJoined(connection.ConnectionId))
        {
            joining.AssertAlive(Ticks, $"{sessionId} join");
            await Task.Delay(5);
            // A deferred join is applied (and answered) by the next tick.
            await TickAsync();
            Assert.True(await FlushAllAsync());
        }

        await client.WaitForJoinAcceptedAsync();
        return client;
    }

    // A client that joins with the given name and token and is refused: ticks until the JoinRejected
    // arrives, closes the client and returns the rejection. (Wave G: a stranger asking for an owned character.)
    public async Task<JoinRejectedMessage> ConnectAndGetJoinRejectionAsync(string sessionId, string displayName, string? token)
    {
        Task<ServerConnection> accept = Service.AcceptOnThreadPoolAsync();
        WireLoopbackClient client = await WireLoopbackClient.ConnectAsync(Port, sessionId);
        ServerConnection connection = await accept.WaitAsync(Timeout);
        _connections[client] = connection;
        try
        {
            await client.SendAsync(new ClientHelloMessage($"wire-{sessionId}"));
            await client.SendAsync(new ClientJoinRequestMessage(sessionId, displayName, token));
            LoopbackDeadline answering = new(Ticks);
            JoinRejectedMessage? rejected = null;
            while (rejected == null)
            {
                answering.AssertAlive(Ticks, $"an answer for {sessionId}");
                await Task.Delay(5);
                await TickAsync();
                Assert.True(await FlushAllAsync());
                rejected = client.Frames.Select(frame => frame.Decoded.Message).OfType<JoinRejectedMessage>().FirstOrDefault();
            }

            return rejected;
        }
        finally
        {
            await DisconnectAsync(client);
        }
    }

    public ServerConnection ConnectionFor(WireLoopbackClient client) => _connections[client];

    // The server's tick number as a LoopbackDeadline progress counter: it advances whenever the
    // server is still running, however slowly, and stops dead when it is not.
    public long Ticks => unchecked((long)Runtime.CurrentServerTick);

    // Ticks the server and drains its clients until done(), bounded by LoopbackDeadline rather than
    // by a flat 30 s of wall clock, so a loaded machine cannot fail the wait for something that was
    // only going to be late. This is the shared form of the UntilAsync helper the classes in this
    // family each had a copy of; they forward here.
    public Task UntilAsync(Func<bool> done, string what, long? maxTicks = null) =>
        UntilAsync(done, () => what, maxTicks);

    // maxTicks bounds the wait in server ticks as well as seconds; see LoopbackDeadline. The message
    // is a factory so a wait can report the state it was watching without building the string on
    // every pass.
    public async Task UntilAsync(Func<bool> done, Func<string> what, long? maxTicks = null)
    {
        LoopbackDeadline deadline = new(Ticks, maxTicks);
        while (!done())
        {
            deadline.AssertAlive(Ticks, what);
            await TickAsync();
            await FlushAllAsync();
            await Task.Delay(2);
        }
    }

    // Closes the client socket and waits until the server has dropped the connection.
    public async Task DisconnectAsync(WireLoopbackClient client)
    {
        ServerConnection connection = _connections[client];
        int before = Service.ConnectionCount;
        await client.DisposeAsync();
        _connections.Remove(client);
        DateTime deadline = DateTime.UtcNow + Timeout;
        while (Service.ConnectionCount >= before || Host.IsConnectionJoined(connection.ConnectionId))
        {
            Assert.True(DateTime.UtcNow < deadline, $"{client.SessionId}'s connection was never dropped.");
            await Task.Delay(5);
        }
    }

    // One fixed tick per call; the clock's float accumulation may need a second nudge. The tick
    // waits until the server has read every command and action the clients sent before it, so a
    // scripted step is applied in the tick that follows it however late the server's receive loop
    // runs on a busy machine.
    public async Task TickAsync()
    {
        await WaitUntilSentMessagesAreReadAsync();
        ulong before = Runtime.CurrentServerTick;
        int executed = await Service.AdvanceOnceAsync(SimulationClock.FixedDeltaSeconds);
        if (executed == 0)
        {
            executed = await Service.AdvanceOnceAsync(SimulationClock.FixedDeltaSeconds * 0.01);
        }

        Assert.Equal(1, executed);
        Assert.Equal(before + 1, Runtime.CurrentServerTick);
    }

    // The server routes a command or action under the same lock that runs a tick, recording it as
    // the connection's last one before it returns, so once the last sent sequence shows here the
    // next tick sees it.
    private async Task WaitUntilSentMessagesAreReadAsync()
    {
        DateTime deadline = DateTime.UtcNow + Timeout;
        foreach ((WireLoopbackClient client, ServerConnection connection) in _connections)
        {
            SpinWait spinner = default;
            while (!HasRead(connection.ConnectionId, client))
            {
                Assert.True(DateTime.UtcNow < deadline, $"The server never read {client.SessionId}'s last command or action.");
                Assert.False(connection.IsFaulted, $"{client.SessionId}'s connection faulted.");
                if (spinner.NextSpinWillYield)
                {
                    await Task.Yield();
                }

                spinner.SpinOnce(sleep1Threshold: -1);
            }
        }
    }

    private bool HasRead(string connectionId, WireLoopbackClient client)
    {
        ulong commands = client.SentCommandSequence;
        ulong actions = client.SentActionSequence;
        if (commands == 0 && actions == 0)
        {
            return true;
        }

        if (!Host.TryGetConnectionContext(connectionId, out ServerRoutingContext? context) || context == null)
        {
            return false;
        }

        return (commands == 0 || context.LastCommandMessage?.CommandFrame.Sequence >= commands) &&
            (actions == 0 || context.LastActionMessage?.ActionSequence >= actions);
    }

    public async Task<bool> FlushAllAsync(params WireLoopbackClient[] except)
    {
        bool flushed = true;
        foreach ((WireLoopbackClient client, ServerConnection connection) in _connections)
        {
            if (Array.IndexOf(except, client) < 0)
            {
                flushed &= await OutboundDrain.FlushAsync(connection, Timeout);
            }
        }

        return flushed;
    }

    // From the session's eye to the nearest live enemy's combat anchor, or null with none alive.
    public Vector3? DirectionToNearestEnemy(string sessionId)
    {
        ActorSnapshot? player = Runtime.CaptureActorSnapshots().Where(actor => actor.SessionId == sessionId).Cast<ActorSnapshot?>().FirstOrDefault();
        if (player == null)
        {
            return null;
        }

        Vector3 eye = player.Value.Position + new Vector3(0f, 0.35f, 0f);
        EnemyActor? nearest = Runtime.Enemies.ActiveActors
            .Where(enemy => enemy.Combatant.Health.IsAlive)
            .OrderBy(enemy => (enemy.Position - eye).LengthSquared)
            .FirstOrDefault();
        if (nearest == null)
        {
            return null;
        }

        Vector3 direction = nearest.Combatant.GetCombatAnchorWorldPosition() - eye;
        return direction.LengthSquared <= 0.0001f ? Vector3.UnitZ : direction.Normalized();
    }

    public async ValueTask DisposeAsync()
    {
        await Service.DisposeAsync();
        Host.Dispose();
    }
}

// A protocol v2 client written against the wire format: it encodes its own frames and keeps every
// received frame's exact bytes, and the raw byte stream as it came off the socket.
internal sealed class WireLoopbackClient : IAsyncDisposable
{
    private const string SnapshotKind = "server.snapshot";

    private readonly TcpClient _tcp;
    private readonly NetworkStream _stream;
    private readonly TransportJsonCodec _codec = new();
    private readonly LengthPrefixedMessageFramer _framer = new();
    private readonly List<WireFrame> _frames = [];
    private readonly ActorStateCache _actorStates = new();
    private readonly MemoryStream _raw = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ManualResetEventSlim _readGate = new(initialState: true);
    private readonly Task _readLoop;
    private ulong _outboundSequence;
    private ulong _commandSequence;
    private ulong _actionSequence;
    private int _disposed;

    private WireLoopbackClient(TcpClient tcp, string sessionId)
    {
        _tcp = tcp;
        _stream = tcp.GetStream();
        SessionId = sessionId;
        _readLoop = Task.Factory.StartNew(ReadLoop, TaskCreationOptions.LongRunning);
    }

    public string SessionId { get; }

    // The sequence of the last command and action this client wrote to the socket (0 for none).
    public ulong SentCommandSequence => Volatile.Read(ref _commandSequence);

    public ulong SentActionSequence => Volatile.Read(ref _actionSequence);

    public Exception? ReadFault { get; private set; }

    // SCALE-2: delta frames whose base this client did not hold, and actors it withheld as stale.
    // The server sends a keyframe whenever a connection lacks the base, so both stay 0. Read once
    // the read loop has settled.
    public long ActorChainBreaks => _actorStates.ChainBreaks;

    public long StaleActorsWithheld => _actorStates.StaleActorsWithheld;

    public long ActorKeyframesApplied => _actorStates.KeyframesApplied;

    public long ActorDeltaFramesApplied => _actorStates.DeltaFramesApplied;

    public IReadOnlyList<WireFrame> Frames
    {
        get
        {
            lock (_frames)
            {
                return _frames.ToArray();
            }
        }
    }

    public byte[] RawStream
    {
        get
        {
            lock (_frames)
            {
                return _raw.ToArray();
            }
        }
    }

    public IEnumerable<WireFrame> Snapshots() => Frames.Where(frame => frame.Decoded.Envelope.MessageKind == SnapshotKind);

    public static async Task<WireLoopbackClient> ConnectAsync(int port, string sessionId, int? receiveBufferSize = null)
    {
        TcpClient tcp = new() { NoDelay = true };
        if (receiveBufferSize.HasValue)
        {
            tcp.ReceiveBufferSize = receiveBufferSize.Value;
        }

        await tcp.ConnectAsync(IPAddress.Loopback, port);
        return new WireLoopbackClient(tcp, sessionId);
    }

    // Set before HandshakeAsync to join as another character of another account's token (an alt):
    // the default is "Name {session}" with the token derived from that name.
    public string? JoinNameOverride { get; set; }

    public string? JoinTokenOverride { get; set; }

    public async Task HandshakeAsync()
    {
        string name = JoinNameOverride ?? $"Name {SessionId}";
        await SendAsync(new ClientHelloMessage($"wire-{SessionId}"));
        await SendAsync(new ClientJoinRequestMessage(SessionId, name, JoinTokenOverride ?? Dream.Tests.Support.TestAccountTokens.For(name)));
    }

    public Task WaitForJoinAcceptedAsync()
    {
        return WaitForAsync(frames => frames.Any(frame => frame.Decoded.Message is JoinAcceptedMessage), "join accepted");
    }

    public Task WaitForSnapshotTickAsync(ulong tick)
    {
        return WaitForAsync(
            frames => frames.Any(frame => frame.Decoded.Message is ServerSnapshotMessage snapshot && snapshot.Snapshot.ServerTick >= tick),
            $"snapshot tick {tick}");
    }

    public Task WaitForAsync(Func<IReadOnlyList<WireFrame>, bool> condition, string what)
    {
        return WaitForCoreAsync(condition, what);
    }

    public Task SendCommandAsync(PlayerInteractionMode mode, PlayerMoveIntent move, Vector3 facing, bool attack)
    {
        _commandSequence++;
        return SendAsync(new PlayerCommandMessage(new PlayerCommandFrame(
            new PlayerSessionId(SessionId),
            _commandSequence,
            new PlayerModeSwitchIntent(mode),
            move,
            new PlayerLookIntent(0f, 0f, facing),
            PrimaryUsePressed: attack)));
    }

    public Task SendActionAsync(IPlayerActionPayload payload)
    {
        _actionSequence++;
        return SendAsync(new PlayerActionMessage(new PlayerSessionId(SessionId), _actionSequence, payload));
    }

    public async Task SendAsync<TMessage>(TMessage message)
        where TMessage : IClientToServerMessage
    {
        _outboundSequence++;
        byte[] payload = _codec.Encode(new TransportEnvelope(TransportEnvelope.CurrentProtocolVersion, message.MessageKind, _outboundSequence), message);
        await _stream.WriteAsync(_framer.Frame(payload));
    }

    public void PauseReading() => _readGate.Reset();

    public void ResumeReading() => _readGate.Set();

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _shutdown.Cancel();
        _readGate.Set();
        _tcp.Dispose();
        try
        {
            await _readLoop.WaitAsync(WireLoopbackServer.Timeout);
        }
        catch (Exception)
        {
        }

        _shutdown.Dispose();
    }

    // Waits on arriving frames, so the progress counter is how many have arrived: the failure this
    // is here to catch is a server that has gone silent, and that is exactly "no new frame for the
    // no-progress window". A server that is merely slow keeps the count moving and keeps the wait
    // alive, up to LoopbackDeadline.HardTimeout.
    private async Task WaitForCoreAsync(Func<IReadOnlyList<WireFrame>, bool> condition, string what)
    {
        LoopbackDeadline deadline = new(Frames.Count);
        while (!condition(Frames))
        {
            Assert.True(ReadFault == null, $"{SessionId} read loop faulted: {ReadFault}");
            deadline.AssertAlive(Frames.Count, $"{what} on {SessionId}");
            await Task.Delay(5);
        }
    }

    private void ReadLoop()
    {
        byte[] chunk = new byte[16 * 1024];
        List<byte> pending = [];
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                _readGate.Wait(_shutdown.Token);
                int read = _stream.Read(chunk, 0, chunk.Length);
                if (read <= 0)
                {
                    return;
                }

                lock (_frames)
                {
                    _raw.Write(chunk, 0, read);
                }

                pending.AddRange(chunk.AsSpan(0, read).ToArray());
                byte[] buffered = pending.ToArray();
                int offset = 0;
                while (_framer.TryDeframe(buffered.AsSpan(offset), out byte[]? payload, out int consumed))
                {
                    offset += consumed;
                    DecodedProtocolMessage decoded = _codec.Decode(payload);
                    // Like RemoteClientSession, rebuild full actors from delta frames in wire order.
                    if (decoded.Message is ServerSnapshotMessage snapshot)
                    {
                        decoded = decoded with { Message = _actorStates.Apply(snapshot) };
                    }

                    WireFrame frame = new(payload!, decoded, ExtractPayloadJson(payload!));
                    lock (_frames)
                    {
                        _frames.Add(frame);
                    }
                }

                pending.RemoveRange(0, offset);
            }
        }
        catch (Exception) when (_shutdown.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            ReadFault = ex;
        }
    }

    // The raw bytes of the envelope's "payload" value.
    private static byte[] ExtractPayloadJson(byte[] frame)
    {
        Utf8JsonReader reader = new(frame);
        reader.Read();
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            bool isPayload = reader.ValueTextEquals("payload"u8);
            reader.Read();
            int start = (int)reader.TokenStartIndex;
            reader.Skip();
            if (isPayload)
            {
                return frame[start..(int)reader.BytesConsumed];
            }
        }

        throw new InvalidOperationException("Frame has no payload.");
    }
}
