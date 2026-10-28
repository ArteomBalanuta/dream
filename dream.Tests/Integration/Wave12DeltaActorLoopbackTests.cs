using System.Net;
using System.Net.Sockets;
using OpenTK.Mathematics;
using Xunit;
using Xunit.Abstractions;
using Dream.Client.Networking;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Multiplayer.Transport;
using Dream.Server;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Tests.Utilities;
using static Dream.Tests.Integration.Wave12DeltaActorLoopbackTests;

namespace Dream.Tests.Integration;

// Wave 12 tester, SCALE-2 (delta actor frames) under real conditions: the real
// DedicatedServerService and runtime-backed host over loopback TCP, ticked by the test one fixed
// step at a time, and the real RemoteClientSession (its ActorStateCache rebuilds full actors from
// the delta frames). After every tick the test records the server's actors; every snapshot a
// client delivers is compared with them: each shown actor within quantization (1/4096 m) and
// exact in every other field, and every server actor shown (none withheld as stale). Scenarios:
// a join in the middle of a keyframe interval, world resyncs interleaved with deltas, a slow link
// whose snapshots coalesce while a world transfer is going out, a NETR-8 takeover mid-chain, a
// player actor id reused by a new session between keyframes (the identity group must force a
// full record), enemy deaths and respawns and player damage and death, and a 2000-tick soak.
// The shared rig, the 2000-tick soak and the bandwidth measurement (both load tests); the
// scenarios below are split into classes of their own so they run in parallel (ARCH-1c: one
// class is one collection).
public sealed class Wave12DeltaActorLoopbackTests(ITestOutputHelper output)
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

    // 2000 ticks: walkers, a late joiner, a leaver who comes back, wolf kills, damage, a resync.
    // Every snapshot every client delivers equals the server's actors within quantization, nothing
    // is withheld, and a connection is forced onto a keyframe only for its join and its resync.
    [Dream.Tests.Load.LoadFact]
    public async Task Soak2000Ticks_ClientActorsEqualTheServersWithinQuantization()
    {
        await using Rig rig = await Rig.StartAsync(output);
        Random random = new(1212);
        List<Peer> peers = [await rig.JoinAsync("w12-k0"), await rig.JoinAsync("w12-k1"), await rig.JoinAsync("w12-k2")];
        Peer? leaver = null;
        bool resynced = false;
        while (rig.Tick < 2000)
        {
            ulong tick = rig.Tick;
            foreach (Peer peer in peers)
            {
                await peer.SendCommandAsync(walking: random.Next(4) != 0, yaw: (float)(random.NextDouble() * MathF.Tau));
            }

            if (tick == 333)
            {
                peers.Add(await rig.JoinAsync("w12-k3"));
            }

            if (tick == 700)
            {
                leaver = peers[1];
                peers.RemoveAt(1);
                await rig.DisconnectAsync(leaver);
            }

            if (tick == 900)
            {
                peers.Add(await rig.JoinAsync("w12-k1b"));
            }

            if (tick == 1200 && !resynced)
            {
                await Task.Delay(ServerRoutingContext.WorldResyncCooldown);
                peers[0].Session.QueueWorldResyncRequest(clientChecksum: 1);
                await rig.WaitUntilAsync(() => rig.Host.TryGetConnectionContext(peers[0].Connection.ConnectionId, out ServerRoutingContext? context) && context!.WorldResyncPending, "the resync request");
                resynced = true;
            }

            if (random.Next(120) == 0)
            {
                rig.KillAnEnemy();
            }

            if (random.Next(60) == 0)
            {
                rig.Runtime.Player.Combatant.ApplyDamage(1);
            }

            await rig.StepAsync();
            if (rig.Tick % 200 == 0)
            {
                await rig.VerifyAllAsync(except: leaver is null ? [] : [leaver]);
            }
        }

        await rig.VerifyAllAsync(except: leaver is null ? [] : [leaver]);
        rig.AssertNoChainBreaksOrWithheldActors(except: leaver is null ? [] : [leaver]);
        foreach (Peer peer in rig.Peers.Where(peer => peer != leaver))
        {
            long expected = 1 + (peer == peers[0] ? 1 : 0);
            output.WriteLine($"{peer.SessionId}: {peer.Received.Count} snapshots, keyframes applied {peer.Session.ActorStates.KeyframesApplied}, deltas {peer.Session.ActorStates.DeltaFramesApplied}, forced {peer.Connection.ActorKeyframesForced}, {peer.Connection.BytesSent / Math.Max(1, peer.Received.Count)} B/snapshot");
            Assert.True(peer.Connection.ActorKeyframesForced <= expected, $"{peer.SessionId}: {peer.Connection.ActorKeyframesForced} forced keyframes, expected at most {expected}");
        }

        output.WriteLine($"max transform error {rig.MaxTransformError:E2} m (bound {ActorQuantization.MaxError:E2})");
    }

    // Measurement: server-to-client bytes per second and forced keyframes with 8 and 32 walking
    // clients at the default snapshot rate (32 Hz), over 20 simulated seconds.
    // A measurement, so a load test (DREAM_LOAD_TEST=1).
    [Dream.Tests.Load.LoadFact]
    public Task Measure_BytesPerSecond_AndForcedKeyframes_8Clients() => MeasureAsync(8);

    [Dream.Tests.Load.LoadFact]
    public Task Measure_BytesPerSecond_AndForcedKeyframes_32Clients() => MeasureAsync(32);

    private async Task MeasureAsync(int clients)
    {
        await using Rig rig = await Rig.StartAsync(output, ServerConnectionOptions.Default, recordExpected: false);
        Random random = new(clients);
        List<Peer> peers = [];
        for (int i = 0; i < clients; i++)
        {
            peers.Add(await rig.JoinAsync($"w12-m{i:00}"));
        }

        long[] bytesBefore = peers.Select(peer => peer.Connection.BytesSent).ToArray();
        long[] forcedBefore = peers.Select(peer => peer.Connection.ActorKeyframesForced).ToArray();
        const int Ticks = 64 * 20;
        float[] yaw = peers.Select(_ => (float)(random.NextDouble() * MathF.Tau)).ToArray();
        for (int t = 0; t < Ticks; t++)
        {
            for (int i = 0; i < peers.Count; i++)
            {
                if (random.Next(32) == 0)
                {
                    yaw[i] = (float)(random.NextDouble() * MathF.Tau);
                }

                // Bots: mostly walking, some standing still.
                await peers[i].SendCommandAsync(walking: (t / 64 + i) % 4 != 0, yaw: yaw[i]);
            }

            await rig.StepAsync();
        }

        double seconds = Ticks / 64d;
        double[] perClient = peers.Select((peer, i) => (peer.Connection.BytesSent - bytesBefore[i]) / seconds).ToArray();
        long[] forced = peers.Select((peer, i) => peer.Connection.ActorKeyframesForced - forcedBefore[i]).ToArray();
        long[] coalesced = peers.Select(peer => peer.Connection.CoalescedSnapshots).ToArray();
        // For comparison, the snapshot of the final state as it went before SCALE-2: every actor in
        // full and every session's accepted sequence in the body.
        IReadOnlyList<ActorSnapshot> finalActors = rig.Actors();
        Dictionary<string, ulong> sequences = peers.ToDictionary(peer => peer.SessionId, peer => peer.CommandSequence);
        TransportJsonCodec codec = new();
        int legacyBytes = codec.Encode(
            new TransportEnvelope(TransportEnvelope.CurrentProtocolVersion, "server.snapshot", 1),
            new ServerSnapshotMessage(new ServerSnapshotEnvelope(rig.Tick, sequences, finalActors.ToArray(), PlayerInteractionMode.Combat, null, [], []))).Length;
        double legacyPerSecond = legacyBytes * 64d / ServerConnectionOptions.Default.SnapshotIntervalTicks;
        output.WriteLine($"legacy (full actors + full sequence map) snapshot of the final state: {legacyBytes} B, {legacyPerSecond / 1024:0.0} KB/s per client at the same rate");
        output.WriteLine(
            $"{clients} clients, {Ticks} ticks at {64 / ServerConnectionOptions.Default.SnapshotIntervalTicks} Hz snapshots: " +
            $"{perClient.Average() / 1024:0.0} KB/s per client (min {perClient.Min() / 1024:0.0}, max {perClient.Max() / 1024:0.0}); " +
            $"forced keyframes during the run: total {forced.Sum()}, max per connection {forced.Max()}; coalesced {coalesced.Sum()}");
        // Forced keyframes only for a missing base; with every snapshot delivered there is none.
        Assert.True(forced.Max() <= 1, $"forced keyframes per connection: {string.Join(",", forced)}");
        await rig.DrainAllAsync();
        rig.AssertNoChainBreaksOrWithheldActors();
    }


    internal sealed record Snapshot(ulong Tick, IReadOnlyList<ActorSnapshot> Shown, IReadOnlyList<ActorSnapshot> Expected, ActorWireFrame? Frame);

    internal sealed class Peer
    {
        private ulong _commandSequence;

        public Peer(string sessionId, RemoteClientSession session, ServerConnection connection, GatedReceiveConnection? link)
        {
            SessionId = sessionId;
            Session = session;
            Connection = connection;
            Link = link;
        }

        public string SessionId { get; }

        public RemoteClientSession Session { get; }

        public ServerConnection Connection { get; }

        public GatedReceiveConnection? Link { get; }

        // The server's end of this peer's connection (its sends can be slowed).
        public GatedReceiveConnection? ServerLink { get; init; }

        // Sees every message the client delivers, in order, as the rig drains them.
        public Action<IServerToClientMessage>? Observer { get; set; }

        public ulong JoinedAtTick { get; set; }

        public ulong CommandSequence => Volatile.Read(ref _commandSequence);

        public List<Snapshot> Received { get; } = [];

        public bool Departed { get; set; }

        public async Task SendCommandAsync(bool walking, float yaw)
        {
            ulong sequence = Interlocked.Increment(ref _commandSequence);
            Vector3 facing = new(MathF.Sin(yaw), 0f, MathF.Cos(yaw));
            await Session.SendCommandFrameAsync(new PlayerCommandFrame(
                new PlayerSessionId(SessionId),
                sequence,
                PlayerModeSwitchIntent.None,
                new PlayerMoveIntent(walking ? 1f : 0f, 0f),
                new PlayerLookIntent(yaw, 0f, facing),
                PrimaryUsePressed: false));
        }
    }

    // ReceiveAsync can be held (the client stops reading, like a stalled link); the socket's
    // receive buffer is tiny, so the server's writes back up quickly.
    internal sealed class GatedReceiveConnection(ITransportConnection inner) : ITransportConnection
    {
        private volatile TaskCompletionSource? _hold;
        private volatile TaskCompletionSource? _sendStall;

        public string ConnectionId => inner.ConnectionId;

        public bool SmallReads { get; init; }

        public void Hold() => _hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Resume() => _hold?.TrySetResult();

        public void StallSends() => _sendStall = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // A slow link: every send waits this long first (0 for none).
        public int SendDelayMilliseconds { get; set; }

        public async ValueTask SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
        {
            TaskCompletionSource? stall = _sendStall;
            if (stall != null)
            {
                await stall.Task.WaitAsync(cancellationToken);
            }

            int delay = SendDelayMilliseconds;
            if (delay > 0)
            {
                await Task.Delay(delay, cancellationToken);
            }

            await inner.SendAsync(payload, cancellationToken);
        }

        public async ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            TaskCompletionSource? hold = _hold;
            if (hold != null)
            {
                await hold.Task.WaitAsync(cancellationToken);
            }

            return await inner.ReceiveAsync(SmallReads && buffer.Length > 2048 ? buffer[..2048] : buffer, cancellationToken);
        }

        public ValueTask DisposeAsync()
        {
            Resume();
            _sendStall?.TrySetCanceled();
            return inner.DisposeAsync();
        }
    }

    // The server's listener, with every accepted connection's sends gated (the server-to-client
    // half of a slow link).
    private sealed class GatedServerTransport : IServerTransport
    {
        private readonly TcpServerTransport _inner = new(IPAddress.Loopback, 0);

        public int Port => _inner.Port;

        public GatedReceiveConnection? LastAccepted { get; private set; }

        public void Start() => _inner.Start();

        public async ValueTask<ITransportConnection> AcceptClientAsync(CancellationToken cancellationToken = default)
        {
            LastAccepted = new GatedReceiveConnection(await _inner.AcceptClientAsync(cancellationToken));
            return LastAccepted;
        }

        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }

    private sealed class GatedConnector(bool slowLink) : IClientTransport
    {
        public GatedReceiveConnection? Connection { get; private set; }

        public async ValueTask<ITransportConnection> ConnectAsync(string host, int port, CancellationToken cancellationToken = default)
        {
            TcpClient tcp = new() { NoDelay = true };
            if (slowLink)
            {
                tcp.ReceiveBufferSize = 4096;
            }

            await tcp.ConnectAsync(IPAddress.Loopback, port, cancellationToken);
            Connection = new GatedReceiveConnection(new TcpClientTransport(tcp)) { SmallReads = slowLink };
            return Connection;
        }
    }

    internal sealed class Rig : IAsyncDisposable
    {
        private readonly ITestOutputHelper _output;
        private readonly bool _recordExpected;
        private readonly Dictionary<ulong, ActorSnapshot[]> _expected = [];
        private readonly List<Peer> _peers = [];

        private readonly GatedServerTransport _transport;

        private Rig(DedicatedServerHost host, DedicatedServerService service, GatedServerTransport transport, ITestOutputHelper output, bool recordExpected)
        {
            Host = host;
            Service = service;
            _transport = transport;
            _output = output;
            _recordExpected = recordExpected;
        }

        public DedicatedServerHost Host { get; }

        public DedicatedServerService Service { get; }

        public WorldSessionRuntime Runtime => Host.Runtime!;

        public ulong Tick => Runtime.CurrentServerTick;

        public IReadOnlyList<Peer> Peers => _peers;

        public float MaxTransformError { get; private set; }

        public static Task<Rig> StartAsync(ITestOutputHelper output, ServerConnectionOptions? options = null, bool recordExpected = true, DedicatedServerHost? host = null)
        {
            // The production host shape (ServerBootstrap.CreateAuthoritativeHost): joins deferred to the tick.
            host ??= DedicatedServerHost.CreateRuntimeAttached(persistence: null, deferJoinsToTick: true);
            GatedServerTransport transport = new();
            DedicatedServerService service = new(
                host,
                transport,
                new TransportJsonCodec(),
                new LengthPrefixedMessageFramer(),
                options ?? PerTickSnapshots.Options);
            service.Start();
            return Task.FromResult(new Rig(host, service, transport, output, recordExpected));
        }

        public IReadOnlyList<ActorSnapshot> Actors() => Runtime.CaptureActorSnapshots();

        public IEnumerable<ActorSnapshot[]> AllExpected() => _expected.Values;

        public async Task<Peer> JoinAsync(string sessionId, bool slowLink = false, bool gated = false)
        {
            Peer peer = await ConnectWithoutJoiningAsync(sessionId, slowLink, gated);
            await WaitUntilJoinedAsync(peer);
            return peer;
        }

        public async Task<Peer> ConnectWithoutJoiningAsync(string sessionId, bool slowLink = false, bool gated = false)
        {
            Task<ServerConnection> accept = Service.AcceptOnThreadPoolAsync();
            GatedConnector? connector = slowLink || gated ? new GatedConnector(slowLink) : null;
            RemoteClientSession session = await RemoteClientSession.ConnectAsync(
                new RemoteClientOptions("127.0.0.1", Service.Port, sessionId, $"Name {sessionId}", "w12-test"),
                connector).AsTask().WaitAsync(Deadline);
            ServerConnection connection = await accept.WaitAsync(Deadline);
            Peer peer = new(sessionId, session, connection, connector?.Connection) { ServerLink = _transport.LastAccepted };
            _peers.Add(peer);
            // The join request is queued for the next tick (deferred join); wait until it is.
            await WaitUntilAsync(() => IsJoinPending(connection.ConnectionId) || Host.IsConnectionJoined(connection.ConnectionId), $"{sessionId}'s join request to be queued or applied");
            return peer;
        }

        private bool IsJoinPending(string connectionId)
        {
            lock (HostSync)
            {
                System.Collections.IEnumerable pending = (System.Collections.IEnumerable)typeof(DedicatedServerHost)
                    .GetField("_pendingJoins", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                    .GetValue(Host)!;
                foreach (object join in pending)
                {
                    if ((string)join.GetType().GetProperty("ConnectionId")!.GetValue(join)! == connectionId)
                    {
                        return true;
                    }
                }

                return false;
            }
        }

        private object HostSync => typeof(DedicatedServerHost)
            .GetField("_authoritySync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(Host)!;

        public async Task WaitUntilJoinedAsync(Peer peer)
        {
            DateTime deadline = DateTime.UtcNow + Deadline;
            while (!(peer.Session.IsJoined && Host.IsConnectionJoinedAndSeated(peer)))
            {
                Assert.True(DateTime.UtcNow < deadline, $"{peer.SessionId} never joined");
                Assert.True(peer.Session.IsConnected, $"{peer.SessionId} disconnected: {peer.Session.DisconnectReason}");
                if (Host.IsConnectionJoinedAndSeated(peer))
                {
                    // Applied; the JoinAccepted (and its world) is on its way.
                    await Task.Delay(2);
                    continue;
                }

                await StepAsync();
            }

            peer.JoinedAtTick = Tick;
        }

        public async Task DisconnectAsync(Peer peer)
        {
            int before = Service.ConnectionCount;
            await peer.Session.DisposeAsync();
            peer.Departed = true;
            // The rig keeps no reference to a departed client (the churn test measures memory).
            _peers.Remove(peer);
            await WaitUntilAsync(
                () => Service.ConnectionCount < before && !Host.IsConnectionJoined(peer.Connection.ConnectionId)
                    && !Runtime.Sessions.Players.Any(player => player.SessionId.Value == peer.SessionId && player.IsSpawned),
                $"{peer.SessionId} to leave");
        }

        public void KillAnEnemy()
        {
            if (Runtime.Enemies.ActiveActors.FirstOrDefault(enemy => enemy.Combatant.Health.IsAlive) is { } enemy)
            {
                enemy.Combatant.ApplyDamage(1_000_000);
            }
        }

        // One fixed tick (after the server has read every command the clients sent), then the
        // server's actors for that tick are recorded and every writer drains.
        public async Task StepAsync(bool flush = true, Peer? silent = null)
        {
            foreach (Peer peer in _peers)
            {
                if (peer.Departed || peer.CommandSequence == 0 || peer == silent)
                {
                    continue;
                }

                await WaitUntilAsync(
                    () => peer.Connection.IsFaulted
                        || !Host.TryGetConnectionContext(peer.Connection.ConnectionId, out ServerRoutingContext? context)
                        || context?.LastCommandMessage?.CommandFrame.Sequence >= peer.CommandSequence,
                    $"the server to read {peer.SessionId}'s command {peer.CommandSequence}");
            }

            ulong before = Tick;
            int executed = await Service.AdvanceOnceAsync(SimulationClock.FixedDeltaSeconds);
            if (executed == 0)
            {
                executed = await Service.AdvanceOnceAsync(SimulationClock.FixedDeltaSeconds * 0.01);
            }

            Assert.Equal(1, executed);
            Assert.Equal(before + 1, Tick);
            if (_recordExpected)
            {
                _expected[Tick] = [.. Runtime.CaptureActorSnapshots()];
            }

            if (flush)
            {
                await FlushAsync();
            }
        }

        public async Task FlushAsync(params Peer[] except)
        {
            foreach (Peer peer in _peers)
            {
                if (!peer.Departed && !peer.Connection.IsFaulted && Array.IndexOf(except, peer) < 0)
                {
                    Assert.True(await OutboundDrain.FlushAsync(peer.Connection, Deadline), $"{peer.SessionId}'s writer did not drain.");
                }
            }
        }

        // Snapshots go out every SnapshotIntervalTicks, so the last one may be a tick behind.
        public async Task DrainAllAsync()
        {
            foreach (Peer peer in _peers.Where(peer => !peer.Departed))
            {
                await DrainAsync(peer, Tick - 1);
            }
        }

        // Takes every snapshot the client delivered up to tick and compares it with the server's.
        public async Task VerifyAllAsync(params Peer[] except)
        {
            foreach (Peer peer in _peers)
            {
                if (peer.Departed || Array.IndexOf(except, peer) >= 0)
                {
                    continue;
                }

                await DrainAsync(peer, Tick);
            }
        }

        private async Task DrainAsync(Peer peer, ulong tick)
        {
            DateTime deadline = DateTime.UtcNow + Deadline;
            while (peer.Received.Count == 0 || peer.Received[^1].Tick < tick)
            {
                if (!peer.Session.TryDequeueInbound(out IServerToClientMessage? message))
                {
                    Assert.True(peer.Session.IsConnected, $"{peer.SessionId} disconnected: {peer.Session.DisconnectReason}");
                    Assert.True(DateTime.UtcNow < deadline, $"{peer.SessionId} never delivered the snapshot of tick {tick} (last {(peer.Received.Count == 0 ? 0 : peer.Received[^1].Tick)}).");
                    await Task.Delay(2);
                    continue;
                }

                peer.Observer?.Invoke(message!);
                if (message is not ServerSnapshotMessage snapshot)
                {
                    continue;
                }

                ServerSnapshotEnvelope envelope = snapshot.Snapshot;
                ActorSnapshot[] expected = _recordExpected ? _expected[envelope.ServerTick] : [];
                IReadOnlyList<ActorSnapshot> shown = envelope.Actors ?? [];
                peer.Received.Add(new Snapshot(envelope.ServerTick, shown, expected, envelope.ActorFrame));
                if (_recordExpected)
                {
                    Compare(peer, envelope.ServerTick, expected, shown);
                }
            }
        }

        private void Compare(Peer peer, ulong tick, ActorSnapshot[] expected, IReadOnlyList<ActorSnapshot> shown)
        {
            Dictionary<int, ActorSnapshot> server = expected.ToDictionary(actor => actor.ActorId);
            Assert.True(
                shown.Count == expected.Length,
                $"{peer.SessionId} tick {tick}: shows {shown.Count} actors, the server has {expected.Length} " +
                $"(missing {string.Join(",", server.Keys.Except(shown.Select(actor => actor.ActorId)))}; stale now {peer.Session.ActorStates.StaleActorCount})");
            foreach (ActorSnapshot actor in shown)
            {
                Assert.True(server.TryGetValue(actor.ActorId, out ActorSnapshot truth), $"{peer.SessionId} tick {tick}: actor {actor.ActorId} is not on the server");
                string where = $"{peer.SessionId} tick {tick} actor {actor.ActorId}";
                Assert.True(truth.Kind == actor.Kind, where + " kind");
                Assert.True(truth.SessionId == actor.SessionId, $"{where} session {actor.SessionId} != {truth.SessionId}");
                Assert.True(truth.DisplayName == actor.DisplayName, $"{where} name {actor.DisplayName} != {truth.DisplayName}");
                Assert.True(truth.CurrentHealth == actor.CurrentHealth && truth.MaxHealth == actor.MaxHealth && truth.IsAlive == actor.IsAlive, $"{where} health {actor.CurrentHealth}/{actor.MaxHealth}/{actor.IsAlive} != {truth.CurrentHealth}/{truth.MaxHealth}/{truth.IsAlive}");
                Assert.True(truth.InteractionMode == actor.InteractionMode && truth.EnemyBehaviorState == actor.EnemyBehaviorState, $"{where} mode");
                Assert.True(truth.CombatStats == actor.CombatStats, $"{where} combat");
                Assert.True(truth.Progression == actor.Progression, $"{where} progression");
                Assert.True(truth.StarvedTicks == actor.StarvedTicks, $"{where} starved {actor.StarvedTicks} != {truth.StarvedTicks}");
                float error = MathF.Max(MaxComponent(truth.Position - actor.Position), MaxComponent(truth.Facing - actor.Facing));
                MaxTransformError = MathF.Max(MaxTransformError, error);
                Assert.True(error <= ActorQuantization.MaxError * 1.01f + 1e-6f, $"{where} transform off by {error} (position {actor.Position} vs {truth.Position})");
            }
        }

        private static float MaxComponent(Vector3 v) => MathF.Max(MathF.Abs(v.X), MathF.Max(MathF.Abs(v.Y), MathF.Abs(v.Z)));

        public void AssertNoChainBreaksOrWithheldActors(params Peer[] except)
        {
            foreach (Peer peer in _peers)
            {
                if (peer.Departed || Array.IndexOf(except, peer) >= 0)
                {
                    continue;
                }

                Assert.True(peer.Session.ActorStates.ChainBreaks == 0, $"{peer.SessionId}: {peer.Session.ActorStates.ChainBreaks} chain breaks");
                Assert.True(peer.Session.ActorStates.StaleActorsWithheld == 0, $"{peer.SessionId}: {peer.Session.ActorStates.StaleActorsWithheld} actors withheld");
            }
        }

        public async Task WaitUntilAsync(Func<bool> condition, string what)
        {
            DateTime deadline = DateTime.UtcNow + Deadline;
            SpinWait spinner = default;
            while (!condition())
            {
                Assert.True(DateTime.UtcNow < deadline, $"Timed out waiting for {what}.");
                if (spinner.NextSpinWillYield)
                {
                    await Task.Delay(1);
                }

                spinner.SpinOnce(sleep1Threshold: -1);
            }
        }

        public async ValueTask DisposeAsync()
        {
            foreach (Peer peer in _peers)
            {
                await peer.Session.DisposeAsync();
            }

            await Service.DisposeAsync();
            Host.Dispose();
        }
    }
}

internal static class Wave12RigHostExtensions
{
    // Joined (the host answered the join) and seated in the runtime.
    public static bool IsConnectionJoinedAndSeated(this DedicatedServerHost host, Wave12DeltaActorLoopbackTests.Peer peer)
    {
        return host.IsConnectionJoined(peer.Connection.ConnectionId)
            && host.Runtime!.Sessions.TryGet(new PlayerSessionId(peer.SessionId), out ConnectedPlayer? player)
            && player!.IsSpawned;
    }
}

// Joins between keyframes, actor id reuse, deaths and respawns (see Wave12DeltaActorLoopbackTests).
public sealed class Wave12DeltaActorJoinTests(ITestOutputHelper output)
{
    // A join between keyframes: the joiner's first snapshot is the payload's keyframe twin, so it
    // shows every actor at once; the connection that was already there keeps getting deltas.
    [Fact]
    public async Task JoinInTheMiddleOfAKeyframeInterval_ShowsEveryActorInTheFirstSnapshot()
    {
        await using Rig rig = await Rig.StartAsync(output);
        Peer a = await rig.JoinAsync("w12-a");
        // Run to the middle of an interval, A walking and turning.
        while (rig.Tick % ActorDeltaContext.KeyframeIntervalTicks != 29)
        {
            await a.SendCommandAsync(walking: true, yaw: rig.Tick * 0.05f);
            await rig.StepAsync();
        }

        long forcedOnA = a.Connection.ActorKeyframesForced;
        Peer b = await rig.JoinAsync("w12-b");
        for (int i = 0; i < 80; i++)
        {
            await a.SendCommandAsync(walking: i % 3 != 0, yaw: i * 0.07f);
            await b.SendCommandAsync(walking: i % 2 == 0, yaw: -i * 0.03f);
            await rig.StepAsync();
        }

        await rig.VerifyAllAsync();
        Snapshot first = b.Received[0];
        Assert.NotNull(first.Frame);
        Assert.True(first.Frame!.IsKeyframe, $"B's first snapshot (tick {first.Tick}) was a delta.");
        Assert.NotEqual(0ul, first.Tick % ActorDeltaContext.KeyframeIntervalTicks);
        Assert.Equal(first.Expected.Count, first.Shown.Count);
        // A held every base: B's join forced nothing on A.
        Assert.Equal(forcedOnA, a.Connection.ActorKeyframesForced);
        rig.AssertNoChainBreaksOrWithheldActors();
        output.WriteLine($"B joined at tick {b.JoinedAtTick}, first snapshot {first.Tick} a keyframe of {first.Shown.Count} actors; forced A={a.Connection.ActorKeyframesForced} B={b.Connection.ActorKeyframesForced}");
    }

    // Wave 10 id reuse: X leaves and Y joins within one tick, between keyframes, and Y is given
    // X's released actor id. In the delta frame the id's base is X; the identity group (session id,
    // name) differs, so the record must be full and the observer must show Y, never X's name or
    // stale state under Y's id.
    [Fact]
    public async Task ActorIdReusedByANewSessionBetweenKeyframes_IsAFullRecord()
    {
        await using Rig rig = await Rig.StartAsync(output);
        Peer observer = await rig.JoinAsync("w12-io");
        Peer anchor = await rig.JoinAsync("w12-ib");
        Peer x = await rig.JoinAsync("w12-ix");
        for (int i = 0; i < 10; i++)
        {
            await x.SendCommandAsync(walking: true, yaw: 0.3f);
            await rig.StepAsync();
        }

        while (rig.Tick % ActorDeltaContext.KeyframeIntervalTicks is < 10 or > 50)
        {
            await rig.StepAsync();
        }

        int xActor = rig.Actors().Single(actor => actor.SessionId == "w12-ix").ActorId;
        // Y's join is queued (not applied until a tick), then X leaves; one tick applies both.
        Peer y = await rig.ConnectWithoutJoiningAsync("w12-iy");
        await rig.DisconnectAsync(x);
        ulong reuseTick = rig.Tick + 1;
        await rig.StepAsync();
        await rig.WaitUntilJoinedAsync(y);
        for (int i = 0; i < 20; i++)
        {
            await y.SendCommandAsync(walking: true, yaw: -0.4f);
            await rig.StepAsync();
        }

        await rig.VerifyAllAsync(except: x);
        ActorSnapshot yActor = rig.Actors().Single(actor => actor.SessionId == "w12-iy");
        output.WriteLine($"X had actor {xActor}; Y joined at tick {y.JoinedAtTick} with actor {yActor.ActorId}");
        Assert.Equal(xActor, yActor.ActorId);
        Snapshot atReuse = observer.Received.Single(snapshot => snapshot.Tick == reuseTick);
        ActorSnapshot shown = atReuse.Shown.Single(actor => actor.ActorId == xActor);
        Assert.Equal("w12-iy", shown.SessionId);
        Assert.NotNull(atReuse.Frame);
        Assert.False(atReuse.Frame!.IsKeyframe, "The reuse landed on a keyframe; the test needs a delta frame.");
        ActorWireRecord record = atReuse.Frame.Records.Single(r => r.ActorId == xActor);
        Assert.True(record.IsFull, $"Actor {xActor}'s record at the reuse was a delta ({record.Groups}).");
        rig.AssertNoChainBreaksOrWithheldActors(except: x);
    }

    // Deaths and respawns: wolves killed between ticks (each respawn takes a fresh actor id and
    // arrives as a full record in a delta frame), the primary player damaged and killed (health
    // group). Every snapshot shows exactly the server's actors.
    [Fact]
    public async Task EnemyDeathsAndRespawns_AndPlayerDeath_AreShownOnTheNextSnapshot()
    {
        await using Rig rig = await Rig.StartAsync(output);
        Peer a = await rig.JoinAsync("w12-da");
        Peer b = await rig.JoinAsync("w12-db");
        HashSet<int> enemyIds = [];
        for (int i = 0; i < 400; i++)
        {
            await a.SendCommandAsync(walking: i % 3 == 0, yaw: i * 0.02f);
            await b.SendCommandAsync(walking: true, yaw: -i * 0.03f);
            if (i % 40 == 13)
            {
                rig.KillAnEnemy();
            }

            if (i % 25 == 7)
            {
                rig.Runtime.Player.Combatant.ApplyDamage(7);
            }

            await rig.StepAsync();
            foreach (ActorSnapshot actor in rig.Actors().Where(actor => actor.SessionId == null))
            {
                enemyIds.Add(actor.ActorId);
            }
        }

        await rig.VerifyAllAsync();
        Assert.True(enemyIds.Count >= 8, $"Only {enemyIds.Count} distinct enemy ids: the respawns did not happen.");
        Assert.Contains(rig.AllExpected(), actors => actors.Any(actor => actor.SessionId != null && !actor.IsAlive));
        output.WriteLine($"{enemyIds.Count} enemy lives; forced keyframes A={a.Connection.ActorKeyframesForced} B={b.Connection.ActorKeyframesForced}");
        rig.AssertNoChainBreaksOrWithheldActors();
    }
}

// World resyncs interleaved with deltas (see Wave12DeltaActorLoopbackTests).
public sealed class Wave12DeltaActorResyncTests(ITestOutputHelper output)
{
    // World resyncs (a snapshot off the chain, sent as a world transfer's carrier) interleaved with
    // deltas: the client's cache drops the chain at the resync and the server sends the next frame
    // as a keyframe, so nothing is ever withheld.
    [Fact]
    public async Task ResyncsInterleavedWithDeltas_NeverBreakTheChain()
    {
        await using Rig rig = await Rig.StartAsync(output);
        Peer a = await rig.JoinAsync("w12-ra");
        Peer b = await rig.JoinAsync("w12-rb");
        const int Resyncs = 3;
        int requested = 0;
        for (int i = 0; i < 300; i++)
        {
            await a.SendCommandAsync(walking: true, yaw: i * 0.04f);
            await b.SendCommandAsync(walking: i % 5 != 0, yaw: -i * 0.02f);
            if (i % 90 == 37 && requested < Resyncs)
            {
                // The server's resync cooldown is wall-clock (1.5 s).
                await Task.Delay(ServerRoutingContext.WorldResyncCooldown + TimeSpan.FromMilliseconds(100));
                b.Session.QueueWorldResyncRequest(clientChecksum: 0xBADu);
                await rig.WaitUntilAsync(() => rig.Host.TryGetConnectionContext(b.Connection.ConnectionId, out ServerRoutingContext? context) && context!.WorldResyncPending, "the resync request to arrive");
                requested++;
            }

            await rig.StepAsync();
        }

        await rig.VerifyAllAsync();
        int resyncSnapshots = b.Received.Count(snapshot => snapshot.Frame == null);
        output.WriteLine($"{resyncSnapshots} frame-less (resync) snapshots, transfers {b.Connection.WorldTransfers}; forced keyframes B={b.Connection.ActorKeyframesForced} A={a.Connection.ActorKeyframesForced}");
        Assert.Equal(Resyncs, resyncSnapshots);
        // After every resync the next delta is a forced keyframe, never a chain break.
        Assert.True(b.Connection.ActorKeyframesForced >= Resyncs + 1, $"{b.Connection.ActorKeyframesForced} forced keyframes");
        Assert.True(b.Connection.ActorKeyframesForced <= Resyncs + 2, $"{b.Connection.ActorKeyframesForced} forced keyframes");
        rig.AssertNoChainBreaksOrWithheldActors();
    }
}

// A slow link with coalescing and a world transfer (see Wave12DeltaActorLoopbackTests).
public sealed class Wave12DeltaActorSlowLinkTests(ITestOutputHelper output)
{
    // A slow link: every server write to B takes 30 ms, right after B asked for a world resync.
    // The server's writer falls behind, snapshots merge (sent as keyframes), the world transfer
    // goes out in chunks between them, and B never withholds an actor or shows a stale one. Then
    // the link recovers.
    [Fact]
    public async Task SlowLink_CoalescedSnapshotsAndAWorldTransfer_WhileActorsChange()
    {
        await using Rig rig = await Rig.StartAsync(output);
        Peer a = await rig.JoinAsync("w12-sa");
        Peer b = await rig.JoinAsync("w12-sb");
        for (int i = 0; i < 20; i++)
        {
            await a.SendCommandAsync(walking: true, yaw: i * 0.1f);
            await b.SendCommandAsync(walking: true, yaw: -i * 0.1f);
            await rig.StepAsync();
        }

        await Task.Delay(ServerRoutingContext.WorldResyncCooldown + TimeSpan.FromMilliseconds(100));
        b.Session.QueueWorldResyncRequest(clientChecksum: 0xBADu);
        await rig.WaitUntilAsync(() => rig.Host.TryGetConnectionContext(b.Connection.ConnectionId, out ServerRoutingContext? context) && context!.WorldResyncPending, "the resync request to arrive");
        b.ServerLink!.SendDelayMilliseconds = 30;
        System.Diagnostics.Stopwatch held = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 300; i++)
        {
            await a.SendCommandAsync(walking: i % 7 != 0, yaw: i * 0.09f);
            await b.SendCommandAsync(walking: i % 3 != 0, yaw: i * 0.05f);
            if (i % 50 == 25)
            {
                rig.KillAnEnemy();
            }

            await rig.StepAsync(flush: false);
            await rig.FlushAsync(except: b);
        }

        long coalesced = b.Connection.CoalescedSnapshots;
        ulong heldTicks = rig.Tick;
        b.ServerLink.SendDelayMilliseconds = 0;
        for (int i = 0; i < 150; i++)
        {
            await a.SendCommandAsync(walking: true, yaw: -i * 0.05f);
            await rig.StepAsync();
        }

        await rig.VerifyAllAsync();
        output.WriteLine(
            $"held {held.Elapsed.TotalSeconds:0.0} s to tick {heldTicks}; coalesced {coalesced} (now {b.Connection.CoalescedSnapshots}), transfers {b.Connection.WorldTransfers}, chunks {b.Connection.WorldChunksSent}, " +
            $"forced keyframes B={b.Connection.ActorKeyframesForced}, B received {b.Received.Count} snapshots");
        Assert.True(b.Connection.CoalescedSnapshots > 0, "The slow link never made the server merge snapshots.");
        Assert.True(b.Session.WorldTransfersCompleted >= 1);
        rig.AssertNoChainBreaksOrWithheldActors();
        // The last snapshot matches the server.
        Assert.Equal(rig.Tick, b.Received[^1].Tick);
    }
}

