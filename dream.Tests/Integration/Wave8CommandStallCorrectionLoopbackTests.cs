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
// Measured before the fix (Debug, loopback, wave 8 tip e4c6930, three runs each):
// - 1 s send stall: 4.530 m snap (1 snap), every run. The queue folded 47 stall commands into one
//   frame whose sequence jumped by 48; the buffered inbox enqueued it as ONE slot standing for ONE
//   tick, skipped 16 leading ticks and popped it with its full jumped sequence. The ack covered 64
//   client walk ticks after the server walked 17 of them: 47 ticks (~4.5 m) behind, a snap.
// - 1 s frame stall: max 0.834 m (smoothed, no snap); the client drops all but 8 catch-up ticks.
// - Hitchy loop (30-120 ms hitches, every 150-400 ms): max 0.741 m, no snap.
//
// After wave 8 fix loop 1 (the client predicts and sends idle past the 16-tick repeat window, the
// outbound queue folds runs of equal movement, the inbox counts a sequence jump as that many ticks,
// skips every starved tick and drops the stall's leftover idle tail):
// - 1 s send stall: 0.000 m, no snap (48 idle ticks, wire jump 34).
// - 1 s frame stall: 0.741-0.834 m, no snap (unchanged: the client drops the stalled ticks).
// - Hitchy loop: 0.466 m, no snap.
//
// Wave 8 fix loop 2 adds the network stall (reviewer S-D): the transport accepts every send at once
// and holds it for a second, as a kernel socket buffer does when the network stalls, so the
// client's send queue never grows and only the ack lag (ClientCommandTickStream's second trigger,
// wired here as PlayScene wires it) can tell the client the server is idling it. And steady play
// over a constant 200 ms uplink delay, which must never trigger the idle mirror.
//
// Wave 9 fix loop 1 (W9-5): the facing probe had ranked a walk off the world edge as open and the
// client walked along its camera's default -Z, so the steady and hitchy runs measured a free fall
// (7.1 m total correction in a flaky run). With the walk on the floor and W9-2 (starved ticks
// compared like with like), max correction / mean per ack, 3 runs each, 64 Hz vs 32 Hz snapshots:
// send stall 0.000 / 0.000 both; frame stall 0.37-0.84 m vs 0.84 m; network stall 0.000 both;
// steady 200 ms 0.56-0.65 m (mean 0.0021-0.0024) vs 0.56-0.65 m (0.0042-0.0066); hitchy 0.46 m
// (0.0017) vs 0.46 m (0.0033). No snaps. The max is the join settle at ack ~40 in every run.
public class Wave8CommandStallCorrectionLoopbackTests(ITestOutputHelper output)
{
    private const int WalkTicksBeforeStall = 32;
    private const int StallTicks = SimulationClock.AuthoritativeTicksPerSecond;
    private const int TicksAfterStall = 96;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(40);

    // The mechanism behind the fix: the stall still overflows the outbound queue and the wire still
    // carries folded frames with sequence jumps, but the client idled past the repeat window and the
    // server counted and skipped every starved tick, so there is next to no correction instead of
    // the 4.53 m snap measured before (0.000 m measured).
    [Fact]
    public async Task SendStall_OneSecond_ClientIdlesPastTheRepeatWindow_AndTheServerSkipsEveryStarvedTick()
    {
        StallReport report = await RunAsync(Scenario.SendStall, "w8-send-a");
        output.WriteLine(report.Describe("1 s send stall"));

        Assert.True(report.MergedCommands > 0, "The stall never overflowed the outbound queue.");
        Assert.True(report.LargestSequenceJump > 16, $"largest wire sequence jump {report.LargestSequenceJump}");
        // 64 stall ticks minus the 16-tick repeat window, give or take the send loop's timing.
        Assert.InRange(report.SendStallIdleTicks, 40L, 56L);
        Assert.True(report.MaxCorrectionAfter < 0.5f, report.Describe("send"));
        Assert.Equal(0, report.SnapsAfter);
    }

    [Fact]
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

    // S-D: a one-second network stall that the send queue never sees. The ack lag idles the client
    // about a round trip after the server's repeat window ended; the server replays the walk ticks
    // between the two switches, so the correction stays well below the snap distance.
    [Fact]
    public async Task NetworkStall_OneSecond_NoLocalBacklog_AckLagIdles_CorrectsBelowSnapDistance()
    {
        StallReport report = await RunAsync(Scenario.NetworkStall, "w8-net");
        output.WriteLine(report.Describe("1 s network stall"));

        Assert.True(report.MaxPendingDuringStall < ClientCommandTickStream.SendStallBacklog, report.Describe("the local send queue grew"));
        Assert.True(report.AckStallIdleTicks > 0, report.Describe("the ack lag never idled the client"));
        // Nothing folded on the client: the wire carries consecutive sequences.
        Assert.Equal(1UL, report.LargestSequenceJump);
        Assert.True(report.MaxCorrectionAfter < ClientPredictionReconciler.DefaultSnapDistance, report.Describe("network"));
        Assert.Equal(0, report.SnapsAfter);
    }

