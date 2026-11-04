using OpenTK.Mathematics;
using Xunit;
using Xunit.Abstractions;
using Dream.Client.Networking;
using Dream.Client.Replication;
using Dream.Client.World;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Combat;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Tests.Utilities;

namespace Dream.Tests.Client.Replication;

// NETR-4 reconciliation against the real server simulation. The client records one prediction
// per tick, keyed by the tick's command sequence; a correction replays the unacknowledged ticks
// from the server state.
public class ClientReplayReconciliationTests(ITestOutputHelper output)
{
    private const string Steady = "session-a";
    private const string Walker = "session-b";
    private const int WalkTicks = 2;
    // The snap threshold is a distance (3 m), so how long a stall has to be before the ack after it
    // snaps depends on the cruise speed. At the old flat 6 m/s a 64-tick (1 s) stall diverged 4.5 m;
    // at the 4.3 m/s run cruise the same stall is 2.99 m, just under the threshold, and the ack
    // smooths instead. 80 ticks (1.25 s) puts this scenario back where it was testing the snap.
    private const int StallTicks = 80;
    private const int UnacknowledgedTicks = 6;

    private const int FloorSize = 48;
    // Far enough from every map edge that a full walk cannot reach the bounds clamp (Player's
    // EdgeMargin keeps the body centre in [0.5, Width-0.5]). The old (5, 1.5, 5) resolved to a
    // spawn 6.3 m from the west edge, which is shorter than one walk, so the walker ran into the
    // edge and the measurement became a wall slide instead of a reconciliation.
    private static readonly Vector3 WalkerStart = new(36f, 1.5f, 36f);
    // The motor accelerates over the first ticks, so a free walk falls a little short of
    // ticks x MoveSpeed x dt, by a fixed amount independent of the tick count: 0.43 m observed
    // from the solver's own settling, plus the explicit ground ramp's v^2 / 2a =
    // 4.3^2 / (2 x 18) = 0.51 m. 1.2 covers the 0.94 m sum with a little room.
    private const float AccelerationRampMeters = 1.2f;
    private const float StraightLaneDriftMeters = 0.01f;

    private static readonly PlayerSimulationTuning Tuning = PlayerSimulationTuning.AuthoritativeDefault;

    // The server loses most of a one-second stall (NETR-3 idles the seat after 16 ticks), so the
    // ack after the resume snaps. The snap must land where the server will be once it simulates
    // the client's unacknowledged commands, one per tick, from its acknowledged state.
    [Fact]
    public void SnapAfterStall_ReplaysUnacknowledgedTicks_AndLandsWhereTheServerGoes()
    {
        Vector3 facing = MostOpenFacing(WalkTicks + StallTicks + UnacknowledgedTicks + 2);
        int ackIndex = WalkTicks + StallTicks;
        int totalTicks = ackIndex + 1 + UnacknowledgedTicks;
        List<Vector3> predicted = RunWalker(totalTicks, facing, ServerFeed.AllCommands);
        List<Vector3> server = RunWalker(totalTicks, facing, ServerFeed.StallThenLatestOnly);

        ClientPredictionReconciler reconciler = new();
        for (int index = 0; index < totalTicks; index++)
        {
            reconciler.RecordPrediction((ulong)(index + 1), predicted[index]);
        }

        ulong ack = (ulong)(ackIndex + 1);
        ClientReconciliationResult result = reconciler.Reconcile(ack, server[ackIndex]);

        Vector3 landing = result.SnapPosition;
        Vector3 serverAfterReplay = server[^1];
        float miss = (landing - serverAfterReplay).Xz.Length;
        output.WriteLine(
            $"error at ack {result.Error.Xz.Length:F3} m; snap landing {landing}, server after {UnacknowledgedTicks} more ticks {serverAfterReplay}, miss {miss:F4} m");
        Assert.Equal(ClientReconciliationKind.Snap, result.Kind);
        // One sample per tick from the ack on: the acknowledged one plus each unacknowledged tick.
        Assert.Equal(1 + UnacknowledgedTicks, reconciler.HistoryCount);
        AssertVector(server[ackIndex] + (predicted[^1] - predicted[ackIndex]), landing);
        // The old snap put the player on the acknowledged server position; the replay carries the
        // client's unacknowledged travel on top of it, a whole window of cruise further on.
        float replayGain = (landing - server[ackIndex]).Xz.Length;
        Assert.True(replayGain > 0.3f, $"replay only moved the snap {replayGain} m past the ack");
        // It lands a little ahead of where the server actually goes, and cannot do better: the
        // server idled the seat through the stall, so it resumes from rest and ramps back up while
        // the client body never stopped cruising, and a replay that translates the client's path
        // cannot model that velocity gap. Over these 6 ticks it is the client's cruise travel
        // (6/64 x 4.3 = 0.40 m) less the server's ramp-up (0.09 m), about 0.31 m; the reconciler
        // smooths the remainder away. Before the ramp both sides were at cruise instantly and the
        // translation was exact, which is what the old 0.05 m tolerance measured.
        Assert.True(miss < 0.35f, $"snap landed {miss} m from where the server went");
    }