// A NETR-8 takeover mid-chain (see Wave12DeltaActorLoopbackTests).
public sealed class Wave12DeltaActorTakeoverTests(ITestOutputHelper output)
{
    // NETR-8 takeover of a connection mid-chain: B's first connection goes silent (its sends stall,
    // it still receives) for TakeoverSilence, and B reconnects with the same session id and name.
    // The new connection's first snapshot is a keyframe; the observer's chain is untouched.
    [Fact]
    public async Task TakeoverMidChain_NewConnectionStartsOnAKeyframe_ObserverUnaffected()
    {
        await using Rig rig = await Rig.StartAsync(output);
        Peer observer = await rig.JoinAsync("w12-to");
        Peer b = await rig.JoinAsync("w12-tb", gated: true);
        for (int i = 0; i < 40; i++)
        {
            await observer.SendCommandAsync(walking: true, yaw: i * 0.05f);
            await b.SendCommandAsync(walking: true, yaw: -i * 0.05f);
            await rig.StepAsync();
        }

        long observerForced = observer.Connection.ActorKeyframesForced;
        int actorBefore = rig.Actors().Single(actor => actor.SessionId == "w12-tb").ActorId;
        b.Link!.StallSends();
        // The server keeps ticking (and sending) at about real time while B is silent.
        DateTime silentUntil = DateTime.UtcNow + DedicatedServerHost.TakeoverSilence + TimeSpan.FromMilliseconds(500);
        while (DateTime.UtcNow < silentUntil)
        {
            await observer.SendCommandAsync(walking: true, yaw: rig.Tick * 0.01f);
            await rig.StepAsync(silent: b);
            await Task.Delay(15);
        }

        Peer b2 = await rig.JoinAsync("w12-tb");
        b.Departed = true;
        await rig.WaitUntilAsync(() => !rig.Host.TryGetConnectionContext(b.Connection.ConnectionId, out _), "the old connection to be released");
        for (int i = 0; i < 100; i++)
        {
            await observer.SendCommandAsync(walking: i % 4 != 0, yaw: i * 0.05f);
            await b2.SendCommandAsync(walking: true, yaw: i * 0.08f);
            await rig.StepAsync();
        }

        await rig.VerifyAllAsync(except: b);
        Snapshot first = b2.Received[0];
        Assert.True(first.Frame?.IsKeyframe ?? true, "The takeover's first snapshot was a delta.");
        Assert.Equal(observerForced, observer.Connection.ActorKeyframesForced);
        Assert.Equal(1, rig.Actors().Count(actor => actor.SessionId == "w12-tb"));
        output.WriteLine($"takeover at tick {b2.JoinedAtTick}: actor {actorBefore} -> {rig.Actors().Single(actor => actor.SessionId == "w12-tb").ActorId}; first snapshot keyframe={first.Frame?.IsKeyframe}");
        rig.AssertNoChainBreaksOrWithheldActors(except: b);
    }
}
