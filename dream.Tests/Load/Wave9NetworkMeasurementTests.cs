using System.Diagnostics;
using System.Reflection;
using System.Text;
using OpenTK.Mathematics;
using Xunit;
using Xunit.Abstractions;
using Dream.Client.Networking;
using Dream.Client.Replication;
using Dream.Client.Startup;
using Dream.Client.World;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Server;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Tests.Integration;

namespace Dream.Tests.Load;

// Wave 9 tester measurements (skipped unless DREAM_LOAD_TEST=1; they print tables, assert little).
//
// 1. NETR-7 x NETR-4/5: the wave-8 stall table re-measured at 64 Hz (SnapshotIntervalTicks 1) and
//    at the new default 32 Hz (2), with the wave-8 scenarios run through
//    Wave8CommandStallCorrectionLoopbackTests' own harness (by reflection, so the scenario code is
//    the same). DREAM_W9_RUNS sets the runs per cell (default 3).
// 2. The same steady 200 ms and hitchy scenarios with the CPU saturated by spinning threads (load),
//    to reproduce NETR-7's late corrections under load and see whether 32 Hz makes them worse.
// 3. PHYS-2 x client prediction: a headless client walks into a cube the server has frozen, with
//    freezing on and off on the server; reports the reconciliation corrections and how the
//    replicated cube motion on the client follows the server.
[Collection(TimingSensitiveCollection.Name)]
public class Wave9NetworkMeasurementTests
{
    private readonly ITestOutputHelper _output;

    public Wave9NetworkMeasurementTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static int Runs => int.TryParse(Environment.GetEnvironmentVariable("DREAM_W9_RUNS"), out int runs) && runs > 0 ? runs : 3;

    [LoadFact]
    public async Task StallTable_At64HzAnd32Hz()
    {
        // DREAM_W9_SCENARIOS (comma-separated) narrows the table.
        string[] scenarios = Environment.GetEnvironmentVariable("DREAM_W9_SCENARIOS") is { Length: > 0 } only
            ? only.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : ["SendStall", "FrameStall", "NetworkStall", "Steady200Ms", "Hitchy"];
        StringBuilder table = new();
        foreach (string scenario in scenarios)
        {
            foreach (int interval in new[] { 1, 2 })
            {
                List<string> cells = [];
                for (int run = 0; run < Runs; run++)
                {
                    Wave8Report report = await RunWave8Async(scenario, $"w9-{scenario}-{interval}-{run}", interval);
                    cells.Add(report.Short());
                    _output.WriteLine(report.Describe($"{scenario} @{64 / interval} Hz run {run}"));
                }

                table.AppendLine($"{scenario,-13} {64 / interval,2} Hz: {string.Join(" | ", cells)}");
            }
        }

        _output.WriteLine("---- stall table (max correction m / snaps / late ticks = max-correction ack - stall start / ack-idle ticks) ----");
        _output.WriteLine(table.ToString());
    }

    [LoadFact]
    public async Task SteadyAndHitchy_UnderCpuLoad_At64HzAnd32Hz()
    {
        StringBuilder table = new();
        foreach (string scenario in new[] { "Steady200Ms", "Hitchy" })
        {
            foreach (int interval in new[] { 1, 2 })
            {
                List<string> cells = [];
                for (int run = 0; run < Runs; run++)
                {
                    using CpuBurner burner = new(Environment.ProcessorCount);
                    Wave8Report report = await RunWave8Async(scenario, $"w9-load-{scenario}-{interval}-{run}", interval);
                    cells.Add(report.Short());
                    _output.WriteLine(report.Describe($"LOADED {scenario} @{64 / interval} Hz run {run}"));
                }

                table.AppendLine($"loaded {scenario,-12} {64 / interval,2} Hz: {string.Join(" | ", cells)}");
            }
        }

        _output.WriteLine("---- under CPU load ----");
        _output.WriteLine(table.ToString());
    }

    [LoadFact]
    public async Task PushIntoAFrozenCube_PredictionCorrections_FreezeOnVsOff()
    {
        StringBuilder table = new();
        foreach (bool freeze in new[] { true, false })
        {
            for (int run = 0; run < Runs; run++)
            {
                PushReport report = await RunPushAsync(freeze, $"w9-push-{freeze}-{run}");
                _output.WriteLine(report.ToString());
                table.AppendLine($"freeze {(freeze ? "on " : "off")}: {report.Short()}");
            }
        }

        _output.WriteLine("---- push (max correction / total correction / snaps / server cube moved / client cube moved / client-server cube gap at end / cube frozen before push) ----");
        _output.WriteLine(table.ToString());
    }

    // ---- 1 and 2: the wave-8 harness by reflection ----

