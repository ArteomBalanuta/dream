using System.Diagnostics;
using OpenTK.Mathematics;
using Dream.Client.Networking;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Multiplayer.Transport;
using Dream.Simulation.Players;
using Dream.Tests.Support;

namespace Dream.Tests.Load;

// One headless player over the real client session (RemoteClientSession over TCP). The load
// runner calls Step once per command frame (64 Hz) from its driver thread: the bot sends a
// command frame from a seeded random walk with periodic builds, attacks and jumps, sends an
// occasional player.action, and drains everything the server sent. Wire bytes are counted by a
// transport wrapper, so they include framing.
public sealed class BotClient : IAsyncDisposable
{
    // Frames between the mode switch and the primary use that follows it.
    private const int UseDelayFrames = 3;

    private readonly RemoteClientSession _session;
    private readonly CountingConnection _connection;
    private readonly LoadActionMix _mix;
    private readonly Random _random;
    private readonly PlayerSessionId _sessionId;
    private readonly long _connectStartedTimestamp;
    private ulong _sequence;
    private ulong _actionSequence;
    private float _yaw;
    private bool _walking;
    private PlayerInteractionMode? _pendingUseMode;
    private int _pendingUseFrames;

    private BotClient(
        RemoteClientSession session,
        CountingConnection connection,
        LoadActionMix mix,
        int seed,
        long connectStartedTimestamp)
    {
        _session = session;
        _connection = connection;
        _mix = mix;
        _random = new Random(seed);
        _sessionId = new PlayerSessionId(session.SessionId);
        _connectStartedTimestamp = connectStartedTimestamp;
        _yaw = (float)(_random.NextDouble() * Math.PI * 2d);
        _walking = true;
    }

    public string SessionId => _session.SessionId;

    public bool IsConnected => _session.IsConnected;

    public bool JoinAccepted { get; private set; }

    public string? JoinRejectedReason { get; private set; }

    // Connect start to JoinAccepted, measured when the bot drained the reply.
    public double? JoinMilliseconds { get; private set; }

    public long BytesSent => _connection.BytesSent;

    public long BytesReceived => _connection.BytesReceived;

    public long CommandsSent => _session.SentCommandCount;

    public long MessagesReceived { get; private set; }

    public long SnapshotsReceived { get; private set; }

    public long ActionResultsReceived { get; private set; }

    public long BuildsRequested { get; private set; }

    public long AttacksRequested { get; private set; }

    public long ActionsQueued { get; private set; }

    public static async Task<BotClient> ConnectAsync(
        int port,
        string sessionId,
        LoadActionMix mix,
        int seed,
        IMessageFramer framer,
        CancellationToken cancellationToken)
    {
        long started = Stopwatch.GetTimestamp();
        CountingClientTransport transport = new(new TcpClientConnector());
        RemoteClientSession session = await RemoteClientSession.ConnectAsync(
            new RemoteClientOptions("127.0.0.1", port, sessionId, sessionId, "load-bot", TestAccountTokens.For(sessionId)),
            transport,
            new TransportJsonCodec(),
            framer,
            cancellationToken);
        // ConnectAsync starts the background receive loop; calling it again is a no-op.
        session.StartBackgroundReceive();
        return new BotClient(session, transport.LastConnection!, mix, seed, started);
    }