    [Fact]
    public void Snap_ZeroesThePredictedVelocity()
    {
        using ClientWorldSession runtime = CreateRuntime();
        ClientPredictionReconciler reconciler = new();
        Vector3 start = runtime.Player.GetWorldPosition();
        reconciler.RecordPrediction(3, start);
        reconciler.RecordPrediction(4, start + new Vector3(0.1f, 0f, 0f));
        runtime.Player.PhysicsBody.LinearVelocity = new BulletSharp.Math.Vector3(18f, 4f, -6f);
        Vector3 server = new(8f, 1.5f, 8f);

        ClientCorrectionApplier.ApplySnapshotToRuntime(
            runtime,
            CreateLocalSnapshot(runtime, server, acknowledgedSequence: 3),
            runtime.LocalPlayerSessionId.Value,
            reconciler);

        AssertVector(server + new Vector3(0.1f, 0f, 0f), runtime.Player.GetWorldPosition());
        Assert.Equal(0f, runtime.Player.PhysicsBody.LinearVelocity.Length, 4);
        Assert.Equal(Vector3.Zero, reconciler.PendingOffset);
    }

    // While the client's commands are stalled, every snapshot repeats the last ack but the server
    // keeps moving the player (it repeats the last command, then idles). Those positions are not
    // the result of the acknowledged command, so they must not correct the prediction; before,
    // each one was smoothed in and the real ack after the stall then counted the same ticks again
    // (6.3 m instead of 4.5 m in the loopback send-stall test).
    [Fact]
    public void RepeatedAck_WithTheServerExtrapolating_IsNotCorrected()
    {
        ClientPredictionReconciler reconciler = new();
        for (ulong sequence = 1; sequence <= 30; sequence++)
        {
            reconciler.RecordPrediction(sequence, new Vector3(sequence * 0.1f, 0f, 0f));
        }

        Assert.Equal(ClientReconciliationKind.None, reconciler.Reconcile(10, new Vector3(1f, 0f, 0f)).Kind);
        for (int extrapolated = 1; extrapolated <= 8; extrapolated++)
        {
            ClientReconciliationResult repeated = reconciler.Reconcile(10, new Vector3(1f + extrapolated * 0.1f, 0f, 0f));
            Assert.Equal(ClientReconciliationKind.None, repeated.Kind);
        }

        Assert.Equal(Vector3.Zero, reconciler.PendingOffset);
        // The next real ack is compared with the untouched prediction for it.
        ClientReconciliationResult next = reconciler.Reconcile(20, new Vector3(1.9f, 0f, 0f));
        Assert.Equal(ClientReconciliationKind.Smooth, next.Kind);
        AssertVector(new Vector3(-0.1f, 0f, 0f), next.Error);
    }

    // A snap while a smooth correction is still draining replaces it instead of adding to it.
    [Fact]
    public void Snap_DiscardsPendingSmoothing()
    {
        // The replay is under test here, not the discard of far snaps.
        ClientPredictionReconciler reconciler = new(replayDiscardDistance: float.PositiveInfinity);
        for (ulong sequence = 1; sequence <= 6; sequence++)
        {
            reconciler.RecordPrediction(sequence, new Vector3(sequence * 0.1f, 0f, 0f));
        }

        Assert.Equal(ClientReconciliationKind.Smooth, reconciler.Reconcile(2, new Vector3(0.6f, 0f, 0f)).Kind);
        Assert.NotEqual(Vector3.Zero, reconciler.PendingOffset);

        ClientReconciliationResult snap = reconciler.Reconcile(4, new Vector3(10f, 0f, 0f));

        Assert.Equal(ClientReconciliationKind.Snap, snap.Kind);
        AssertVector(new Vector3(10.2f, 0f, 0f), snap.SnapPosition);
        Assert.Equal(Vector3.Zero, reconciler.PendingOffset);
        Assert.Equal(Vector3.Zero, reconciler.TakeSmoothingStep(1f));
    }