    // S-D: steady play over a constant 200 ms delay never idles the player.
    [Fact]
    public async Task SteadyPlayOver200MsDelay_NeverTriggersTheIdleMirror()
    {
        StallReport report = await RunAsync(Scenario.Steady200Ms, "w8-rtt200");
        output.WriteLine(report.Describe("steady 200 ms uplink delay"));

        Assert.Equal(0L, report.SendStallIdleTicks);
        Assert.Equal(0, report.SnapsAfter);
        Assert.True(report.MaxCorrectionAfter < ClientPredictionReconciler.DefaultSnapDistance, report.Describe("steady"));
    }

    [Fact]
    public async Task HitchyLoop_SteadyWalk_CorrectsBelowSnapDistance()
    {
        StallReport report = await RunAsync(Scenario.Hitchy, "w8-hitchy");
        output.WriteLine(report.Describe("hitchy loop"));

        Assert.True(report.MaxCorrectionAfter < ClientPredictionReconciler.DefaultSnapDistance, report.Describe("hitchy"));
        Assert.Equal(0, report.SnapsAfter);
        Assert.True(report.ClientEndHeight > 1f, report.Describe("the client fell off the world"));
    }

    private enum Scenario
    {
        SendStall,
        FrameStall,
        Hitchy,
        NetworkStall,
        Steady200Ms
    }

    // NETR-7: the default snapshot interval is 2 ticks (32 Hz), so the reconciler gets an ack every
    // other tick instead of every tick. Steady play over the 200 ms delay corrects no worse at
    // 32 Hz than at 64 Hz.
    [Fact]
    public async Task SteadyPlay_At32HzSnapshots_CorrectsLikeAt64Hz()
    {
        StallReport perTick = await RunAsync(Scenario.Steady200Ms, "w8-rate", snapshotIntervalTicks: 1);
        StallReport everyOther = await RunAsync(Scenario.Steady200Ms, "w8-rate", snapshotIntervalTicks: 2);
        output.WriteLine(perTick.Describe("64 Hz snapshots"));
        output.WriteLine(everyOther.Describe("32 Hz snapshots"));

        foreach (StallReport report in new[] { perTick, everyOther })
        {
            Assert.Equal(0L, report.SendStallIdleTicks);
            Assert.Equal(0, report.SnapsAfter);
            Assert.True(report.MaxCorrectionAfter < ClientPredictionReconciler.DefaultSnapDistance, report.Describe("steady"));
            // W9-5: the comparison is only of walking if the walk stayed on the floor.
            Assert.True(report.ClientEndHeight > 1f, report.Describe("the client fell off the world"));
        }

        // Similar, not identical: each 32 Hz ack folds two ticks of drift into one correction, so
        // the mean per ack roughly doubles while the total over the run stays about the same
        // (measured alone: mean 0.0025 m x 267 acks at 64 Hz, 0.0042 m x 134 acks at 32 Hz). The
        // largest single correction is the join settle in both (0.56-0.66 m at ack 40). A loaded
        // Debug run adds client-side dropped ticks, hence the absolute allowance.
        float perTickTotal = perTick.MeanCorrectionAfter * perTick.CorrectionSamples;
        float everyOtherTotal = everyOther.MeanCorrectionAfter * everyOther.CorrectionSamples;
        Assert.True(
            everyOtherTotal <= (perTickTotal * 2f) + 1f,
            $"total correction {everyOtherTotal:F3} m at 32 Hz vs {perTickTotal:F3} m at 64 Hz");
    }

    private async Task<StallReport> RunAsync(Scenario scenario, string sessionId, int? snapshotIntervalTicks = null)
    {
        DedicatedServerHost? host = null;
        await using IntegratedLocalServerHandle server = await IntegratedLocalServerHandle.StartAsync(
            0,
            () => host = DedicatedServerHost.CreateRuntimeAttached(persistence: null),
            snapshotIntervalTicks is { } interval
                ? IntegratedLocalServerHandle.DefaultConnectionOptions with { SnapshotIntervalTicks = interval }
                : null);
        Assert.True(server.IsReady, server.ErrorMessage);
        int floorSize = host!.Runtime!.Map.Width;
        Vector3 facing = MostOpenFacing(floorSize, sessionId);

        StallableTransport transport = new();
        RemoteClientSession session = await RemoteClientSession.ConnectAsync(
            new RemoteClientOptions("127.0.0.1", server.BoundPort, sessionId, sessionId, "stall-test"),
            transport);
        await using (session)
        {
            if (scenario == Scenario.NetworkStall)
            {
                transport.Connection!.UseDelayLine(TimeSpan.Zero);
            }
            else if (scenario == Scenario.Steady200Ms)
            {
                transport.Connection!.UseDelayLine(TimeSpan.FromMilliseconds(200));
            }

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
                            case Scenario.NetworkStall:
                                transport.Connection!.Hold();
                                break;
                            case Scenario.Steady200Ms:
                                stallDone = true;
                                client.StallEndTick = client.Ticks.LastTick;
                                break;
                            case Scenario.Hitchy:
                                stallDone = true;
                                client.StallEndTick = client.Ticks.LastTick;
                                nextHitchSeconds = now + 0.1d;
                                break;
                        }
                    }

