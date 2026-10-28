using System.Diagnostics;
using OpenTK.Mathematics;
using Xunit;
using Xunit.Abstractions;
using Dream.Client.Networking;
using Dream.Client.Replication;
using Dream.Client.Startup;
using Dream.Client.World;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Multiplayer.Transport;
using Dream.Server;
using Dream.Simulation.Combat;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;

namespace Dream.Tests.Integration;

// Wave 8 tester: NETR-4 (client sends one command per prediction tick, outbound FIFO of 16 that
// merges on overflow) combined with NETR-5 (server BufferedPlayerCommandInbox, ring of 8). Real
// dedicated server over loopback, headless client running PlayScene's per-tick loop.
//
// Measured (Debug, loopback, wave 8 tip e4c6930, three runs each):
// - 1 s send stall: 4.530 m snap (1 snap), every run. The queue folds 47 stall commands into one frame
//   whose sequence jumps by 48. The buffered inbox enqueues that frame as ONE slot standing for ONE
//   tick (Slot.Ticks = 1, BufferedPlayerCommandInbox.cs:229); on the first pop SkipRepeatedTicks
//   skips 16 leading ticks (the runtime repeated the walk 16 ticks, then idled 48), and the frame
//   pops with its full jumped sequence (line 253). The ack covers 64 client walk ticks after the
//   server walked 17 of them: 47 ticks (~4.5 m) behind, so the reconciler snaps.
// - 1 s frame stall: max 0.834 m (smoothed, no snap); the client drops all but 8 catch-up ticks.
// - Hitchy loop (30-120 ms hitches, every 150-400 ms): max 0.741 m, no snap.
public class Wave8CommandStallCorrectionLoopbackTests(ITestOutputHelper output)
{
    private const int WalkTicksBeforeStall = 32;
    private const int StallTicks = SimulationClock.AuthoritativeTicksPerSecond;
    private const int TicksAfterStall = 96;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(40);

    // Current behaviour: the send stall ends in a snap. Asserted so a change in either direction is
    // noticed; the bug test below holds the target.
    [Fact]
    public async Task SendStall_OneSecond_CurrentlySnapsBecauseTheMergedCommandCountsAsOneServerTick()
    {
        StallReport report = await RunAsync(Scenario.SendStall, "w8-send-a");
        output.WriteLine(report.Describe("1 s send stall"));

        Assert.True(report.MergedCommands > 0, "The stall never overflowed the outbound queue.");
        Assert.True(report.LargestSequenceJump > 16, $"largest wire sequence jump {report.LargestSequenceJump}");
        // Measured 4.530 m in three of three runs: 47 walk ticks the server never simulated.
        Assert.True(report.MaxCorrectionAfter >= ClientPredictionReconciler.DefaultSnapDistance, report.Describe("send"));
        Assert.True(report.SnapsAfter >= 1, report.Describe("send"));
    }

    [Fact(Skip = "bug: NETR-4 x NETR-5 send stall. The client FIFO (16) folds the stall into one frame with a sequence jump; " +
                 "BufferedPlayerCommandInbox enqueues it as one tick (Slot.Ticks = 1) and acks the jump after 16 skipped + 1 simulated tick, so the server is 47 walk ticks " +
                 "behind and the client snaps 4.53 m. Fix: client predicts (and sends) idle once its oldest unsent command is 16 ticks old; see Wave8SendStallInboxModelTests. Target: a 1 s send stall corrects by less than DefaultSnapDistance.")]
    public async Task SendStall_OneSecond_TargetCorrectsBelowSnapDistance()
    {
        StallReport report = await RunAsync(Scenario.SendStall, "w8-send-b");
        output.WriteLine(report.Describe("1 s send stall"));
        Assert.True(report.MaxCorrectionAfter < ClientPredictionReconciler.DefaultSnapDistance, report.Describe("send"));
        Assert.Equal(0, report.SnapsAfter);
    }

    [Fact]
    public async Task FrameStall_OneSecond_CorrectsBelowSnapDistance()
    {
        StallReport report = await RunAsync(Scenario.FrameStall, "w8-frame");
        output.WriteLine(report.Describe("1 s frame stall"));

        Assert.True(report.DroppedTicks > 0, "The frame stall never hit the catch-up cap.");
        Assert.Equal(0, report.LargestSequenceJump > 1 ? 1 : 0);
        Assert.True(report.MaxCorrectionAfter < ClientPredictionReconciler.DefaultSnapDistance, report.Describe("frame"));
    }

    [Fact]
    public async Task HitchyLoop_SteadyWalk_CorrectsBelowSnapDistance()
    {
        StallReport report = await RunAsync(Scenario.Hitchy, "w8-hitchy");
        output.WriteLine(report.Describe("hitchy loop"));

        Assert.True(report.MaxCorrectionAfter < ClientPredictionReconciler.DefaultSnapDistance, report.Describe("hitchy"));
        Assert.Equal(0, report.SnapsAfter);
    }