    // Measures the snap-back after a one-second send stall at the client (it keeps predicting and
    // queueing; nothing reaches the server) for the current latest-only server inbox and for a
    // stand-in of NETR-5's buffered inbox (a ring of 8, one command per tick). The client side is
    // the real NETR-4 outbound queue, so the burst after the stall is its 16 commands, the oldest
    // merged. Report only: NETR-5's real inbox is not on this branch.
    [Fact]
    public void OneSecondSendStall_CorrectionDistance_LatestOnlyVersusBufferedInbox()
    {
        Vector3 facing = MostOpenFacing(WalkTicks + StallTicks + 24);
        float latestOnly = MeasureSendStallCorrection(facing, ringCapacity: 0);
        float buffered = MeasureSendStallCorrection(facing, ringCapacity: 8);

        output.WriteLine($"1 s send stall, latest-only inbox: max correction {latestOnly:F2} m");
        output.WriteLine($"1 s send stall, ring-of-8 inbox stand-in: max correction {buffered:F2} m");
        Assert.True(latestOnly > 0f && buffered > 0f);
    }

    private static float MeasureSendStallCorrection(Vector3 facing, int ringCapacity)
    {
        WorldSessionRuntime runtime = new(FloorSize, WalkerStart, new SceneEventHub());
        using RuntimeAuthoritativeSessionHost host = new(runtime);
        host.JoinSession(Steady, Steady);
        host.JoinSession(Walker, Walker);
        runtime.Enemies.Clear();

        // The client predicts with every command, so its prediction for tick t is the
        // all-commands run.
        int predictedTicks = WalkTicks + StallTicks + 24;
        List<Vector3> predicted = RunWalker(predictedTicks, facing, ServerFeed.AllCommands);
        ClientOutboundCommandQueue outbound = new();
        Queue<PlayerCommandFrame> serverRing = new();
        PlayerCommandFrame? latest = null;
        ulong lastAccepted = 0;
        float maxError = 0f;
        for (int tick = 1; tick <= predictedTicks; tick++)
        {
            outbound.TryEnqueue(Command(Walker, (ulong)tick, 1f, facing));
            bool stalled = tick > WalkTicks && tick <= WalkTicks + StallTicks;
            if (!stalled)
            {
                // The transport delivers everything queued; after the stall that is the burst.
                while (outbound.TryDequeue(out PlayerCommandFrame sent))
                {
                    if (ringCapacity == 0)
                    {
                        latest = latest.HasValue ? ClientOutboundCommandQueue.Merge(latest.Value, sent) : sent;
                    }
                    else
                    {
                        if (serverRing.Count == ringCapacity)
                        {
                            // Stand-in overflow policy: fold the oldest into the next one.
                            PlayerCommandFrame oldest = serverRing.Dequeue();
                            PlayerCommandFrame next = serverRing.Dequeue();
                            PlayerCommandFrame[] rest = serverRing.ToArray();
                            serverRing.Clear();
                            serverRing.Enqueue(ClientOutboundCommandQueue.Merge(oldest, next));
                            foreach (PlayerCommandFrame frame in rest)
                            {
                                serverRing.Enqueue(frame);
                            }
                        }

                        serverRing.Enqueue(sent);
                    }
                }
            }

            List<PlayerCommandFrame> commands = [Command(Steady, (ulong)tick, 0f, facing)];
            if (ringCapacity == 0 && latest.HasValue)
            {
                commands.Add(latest.Value);
                lastAccepted = latest.Value.Sequence;
                latest = null;
            }
            else if (ringCapacity > 0 && serverRing.Count > 0)
            {
                PlayerCommandFrame popped = serverRing.Dequeue();
                commands.Add(popped);
                lastAccepted = popped.Sequence;
            }

            host.AdvanceTick((ulong)tick, SimulationClock.FixedDeltaSeconds, commands, Tuning, allowGameplaySimulation: false);
            if (tick > WalkTicks + StallTicks && lastAccepted > 0)
            {
                Vector3 serverPosition = Assert.Single(runtime.CaptureActorSnapshots(), actor => actor.SessionId == Walker).Position;
                float error = (serverPosition - predicted[(int)lastAccepted - 1]).Xz.Length;
                maxError = Math.Max(maxError, error);
            }
        }

        return maxError;
    }

    private enum ServerFeed
    {
        AllCommands,
        // Nothing for StallTicks after WalkTicks, then the newest command only (the latest-only
        // inbox merges the backlog), then every command again.
        StallThenLatestOnly
    }