                    if (scenario == Scenario.NetworkStall && stalled && !stallDone)
                    {
                        client.MaxPendingDuringStall = Math.Max(client.MaxPendingDuringStall, session.PendingCommandCount);
                    }

                    if ((scenario == Scenario.SendStall || scenario == Scenario.NetworkStall) && stalled && !stallDone && walked >= WalkTicksBeforeStall + StallTicks)
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

                    int after = scenario is Scenario.Hitchy or Scenario.Steady200Ms ? TicksAfterStall * 3 : TicksAfterStall;
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

                // A walk off the world edge keeps its horizontal speed while it falls, so distance
                // alone ranked it as open (W9-5: the steady and hitchy scenarios then measured a
                // free fall). Only a walk that stays on the floor counts.
                Vector3 end = Position(runtime, sessionId);
                return MathF.Abs(end.Y - start.Y) < 0.1f ? (end - start).Xz.Length : -1f;
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
        int CommandsSent,
        long SendStallIdleTicks,
        ulong StallStartTick,
        ulong MaxCorrectionAck,
        ulong MaxCorrectionTick,
        long AckStallIdleTicks,
        int MaxPendingDuringStall,
        // Mean correction per acknowledged snapshot after the stall start, and their count.
        float MeanCorrectionAfter = 0f,
        int CorrectionSamples = 0,
        // The client player's height at the end: the walk must stay on the floor (W9-5).
        float ClientEndHeight = float.NaN)
    {
        public string Describe(string label) =>
            $"{label}: max correction after {MaxCorrectionAfter:F3} m (largest smooth {MaxSmoothAfter:F3} m, snaps {SnapsAfter}, " +
            $"mean {MeanCorrectionAfter:F4} m over {CorrectionSamples} acks); " +
            $"send-stall idle ticks {SendStallIdleTicks} (ack-lag only {AckStallIdleTicks}), max unsent during stall {MaxPendingDuringStall}; stall start tick {StallStartTick}, max correction at ack {MaxCorrectionAck} (client tick {MaxCorrectionTick}); " +
            $"stall end tick {StallEndTick}, last ack {LastAcknowledged}, dropped ticks {DroppedTicks}, merged commands {MergedCommands}, " +
            $"largest sequence jump {LargestSequenceJump}, commands sent {CommandsSent}, server walked {ServerWalked:F2} m since stall start, " +
            $"client walked {ClientWalked:F2} m since stall start, client height at end {ClientEndHeight:F2} m";
    }

    private sealed class HeadlessClient : IDisposable
    {
        private readonly RemoteClientSession _session;
        private readonly ClientWorldSession _world;
        private readonly ClientPredictionReconciler _reconciler = new();
        private readonly PlayerSessionId _remoteSessionId;
        private readonly Vector3 _facing;
        private float _maxCorrectionAfter;
        private ulong _maxCorrectionAck;
        private ulong _maxCorrectionTick;
        private float _maxSmoothAfter;
        private double _correctionSum;
        private int _correctionSamples;
        private int _snapsAfter;
        private ulong _lastAcknowledged;
        private Vector3? _serverAtStallStart;
        private Vector3 _lastServerPosition;
        private Vector3 _clientAtEnd;
        private ulong? _latestAck;

        public HeadlessClient(RemoteClientSession session, int floorSize, Vector3 facing)
        {
            _session = session;
            _remoteSessionId = new PlayerSessionId(session.SessionId);
            _facing = facing;
            _world = new ClientWorldSession(floorSize, new Vector3(5f, 1.5f, 5f), new SceneEventHub());
            Ticks = new ClientCommandTickStream(_world.LocalPlayerSessionId);
            // As PlayScene wires it: a send stalled past the repeat window idles the prediction, and
            // so does an ack lag past the round trip (the newest snapshot's ack, as
            // ClientRoutingContext.LastSnapshot holds it).
            Ticks.ObserveSendBacklog(() => _session.PendingCommandCount);
            Ticks.ObserveAcknowledgedSequence(() => _latestAck);
        }

        public int MaxPendingDuringStall { get; set; }

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
            // The relayed command carries the camera's front as its facing (Tick), so the camera
            // must face the probed direction, not its default front (-Z) (W9-5).
            _world.Player.SetFacing(_facing);
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
                    _world.Player.SetFacing(_facing);
                    _reconciler.Reset();
                    continue;
                }

                if (message is not ServerSnapshotMessage snapshotMessage)
                {
                    continue;
                }

                ServerSnapshotEnvelope snapshot = snapshotMessage.Snapshot;
                _latestAck = ClientCommandTickStream.AcknowledgedSequence(snapshot, _session.SessionId);
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
                    if (Math.Max(jump, smooth) > _maxCorrectionAfter)
                    {
                        _maxCorrectionAck = acknowledged;
                        _maxCorrectionTick = Ticks.LastTick;
                    }

                    _maxCorrectionAfter = Math.Max(_maxCorrectionAfter, Math.Max(jump, smooth));
                    _maxSmoothAfter = Math.Max(_maxSmoothAfter, smooth);
                    _correctionSum += Math.Max(jump, smooth);
                    _correctionSamples++;
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
                sentSequences.Count,
                Ticks.SendStallIdleTicks,
                StallStartTick,
                _maxCorrectionAck,
                _maxCorrectionTick,
                Ticks.AckStallIdleTicks,
                MaxPendingDuringStall,
                _correctionSamples == 0 ? 0f : (float)(_correctionSum / _correctionSamples),
                _correctionSamples,
                _clientAtEnd.Y);
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

    // Stall blocks SendAsync (a stalled send loop: the session's queue backs up). With the delay
    // line on, SendAsync returns at once and a pump forwards each payload, in order, once its delay
    // has passed; Hold keeps the pump from forwarding until Resume (a network stall: the bytes
    // wait in the kernel buffer, the session's queue stays empty).
    private sealed class StallableConnection(ITransportConnection inner) : ITransportConnection
    {
        private readonly TransportJsonCodec _codec = new();
        private readonly LengthPrefixedMessageFramer _framer = new();
        private readonly List<ulong> _sentCommands = [];
        private readonly Queue<(long Due, byte[] Payload)> _line = new();
        private readonly SemaphoreSlim _lineSignal = new(0);
        private readonly CancellationTokenSource _pumpStop = new();
        private volatile TaskCompletionSource? _gate;
        private volatile TaskCompletionSource? _hold;
        private long _lineDelayTimestamp;
        private volatile bool _delayLine;
        private Task? _pump;

        public string ConnectionId => inner.ConnectionId;

        public void Stall()
        {
            _gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public void UseDelayLine(TimeSpan delay)
        {
            _lineDelayTimestamp = (long)(delay.TotalSeconds * Stopwatch.Frequency);
            _pump ??= Task.Run(PumpAsync);
            _delayLine = true;
        }

        public void Hold()
        {
            _hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public void Resume()
        {
            _gate?.TrySetResult();
            _hold?.TrySetResult();
        }

        private async Task PumpAsync()
        {
            CancellationToken token = _pumpStop.Token;
            try
            {
                while (true)
                {
                    await _lineSignal.WaitAsync(token);
                    (long Due, byte[] Payload) entry;
                    lock (_line)
                    {
                        entry = _line.Dequeue();
                    }

                    TaskCompletionSource? hold = _hold;
                    if (hold != null)
                    {
                        await hold.Task.WaitAsync(token);
                    }

                    long remaining = entry.Due - Stopwatch.GetTimestamp();
                    if (remaining > 0)
                    {
                        await Task.Delay(TimeSpan.FromSeconds((double)remaining / Stopwatch.Frequency), token);
                    }

                    await inner.SendAsync(entry.Payload, token);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception) when (token.IsCancellationRequested)
            {
            }
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

            // An empty frame is the session's keepalive; it carries no message.
            if (_framer.TryDeframe(payload.Span, out byte[]? message, out _) &&
                message!.Length > 0 &&
                _codec.Decode(message).Message is PlayerCommandMessage command)
            {
                lock (_sentCommands)
                {
                    _sentCommands.Add(command.CommandFrame.Sequence);
                }
            }

            if (_delayLine)
            {
                lock (_line)
                {
                    _line.Enqueue((Stopwatch.GetTimestamp() + _lineDelayTimestamp, payload.ToArray()));
                }

                _lineSignal.Release();
                return;
            }

            await inner.SendAsync(payload, cancellationToken);
        }

        public ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            return inner.ReceiveAsync(buffer, cancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            Resume();
            _pumpStop.Cancel();
            if (_pump != null)
            {
                await _pump;
            }

            await inner.DisposeAsync();
        }
    }
}