    private enum Scenario
    {
        SendStall,
        FrameStall,
        Hitchy
    }

    private async Task<StallReport> RunAsync(Scenario scenario, string sessionId)
    {
        DedicatedServerHost? host = null;
        await using IntegratedLocalServerHandle server = await IntegratedLocalServerHandle.StartAsync(
            0,
            () => host = DedicatedServerHost.CreateRuntimeAttached(persistence: null));
        Assert.True(server.IsReady, server.ErrorMessage);
        int floorSize = host!.Runtime!.Map.Width;
        Vector3 facing = MostOpenFacing(floorSize, sessionId);

        StallableTransport transport = new();
        RemoteClientSession session = await RemoteClientSession.ConnectAsync(
            new RemoteClientOptions("127.0.0.1", server.BoundPort, sessionId, sessionId, "stall-test"),
            transport);
        await using (session)
        {
            using HeadlessClient client = new(session, floorSize, facing);
            Random random = new(1234);
            Stopwatch clock = Stopwatch.StartNew();
            double lastSeconds = 0d;
            ulong? walkStartTick = null;
            bool stalled = false;
            bool stallDone = false;
            double nextHitchSeconds = 0d;
            while (clock.Elapsed < Timeout)
            {
                double now = clock.Elapsed.TotalSeconds;
                double elapsed = now - lastSeconds;
                lastSeconds = now;
                client.Drain();
                if (client.SelfSeen)
                {
                    walkStartTick ??= client.Ticks.LastTick;
                    ulong walked = client.Ticks.LastTick - walkStartTick.Value;
                    if (!stalled && walked >= WalkTicksBeforeStall)
                    {
                        stalled = true;
                        client.StallStartTick = client.Ticks.LastTick;
                        client.ClientWalkStart = client.Position;
                        switch (scenario)
                        {
                            case Scenario.FrameStall:
                                await Task.Delay(TimeSpan.FromSeconds(1));
                                break;
                            case Scenario.SendStall:
                                transport.Connection!.Stall();
                                break;
                            case Scenario.Hitchy:
                                stallDone = true;
                                client.StallEndTick = client.Ticks.LastTick;
                                nextHitchSeconds = now + 0.1d;
                                break;
                        }
                    }

                    if (scenario == Scenario.SendStall && stalled && !stallDone && walked >= WalkTicksBeforeStall + StallTicks)
                    {
                        transport.Connection!.Resume();
                        stallDone = true;
                        client.StallEndTick = client.Ticks.LastTick + 1;
                    }

                    client.Frame(elapsed);
                    if (scenario == Scenario.FrameStall && stalled && !stallDone)
                    {
                        stallDone = true;
                        client.StallEndTick = client.StallStartTick + 1;
                    }

                    int after = scenario == Scenario.Hitchy ? TicksAfterStall * 3 : TicksAfterStall;
                    if (stallDone && client.Ticks.LastTick >= client.StallEndTick + (ulong)after)
                    {
                        break;
                    }

                    if (scenario == Scenario.Hitchy && stallDone && now >= nextHitchSeconds)
                    {
                        // A hitch under the 8-tick (125 ms) catch-up cap every ~150-400 ms.
                        await Task.Delay(TimeSpan.FromMilliseconds(30 + random.Next(90)));
                        nextHitchSeconds = clock.Elapsed.TotalSeconds + 0.15d + random.NextDouble() * 0.25d;
                        continue;
                    }
                }
                else
                {
                    lastSeconds = clock.Elapsed.TotalSeconds;
                }

                await Task.Delay(1);
            }

            Assert.True(stallDone, $"The scenario did not finish in {Timeout.TotalSeconds} s (self seen: {client.SelfSeen}).");
            transport.Connection!.Resume();
            await session.FlushOutboundAsync(TimeSpan.FromSeconds(5));
            return client.CreateReport(transport.Connection.SentCommandSequences(), session.MergedCommandCount);
        }
    }