    // One command frame. dt is the frame interval in seconds.
    public void Step(double dt)
    {
        Drain();
        if (!_session.IsConnected)
        {
            return;
        }

        if (Chance(_mix.TurnsPerSecond, dt))
        {
            _yaw = (float)(_random.NextDouble() * Math.PI * 2d);
            _walking = _random.NextDouble() < _mix.WalkFraction;
        }

        PlayerModeSwitchIntent mode = PlayerModeSwitchIntent.None;
        bool primaryUse = false;
        if (_pendingUseMode is { } pendingMode)
        {
            if (--_pendingUseFrames <= 0)
            {
                primaryUse = true;
                _pendingUseMode = null;
                if (pendingMode == PlayerInteractionMode.Building)
                {
                    BuildsRequested++;
                }
                else
                {
                    AttacksRequested++;
                }
            }
        }
        else if (Chance(_mix.BuildsPerSecond, dt))
        {
            mode = new PlayerModeSwitchIntent(PlayerInteractionMode.Building);
            _pendingUseMode = PlayerInteractionMode.Building;
            _pendingUseFrames = UseDelayFrames;
        }
        else if (Chance(_mix.AttacksPerSecond, dt))
        {
            mode = new PlayerModeSwitchIntent(PlayerInteractionMode.Combat);
            _pendingUseMode = PlayerInteractionMode.Combat;
            _pendingUseFrames = UseDelayFrames;
        }

        bool jump = Chance(_mix.JumpsPerSecond, dt);
        // Look slightly down along the heading, so builds land on the ground in front.
        Vector3 facing = Vector3.Normalize(new Vector3(MathF.Sin(_yaw), -0.35f, MathF.Cos(_yaw)));
        _session.QueueCommandFrame(new PlayerCommandFrame(
            _sessionId,
            ++_sequence,
            mode,
            new PlayerMoveIntent(ForwardAxis: _walking ? 1f : 0f, StrafeAxis: 0f, JumpPressed: jump),
            new PlayerLookIntent(0f, 0f, facing),
            primaryUse));

        if (Chance(_mix.EquipActionsPerSecond, dt))
        {
            _session.QueuePlayerAction(new PlayerActionMessage(
                _sessionId,
                ++_actionSequence,
                new InventoryEquipIntent(_random.Next(0, 8))));
            ActionsQueued++;
        }
    }

    public void Drain()
    {
        while (_session.TryDequeueInbound(out IServerToClientMessage? message) && message != null)
        {
            MessagesReceived++;
            switch (message)
            {
                case ServerSnapshotMessage:
                    SnapshotsReceived++;
                    break;
                case ServerActionMessage:
                    ActionResultsReceived++;
                    break;
                case JoinAcceptedMessage:
                    JoinAccepted = true;
                    JoinMilliseconds ??= Stopwatch.GetElapsedTime(_connectStartedTimestamp).TotalMilliseconds;
                    break;
                case JoinRejectedMessage rejected:
                    JoinRejectedReason = rejected.Reason;
                    break;
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        return _session.DisposeAsync();
    }

    private bool Chance(double perSecond, double dt)
    {
        return perSecond > 0d && _random.NextDouble() < perSecond * dt;
    }

    private sealed class CountingClientTransport : IClientTransport
    {
        private readonly IClientTransport _inner;

        public CountingClientTransport(IClientTransport inner)
        {
            _inner = inner;
        }

        public CountingConnection? LastConnection { get; private set; }

        public async ValueTask<ITransportConnection> ConnectAsync(string host, int port, CancellationToken cancellationToken = default)
        {
            ITransportConnection connection = await _inner.ConnectAsync(host, port, cancellationToken);
            LastConnection = new CountingConnection(connection);
            return LastConnection;
        }
    }

    private sealed class CountingConnection : ITransportConnection
    {
        private readonly ITransportConnection _inner;
        private long _bytesSent;
        private long _bytesReceived;

        public CountingConnection(ITransportConnection inner)
        {
            _inner = inner;
        }

        public string ConnectionId => _inner.ConnectionId;

        public long BytesSent => Interlocked.Read(ref _bytesSent);

        public long BytesReceived => Interlocked.Read(ref _bytesReceived);

        public async ValueTask SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
        {
            await _inner.SendAsync(payload, cancellationToken);
            Interlocked.Add(ref _bytesSent, payload.Length);
        }

        public async ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int read = await _inner.ReceiveAsync(buffer, cancellationToken);
            if (read > 0)
            {
                Interlocked.Add(ref _bytesReceived, read);
            }

            return read;
        }

        public ValueTask DisposeAsync()
        {
            return _inner.DisposeAsync();
        }
    }
}
