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

// The real DedicatedServerService and a given host over loopback TCP, ticked by the test one fixed
// step at a time instead of by the service's own tick loop. Works with deferred joins (the
// production authoritative host): a join is applied by the next tick.
internal sealed class WireLoopbackServer : IAsyncDisposable
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

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

    public static Task<WireLoopbackServer> StartAsync(DedicatedServerHost host)
    {
        DedicatedServerService service = new(
            host,
            new TcpServerTransport(IPAddress.Loopback, 0),
            new TransportJsonCodec(),
            new LengthPrefixedMessageFramer(),
            // Tests step the server one tick at a time and wait for that tick's snapshot.
            PerTickSnapshots.Options);
        service.Start();
        return Task.FromResult(new WireLoopbackServer(host, service));
    }

    public async Task<WireLoopbackClient> ConnectAndJoinAsync(string sessionId, int? receiveBufferSize = null, bool awaitPrefetchBeforeTicking = false)
    {
        Task<ServerConnection> accept = Service.AcceptOnThreadPoolAsync();
        WireLoopbackClient client = await WireLoopbackClient.ConnectAsync(Port, sessionId, receiveBufferSize);
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

        while (!Host.IsConnectionJoined(connection.ConnectionId))
        {
            Assert.True(DateTime.UtcNow < deadline, $"{sessionId} never joined");
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
            DateTime deadline = DateTime.UtcNow + Timeout;
            JoinRejectedMessage? rejected = null;
            while (rejected == null)
            {
                Assert.True(DateTime.UtcNow < deadline, $"{sessionId} was never answered");
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

    public async Task HandshakeAsync()
    {
        await SendAsync(new ClientHelloMessage($"wire-{SessionId}"));
        await SendAsync(new ClientJoinRequestMessage(SessionId, $"Name {SessionId}", Dream.Tests.Support.TestAccountTokens.For($"Name {SessionId}")));
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

    private async Task WaitForCoreAsync(Func<IReadOnlyList<WireFrame>, bool> condition, string what)
    {
        DateTime deadline = DateTime.UtcNow + WireLoopbackServer.Timeout;
        while (!condition(Frames))
        {
            Assert.True(ReadFault == null, $"{SessionId} read loop faulted: {ReadFault}");
            Assert.True(DateTime.UtcNow < deadline, $"{SessionId} never received {what}.");
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