    private static Vector3 MostOpenFacing(int floorSize, string sessionId)
    {
        const int ProbeTicks = WalkTicksBeforeStall + StallTicks + TicksAfterStall * 3 + 16;
        return new[] { Vector3.UnitZ, -Vector3.UnitZ, Vector3.UnitX, -Vector3.UnitX }
            .OrderByDescending(direction =>
            {
                WorldSessionRuntime runtime = new(floorSize, new Vector3(5f, 1.5f, 5f), new SceneEventHub());
                using RuntimeAuthoritativeSessionHost host = new(runtime);
                host.JoinSession(sessionId, sessionId);
                runtime.Enemies.Clear();
                Vector3 start = Position(runtime, sessionId);
                for (int tick = 1; tick <= ProbeTicks; tick++)
                {
                    host.AdvanceTick(
                        (ulong)tick,
                        SimulationClock.FixedDeltaSeconds,
                        [Walk(new PlayerSessionId(sessionId), (ulong)tick, direction)],
                        PlayerSimulationTuning.AuthoritativeDefault,
                        allowGameplaySimulation: false);
                }

                return (Position(runtime, sessionId) - start).Xz.Length;
            })
            .First();

        static Vector3 Position(WorldSessionRuntime runtime, string sessionId) =>
            Assert.Single(runtime.CaptureActorSnapshots(), actor => actor.SessionId == sessionId).Position;
    }

    private static PlayerCommandFrame Walk(PlayerSessionId session, ulong sequence, Vector3 facing)
    {
        return new PlayerCommandFrame(
            session,
            sequence,
            PlayerModeSwitchIntent.None,
            new PlayerMoveIntent(1f, 0f),
            new PlayerLookIntent(0f, 0f, facing),
            PrimaryUsePressed: false);
    }

    private sealed record StallReport(
        float MaxCorrectionAfter,
        float MaxSmoothAfter,
        int SnapsAfter,
        ulong StallEndTick,
        ulong LastAcknowledged,
        long DroppedTicks,
        long MergedCommands,
        ulong LargestSequenceJump,
        float ServerWalked,
        float ClientWalked,
        int CommandsSent)
    {
        public string Describe(string label) =>
            $"{label}: max correction after {MaxCorrectionAfter:F3} m (largest smooth {MaxSmoothAfter:F3} m, snaps {SnapsAfter}); " +
            $"stall end tick {StallEndTick}, last ack {LastAcknowledged}, dropped ticks {DroppedTicks}, merged commands {MergedCommands}, " +
            $"largest sequence jump {LargestSequenceJump}, commands sent {CommandsSent}, server walked {ServerWalked:F2} m since stall start, " +
            $"client walked {ClientWalked:F2} m since stall start";
    }

    private sealed class HeadlessClient : IDisposable
    {
        private readonly RemoteClientSession _session;
        private readonly ClientWorldSession _world;
        private readonly ClientPredictionReconciler _reconciler = new();
        private readonly PlayerSessionId _remoteSessionId;
        private readonly Vector3 _facing;
        private float _maxCorrectionAfter;
        private float _maxSmoothAfter;
        private int _snapsAfter;
        private ulong _lastAcknowledged;
        private Vector3? _serverAtStallStart;
        private Vector3 _lastServerPosition;
        private Vector3 _clientAtEnd;

        public HeadlessClient(RemoteClientSession session, int floorSize, Vector3 facing)
        {
            _session = session;
            _remoteSessionId = new PlayerSessionId(session.SessionId);
            _facing = facing;
            _world = new ClientWorldSession(floorSize, new Vector3(5f, 1.5f, 5f), new SceneEventHub());
            Ticks = new ClientCommandTickStream(_world.LocalPlayerSessionId);
        }

        public ClientCommandTickStream Ticks { get; }

        public bool SelfSeen { get; private set; }

        public ulong StallStartTick { get; set; } = ulong.MaxValue;

        public ulong StallEndTick { get; set; } = ulong.MaxValue;

        public Vector3? ClientWalkStart { get; set; }

        public Vector3 Position => _world.Player.GetWorldPosition();

        public void Dispose()
        {
            _world.Dispose();
        }

        public void Frame(double elapsedSeconds)
        {
            _world.Player.NudgeWorldPosition(_reconciler.TakeSmoothingStep((float)elapsedSeconds));
            Ticks.CaptureInput(Walk(_world.LocalPlayerSessionId, 0, _facing));
            Ticks.Advance(elapsedSeconds, this, static (client, command, fixedDeltaSeconds) => client.Tick(command, fixedDeltaSeconds));
        }

        private void Tick(PlayerCommandFrame command, float fixedDeltaSeconds)
        {
            _session.QueueCommandFrame(RemotePlayerCommandRelay.CreateRelayCommand(command, _remoteSessionId, _world.Player.Camera.Front));
            _world.AdvancePredictionTick(
                command.Sequence,
                fixedDeltaSeconds,
                ClientSimulationCommandPolicy.PrepareForLocalSimulation(command, useRemoteAuthority: true),
                PlayerSimulationTuning.AuthoritativeDefault,
                primaryUseHeld: command.PrimaryUsePressed);
            _reconciler.RecordPrediction(command.Sequence, _world.Player.GetWorldPosition());
        }