    private async Task<Wave8Report> RunWave8Async(string scenario, string sessionId, int interval)
    {
        Type harness = typeof(Wave8CommandStallCorrectionLoopbackTests);
        object instance = Activator.CreateInstance(harness, _output)!;
        Type scenarioType = harness.GetNestedType("Scenario", BindingFlags.NonPublic)!;
        MethodInfo run = harness.GetMethod("RunAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Task task = (Task)run.Invoke(instance, [Enum.Parse(scenarioType, scenario), sessionId, (int?)interval])!;
        await task;
        object report = task.GetType().GetProperty("Result")!.GetValue(task)!;
        T Get<T>(string name) => (T)report.GetType().GetProperty(name)!.GetValue(report)!;
        return new Wave8Report(
            Get<float>("MaxCorrectionAfter"),
            Get<int>("SnapsAfter"),
            Get<float>("MeanCorrectionAfter"),
            Get<int>("CorrectionSamples"),
            Get<ulong>("StallStartTick"),
            Get<ulong>("MaxCorrectionAck"),
            Get<long>("SendStallIdleTicks"),
            Get<long>("AckStallIdleTicks"),
            Get<long>("DroppedTicks"),
            (string)report.GetType().GetMethod("Describe")!.Invoke(report, ["x"])!);
    }

    private sealed record Wave8Report(
        float MaxCorrection,
        int Snaps,
        float MeanCorrection,
        int Samples,
        ulong StallStartTick,
        ulong MaxCorrectionAck,
        long IdleTicks,
        long AckIdleTicks,
        long DroppedTicks,
        string Full)
    {
        public long LateTicks => (long)MaxCorrectionAck - (long)StallStartTick;

        public string Short() =>
            $"{MaxCorrection:F3} m/{Snaps}s/late {LateTicks}/ackidle {AckIdleTicks}/drop {DroppedTicks}";

        public string Describe(string label) => $"{label}: {Full[3..]}";
    }

    private sealed class CpuBurner : IDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly Thread[] _threads;

        public CpuBurner(int threads)
        {
            _threads = new Thread[threads];
            for (int i = 0; i < threads; i++)
            {
                _threads[i] = new Thread(() =>
                {
                    double sink = 0;
                    while (!_stop.IsCancellationRequested)
                    {
                        for (int k = 0; k < 10000; k++)
                        {
                            sink += Math.Sqrt(k);
                        }
                    }

                    GC.KeepAlive(sink);
                })
                { IsBackground = true, Name = "w9-cpu-burner" };
                _threads[i].Start();
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            foreach (Thread thread in _threads)
            {
                thread.Join();
            }

            _stop.Dispose();
        }
    }

    // ---- 3: pushing a frozen cube ----

    private const int WalkTicks = 128;

    private async Task<PushReport> RunPushAsync(bool freeze, string sessionId)
    {
        DedicatedServerHost? host = null;
        Cube? serverCube = null;
        Vector3 facing = Vector3.UnitX;
        int floorSize = 0;
        await using IntegratedLocalServerHandle server = await IntegratedLocalServerHandle.StartAsync(
            0,
            () =>
            {
                host = DedicatedServerHost.CreateRuntimeAttached(persistence: null);
                WorldSessionRuntime runtime = host.Runtime!;
                runtime.Physics.CubeRest.Enabled = freeze;
                runtime.Enemies.Clear();
                floorSize = runtime.Map.Width;
                (Vector3 spawn, facing, Vector3 move) = ProbeSpawnAndOpenFacing(floorSize, sessionId);
                Vector3 cell = new(
                    MathF.Floor(spawn.X + move.X * 2.5f),
                    1f,
                    MathF.Floor(spawn.Z + move.Z * 2.5f));
                serverCube = runtime.Map.TryAddCube(cell) ?? throw new InvalidOperationException($"cell {cell} is taken");
                WorldObjectRegistry registry = (WorldObjectRegistry)typeof(WorldSessionRuntime)
                    .GetField("_worldObjects", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(runtime)!;
                registry.RegisterCube(serverCube, runtime.Map);
                return host;
            },
            IntegratedLocalServerHandle.DefaultConnectionOptions);
        Assert.True(server.IsReady, server.ErrorMessage);
        BulletSharp.RigidBody serverBody = host!.Runtime!.Physics.DynamicMapCubeBodies.Single(candidate =>
            candidate.UserObject is ValueTuple<Cube, Vector3> tuple && ReferenceEquals(tuple.Item1, serverCube));
        Assert.True(host.Runtime.Map.TryGetReplicatedId(serverCube!, out int cubeId));

        RemoteClientSession session = await RemoteClientSession.ConnectAsync(
            new RemoteClientOptions("127.0.0.1", server.BoundPort, sessionId, sessionId, "w9-push"));
        await using (session)
        {
            using PushClient client = new(session, floorSize, facing, cubeId);
            Stopwatch clock = Stopwatch.StartNew();
            double last = 0d;
            bool frozenBeforePush = false;
            ulong? idleStart = null;
            ulong? walkStart = null;
            Vector3 serverCubeStart = Vector3.Zero;
            while (clock.Elapsed < TimeSpan.FromSeconds(30))
            {
                double now = clock.Elapsed.TotalSeconds;
                double elapsed = now - last;
                last = now;
                client.Drain();
                if (client.SelfSeen)
                {
                    idleStart ??= client.Ticks.LastTick;
                    // One second idle (the cube rests and, with freezing on, freezes), then walk.
                    if (walkStart == null && client.Ticks.LastTick - idleStart.Value >= 64)
                    {
                        walkStart = client.Ticks.LastTick;
                        frozenBeforePush = host.Runtime.Physics.CubeRest.IsFrozen(serverBody);
                        serverCubeStart = serverCube!.Position;
                        client.StartMeasuring();
                    }

                    client.Walking = walkStart != null && client.Ticks.LastTick - walkStart.Value < WalkTicks;
                    client.Frame(elapsed);
                    if (walkStart != null && client.Ticks.LastTick - walkStart.Value >= WalkTicks + 96)
                    {
                        break;
                    }
                }

                await Task.Delay(1);
            }

            Assert.NotNull(walkStart);
            await Task.Delay(300);
            client.Drain();
            _output.WriteLine($"facing {facing}, cube placed at {serverCubeStart}, client player ended at {client.PlayerPosition}");
            Vector3 serverCubeEnd = serverCube!.Position;
            return client.CreateReport(freeze, frozenBeforePush, serverCubeStart, serverCubeEnd);
        }
    }

    // The spawn, a facing whose walk is open for 6 m, and the direction that walk actually moves
    // the player (measured on a probe runtime with the same session id).
    private static (Vector3 Spawn, Vector3 Facing, Vector3 Move) ProbeSpawnAndOpenFacing(int floorSize, string sessionId)
    {
        foreach (Vector3 facing in new[] { Vector3.UnitX, -Vector3.UnitX, Vector3.UnitZ, -Vector3.UnitZ })
        {
            WorldSessionRuntime probe = new(floorSize, new Vector3(5f, 1.5f, 5f), new SceneEventHub());
            using RuntimeAuthoritativeSessionHost probeHost = new(probe);
            probeHost.JoinSession(sessionId, sessionId);
            probe.Enemies.Clear();
            PlayerSessionId id = new(sessionId);
            Vector3 spawn = Assert.Single(probe.CaptureActorSnapshots(), actor => actor.SessionId == sessionId).Position;
            for (ulong tick = 1; tick <= 32; tick++)
            {
                probeHost.AdvanceTick(
                    tick,
                    SimulationClock.FixedDeltaSeconds,
                    [new PlayerCommandFrame(id, tick, PlayerModeSwitchIntent.None, new PlayerMoveIntent(1f, 0f), new PlayerLookIntent(0f, 0f, facing), PrimaryUsePressed: false)],
                    PlayerSimulationTuning.AuthoritativeDefault,
                    allowGameplaySimulation: false);
            }

            Vector3 moved = Assert.Single(probe.CaptureActorSnapshots(), actor => actor.SessionId == sessionId).Position - spawn;
            if (moved.Xz.Length < 1f)
            {
                continue;
            }

            Vector3 move = MathF.Abs(moved.X) > MathF.Abs(moved.Z)
                ? new Vector3(MathF.Sign(moved.X), 0f, 0f)
                : new Vector3(0f, 0f, MathF.Sign(moved.Z));
            bool open = true;
            for (int step = 1; step <= 6 && open; step++)
            {
                Vector3 ahead = spawn + move * step;
                Vector3 side = new(move.Z, 0f, move.X);
                foreach (float lateral in new[] { -0.45f, 0.45f })
                {
                    Vector3 probeCell = ahead + side * lateral;
                    int x = (int)MathF.Floor(probeCell.X);
                    int z = (int)MathF.Floor(probeCell.Z);
                    open &= x > 0 && z > 0 && x < floorSize - 1 && z < floorSize - 1 &&
                        probe.Map.TryGetCubeAtCell(x, 1, z) == null && probe.Map.TryGetCubeAtCell(x, 2, z) == null;
                }
            }

            if (open)
            {
                return (spawn, facing, move);
            }
        }

        throw new InvalidOperationException("No open walk from the spawn.");
    }

    private sealed record PushReport(
        bool Freeze,
        bool FrozenBeforePush,
        float MaxCorrection,
        float TotalCorrection,
        int Snaps,
        int Acks,
        float ServerCubeMoved,
        float ClientCubeMoved,
        float EndGap,
        int FirstCubeMoveAck)
    {
        public string Short() =>
            $"{MaxCorrection:F3} m / {TotalCorrection:F3} m / {Snaps} / {ServerCubeMoved:F2} m / {ClientCubeMoved:F2} m / gap {EndGap:F3} m / client cube first moved at ack {FirstCubeMoveAck} of {Acks} / frozen {FrozenBeforePush}";
    }

    // PlayScene's per-tick loop, as in Wave8CommandStallCorrectionLoopbackTests.HeadlessClient,
    // walking along a fixed facing while Walking is set.
    private sealed class PushClient : IDisposable
    {
        private readonly RemoteClientSession _session;
        private readonly ClientWorldSession _world;
        private readonly ClientPredictionReconciler _reconciler = new();
        private readonly PlayerSessionId _remoteSessionId;
        private readonly Vector3 _facing;
        private readonly int _cubeId;
        private ulong? _latestAck;
        private bool _measuring;
        private float _maxCorrection;
        private double _totalCorrection;
        private int _snaps;
        private int _acks;
        private Vector3? _clientCubeStart;
        private int? _firstCubeMoveAck;

        public PushClient(RemoteClientSession session, int floorSize, Vector3 facing, int cubeId)
        {
            _session = session;
            _remoteSessionId = new PlayerSessionId(session.SessionId);
            _facing = facing;
            _cubeId = cubeId;
            _world = new ClientWorldSession(floorSize, new Vector3(5f, 1.5f, 5f), new SceneEventHub());
            Ticks = new ClientCommandTickStream(_world.LocalPlayerSessionId);
            Ticks.ObserveSendBacklog(() => _session.PendingCommandCount);
            Ticks.ObserveAcknowledgedSequence(() => _latestAck);
        }

        public ClientCommandTickStream Ticks { get; }

        public bool SelfSeen { get; private set; }

        public bool Walking { get; set; }

        public Vector3 PlayerPosition => _world.Player.GetWorldPosition();

        public void StartMeasuring()
        {
            _measuring = true;
            _clientCubeStart = ClientCube()?.Position;
        }

        public void Dispose()
        {
            _world.Dispose();
        }

        private Cube? ClientCube() =>
            _world.Map.TryGetCubeByReplicatedId(_cubeId, out Cube? cube) ? cube : null;

        public void Frame(double elapsedSeconds)
        {
            _world.Player.NudgeWorldPosition(_reconciler.TakeSmoothingStep((float)elapsedSeconds));
            _world.Player.SetFacing(_facing);
            Ticks.CaptureInput(new PlayerCommandFrame(
                _world.LocalPlayerSessionId,
                0,
                PlayerModeSwitchIntent.None,
                Walking ? new PlayerMoveIntent(1f, 0f) : new PlayerMoveIntent(0f, 0f),
                new PlayerLookIntent(0f, 0f, _facing),
                PrimaryUsePressed: false));
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
                    // The relayed command carries the camera's front as its facing.
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
                    if (actor.Kind == Dream.Simulation.Combat.CreatureKind.Player && actor.SessionId == _session.SessionId)
                    {
                        SelfSeen = true;
                    }
                }

                if (_measuring && snapshot.LastAcceptedCommandSequences.ContainsKey(_session.SessionId))
                {
                    float correction = Math.Max(jump, smooth);
                    _maxCorrection = Math.Max(_maxCorrection, correction);
                    _totalCorrection += correction;
                    _acks++;
                    if (jump > 0.01f)
                    {
                        _snaps++;
                    }

                    if (ClientCube() is { } cube && _clientCubeStart is { } cubeStart && _firstCubeMoveAck == null &&
                        (cube.Position - cubeStart).Xz.Length > 0.05f)
                    {
                        _firstCubeMoveAck = _acks;
                    }
                }
            }
        }

        public PushReport CreateReport(bool freeze, bool frozenBeforePush, Vector3 serverStart, Vector3 serverEnd)
        {
            Vector3 clientEnd = ClientCube()?.Position ?? Vector3.Zero;
            float clientMoved = _clientCubeStart is { } start ? (clientEnd - start).Xz.Length : float.NaN;
            return new PushReport(
                freeze,
                frozenBeforePush,
                _maxCorrection,
                (float)_totalCorrection,
                _snaps,
                _acks,
                (serverEnd - serverStart).Xz.Length,
                clientMoved,
                (clientEnd - serverEnd).Xz.Length,
                _firstCubeMoveAck ?? -1);
        }
    }
}
