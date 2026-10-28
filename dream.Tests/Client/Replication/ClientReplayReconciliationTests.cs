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
    private const int StallTicks = 64;
    private const int UnacknowledgedTicks = 6;

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
        Assert.True(miss < 0.05f, $"snap landed {miss} m from where the server went");
        // The old snap put the player on the acknowledged server position, a further
        // UnacknowledgedTicks of walking behind.
        Assert.True((server[ackIndex] - serverAfterReplay).Xz.Length > 0.3f);
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
        WorldSessionRuntime runtime = new(48, new Vector3(5f, 1.5f, 5f), new SceneEventHub());
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
        WorldSessionRuntime runtime = new(48, new Vector3(5f, 1.5f, 5f), new SceneEventHub());
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

    // The startup terrain blocks some directions; walk along the most open axis. Probed once per
    // walk length for the class (ARCH-1c): the probe is deterministic set-up.
    private static readonly ProbeCache<int, Vector3> OpenFacings = new(ProbeMostOpenFacing);

    private static Vector3 MostOpenFacing(int ticks) => OpenFacings.Get(ticks);

    private static Vector3 ProbeMostOpenFacing(int ticks)
    {
        return new[] { Vector3.UnitZ, -Vector3.UnitZ, Vector3.UnitX, -Vector3.UnitX }
            .OrderByDescending(direction =>
            {
                List<Vector3> probe = RunWalker(ticks, direction, ServerFeed.AllCommands);
                return (probe[^1] - probe[0]).Xz.Length;
            })
            .First();
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