        public void Drain()
        {
            while (_session.TryDequeueInbound(out IServerToClientMessage? message) && message != null)
            {
                if (message is JoinAcceptedMessage accepted)
                {
                    ClientJoinAcceptedApplier.ApplyToRuntime(_world, accepted);
                    _reconciler.Reset();
                    continue;
                }

                if (message is not ServerSnapshotMessage snapshotMessage)
                {
                    continue;
                }

                ServerSnapshotEnvelope snapshot = snapshotMessage.Snapshot;
                Vector3 before = _world.Player.GetWorldPosition();
                Vector3 pendingBefore = _reconciler.PendingOffset;
                RemoteWorldMutationApplier.ApplySnapshotToRuntime(_world, snapshot);
                ClientCorrectionApplier.ApplySnapshotToRuntime(_world, snapshot, _session.SessionId, _reconciler);
                float jump = (_world.Player.GetWorldPosition() - before).Length;
                float smooth = (_reconciler.PendingOffset - pendingBefore).Length;
                foreach (ActorSnapshot actor in snapshot.Actors)
                {
                    if (actor.Kind == CreatureKind.Player && actor.SessionId == _session.SessionId)
                    {
                        SelfSeen = true;
                        _lastServerPosition = actor.Position;
                        if (ClientWalkStart.HasValue)
                        {
                            _serverAtStallStart ??= actor.Position;
                        }
                    }
                }

                if (!snapshot.LastAcceptedCommandSequences.TryGetValue(_session.SessionId, out ulong acknowledged))
                {
                    continue;
                }

                _lastAcknowledged = Math.Max(_lastAcknowledged, acknowledged);
                if (acknowledged >= StallStartTick)
                {
                    _maxCorrectionAfter = Math.Max(_maxCorrectionAfter, Math.Max(jump, smooth));
                    _maxSmoothAfter = Math.Max(_maxSmoothAfter, smooth);
                    if (jump > 0.01f)
                    {
                        _snapsAfter++;
                    }
                }
            }

            _clientAtEnd = _world.Player.GetWorldPosition();
        }

        public StallReport CreateReport(IReadOnlyList<ulong> sentSequences, long mergedCommands)
        {
            ulong largestJump = 0;
            for (int i = 1; i < sentSequences.Count; i++)
            {
                largestJump = Math.Max(largestJump, sentSequences[i] - sentSequences[i - 1]);
            }

            float clientWalked = ClientWalkStart.HasValue ? (_clientAtEnd - ClientWalkStart.Value).Xz.Length : 0f;
            float serverWalked = _serverAtStallStart.HasValue ? (_lastServerPosition - _serverAtStallStart.Value).Xz.Length : 0f;
            return new StallReport(
                _maxCorrectionAfter,
                _maxSmoothAfter,
                _snapsAfter,
                StallEndTick,
                _lastAcknowledged,
                Ticks.DroppedTicks,
                mergedCommands,
                largestJump,
                serverWalked,
                clientWalked,
                sentSequences.Count);
        }
    }

    private sealed class StallableTransport : IClientTransport
    {
        public StallableConnection? Connection { get; private set; }

        public async ValueTask<ITransportConnection> ConnectAsync(string host, int port, CancellationToken cancellationToken = default)
        {
            ITransportConnection inner = await new TcpClientConnector().ConnectAsync(host, port, cancellationToken);
            Connection = new StallableConnection(inner);
            return Connection;
        }
    }

    private sealed class StallableConnection(ITransportConnection inner) : ITransportConnection
    {
        private readonly TransportJsonCodec _codec = new();
        private readonly LengthPrefixedMessageFramer _framer = new();
        private readonly List<ulong> _sentCommands = [];
        private volatile TaskCompletionSource? _gate;

        public string ConnectionId => inner.ConnectionId;

        public void Stall()
        {
            _gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public void Resume()
        {
            _gate?.TrySetResult();
        }

        public IReadOnlyList<ulong> SentCommandSequences()
        {
            lock (_sentCommands)
            {
                return _sentCommands.ToArray();
            }
        }

        public async ValueTask SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
        {
            TaskCompletionSource? gate = _gate;
            if (gate != null)
            {
                await gate.Task.WaitAsync(cancellationToken);
            }

            if (_framer.TryDeframe(payload.Span, out byte[]? message, out _) &&
                _codec.Decode(message!).Message is PlayerCommandMessage command)
            {
                lock (_sentCommands)
                {
                    _sentCommands.Add(command.CommandFrame.Sequence);
                }
            }

            await inner.SendAsync(payload, cancellationToken);
        }

        public ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            return inner.ReceiveAsync(buffer, cancellationToken);
        }

        public ValueTask DisposeAsync()
        {
            Resume();
            return inner.DisposeAsync();
        }
    }
}