    private static List<Vector3> RunWalker(int totalTicks, Vector3 facing, ServerFeed feed)
    {
        WorldSessionRuntime runtime = new(FloorSize, WalkerStart, new SceneEventHub());
        using RuntimeAuthoritativeSessionHost host = new(runtime);
        host.JoinSession(Steady, Steady);
        host.JoinSession(Walker, Walker);
        runtime.Enemies.Clear();
        List<Vector3> positions = [];
        for (int tick = 1; tick <= totalTicks; tick++)
        {
            List<PlayerCommandFrame> commands = [Command(Steady, (ulong)tick, 0f, facing)];
            bool stalled = feed == ServerFeed.StallThenLatestOnly && tick > WalkTicks && tick <= WalkTicks + StallTicks;
            if (!stalled)
            {
                commands.Add(Command(Walker, (ulong)tick, 1f, facing));
            }

            host.AdvanceTick((ulong)tick, SimulationClock.FixedDeltaSeconds, commands, Tuning, allowGameplaySimulation: false);
            positions.Add(Assert.Single(runtime.CaptureActorSnapshots(), actor => actor.SessionId == Walker).Position);
        }

        return positions;
    }

    // The startup terrain blocks some directions; walk along an axis that is a straight, open lane
    // for the whole walk. Total displacement alone is not enough: a walker pressed against terrain
    // or against the map-bounds clamp still covers ground while sliding sideways, and then the
    // measurement is that slide rather than the reconciliation. So a lane qualifies only when it
    // drifts under a centimetre sideways and covers what a free walk covers. Probed once per walk
    // length for the class (ARCH-1c): the probe is deterministic set-up.
    private static readonly ProbeCache<int, Vector3> OpenFacings = new(ProbeStraightLaneFacing);

    private static Vector3 MostOpenFacing(int ticks) => OpenFacings.Get(ticks);

    private static Vector3 ProbeStraightLaneFacing(int ticks)
    {
        float freeWalkMeters = ticks * Tuning.MoveSpeed * SimulationClock.FixedDeltaSeconds;
        float minimumAlong = freeWalkMeters - AccelerationRampMeters;
        List<string> rejected = [];
        Vector3 best = Vector3.Zero;
        float bestAlong = 0f;
        foreach (Vector3 direction in new[] { Vector3.UnitZ, -Vector3.UnitZ, Vector3.UnitX, -Vector3.UnitX })
        {
            List<Vector3> probe = RunWalker(ticks, direction, ServerFeed.AllCommands);
            Vector3 travelled = probe[^1] - probe[0];
            float along = Vector3.Dot(travelled, direction);
            float lateral = (travelled - (along * direction)).Xz.Length;
            if (lateral < StraightLaneDriftMeters && along >= minimumAlong)
            {
                if (along > bestAlong)
                {
                    best = direction;
                    bestAlong = along;
                }

                continue;
            }

            rejected.Add($"{direction}: {along:F3} m along (needs {minimumAlong:F3}), {lateral:F3} m sideways");
        }

        if (bestAlong <= 0f)
        {
            throw new InvalidOperationException(
                $"No straight {freeWalkMeters:F2} m lane from {WalkerStart} in a {FloorSize}-cell world: "
                + string.Join("; ", rejected)
                + ". The world generation moved under this test; pick a start with an open lane.");
        }

        return best;
    }

    private static PlayerCommandFrame Command(string session, ulong sequence, float forward, Vector3 facing)
    {
        return new PlayerCommandFrame(
            new PlayerSessionId(session),
            sequence,
            new PlayerModeSwitchIntent(PlayerInteractionMode.Building),
            new PlayerMoveIntent(forward, 0f),
            new PlayerLookIntent(0f, 0f, facing),
            PrimaryUsePressed: false);
    }

    private static ServerSnapshotEnvelope CreateLocalSnapshot(ClientWorldSession runtime, Vector3 position, ulong acknowledgedSequence)
    {
        return new ServerSnapshotEnvelope(
            40,
            new Dictionary<string, ulong> { [runtime.LocalPlayerSessionId.Value] = acknowledgedSequence },
            [
                new ActorSnapshot(
                    runtime.Player.Combatant.Actor.Id,
                    CreatureKind.Player,
                    runtime.LocalPlayerSessionId.Value,
                    position,
                    Vector3.UnitZ,
                    100,
                    100,
                    true)
            ],
            PlayerInteractionMode.Building,
            null,
            Array.Empty<WorldMutationDelta>(),
            Array.Empty<DamageEventDelta>());
    }

    private static ClientWorldSession CreateRuntime()
    {
        return new ClientWorldSession(floorSize: 10, playerStartPosition: new Vector3(5f, 1.5f, 5f), new SceneEventHub());
    }

    private static void AssertVector(Vector3 expected, Vector3 actual)
    {
        Assert.Equal(expected.X, actual.X, 3);
        Assert.Equal(expected.Y, actual.Y, 3);
        Assert.Equal(expected.Z, actual.Z, 3);
    }
}
