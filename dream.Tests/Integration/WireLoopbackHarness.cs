using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using OpenTK.Mathematics;
using Xunit;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Multiplayer.Transport;
using Dream.Server;
using Dream.Simulation.Enemies;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;

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
            new LengthPrefixedMessageFramer());
        service.Start();
        return Task.FromResult(new WireLoopbackServer(host, service));
    }

    public async Task<WireLoopbackClient> ConnectAndJoinAsync(string sessionId, int? receiveBufferSize = null)
    {
        Task<ServerConnection> accept = Service.AcceptNextClientAsync().AsTask();
        WireLoopbackClient client = await WireLoopbackClient.ConnectAsync(Port, sessionId, receiveBufferSize);
        ServerConnection connection = await accept.WaitAsync(Timeout);
        _connections[client] = connection;
        await client.HandshakeAsync();
        DateTime deadline = DateTime.UtcNow + Timeout;
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

    // One fixed tick per call; the clock's float accumulation may need a second nudge.
    public async Task TickAsync()
    {
        ulong before = Runtime.CurrentServerTick;
        int executed = await Service.AdvanceOnceAsync(SimulationClock.FixedDeltaSeconds);
        if (executed == 0)
        {
            executed = await Service.AdvanceOnceAsync(SimulationClock.FixedDeltaSeconds * 0.01);
        }

        Assert.Equal(1, executed);
        Assert.Equal(before + 1, Runtime.CurrentServerTick);
    }

    public async Task<bool> FlushAllAsync(params WireLoopbackClient[] except)
    {
        bool flushed = true;
        foreach ((WireLoopbackClient client, ServerConnection connection) in _connections)
        {
            if (Array.IndexOf(except, client) < 0)
            {
                flushed &= await connection.FlushOutboundAsync(Timeout);
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

    public Exception? ReadFault { get; private set; }

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
        await SendAsync(new ClientJoinRequestMessage(SessionId, $"Name {SessionId}"));
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
