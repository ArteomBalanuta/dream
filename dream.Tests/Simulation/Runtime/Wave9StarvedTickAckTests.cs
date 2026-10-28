using Dream.Client.Replication;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using OpenTK.Mathematics;
using Xunit;
using Xunit.Abstractions;

namespace Dream.Tests.Simulation.Runtime;

// Wave 9 tester, NETR-7 x NETR-3/4: the cause of the "late corrections" at 32 Hz.
//
// A snapshot carries the session's last accepted command sequence (the ack) and the player's
// position at the snapshot tick. When the server runs a tick with no new command from the client
// (a starved tick: it repeats the last command, NETR-3), the ack stays at the last command but the
// position moves on. At 64 Hz the snapshot of the acked tick goes out before the starved tick, and
// the starved snapshot repeats the ack, which ClientPredictionReconciler ignores
// (_lastComparedSequence). At 32 Hz (SnapshotIntervalTicks 2) the snapshot is taken every other
// tick, so when the client's last command is consumed on the first tick of an interval and the
// second tick is starved, the FIRST snapshot with that ack already holds one extrapolated tick. The
// reconciler compares it with the prediction for the ack and "corrects" by speed x 1 tick, and the
// next ack corrects back. Measured over loopback (Wave9NetworkMeasurementTests): 0.093 m at walking
// speed after a send or network stall (0.000 m at 64 Hz), 0.11-0.57 m pairs in the hitchy loop once
// the player is falling (the wave-8 harness walks off the world edge), up to speed x k for an
// interval or a catch-up advance holding k starved ticks.
public class Wave9StarvedTickAckTests
{
    private const string Session = "w9-starved";

    private readonly ITestOutputHelper _output;

    public Wave9StarvedTickAckTests(ITestOutputHelper output)
    {
        _output = output;
    }

    // The server side, measured: after a starved tick the ack is unchanged and the position has
    // moved by one more tick of walking.
    [Fact]
    public void SnapshotAfterAStarvedTick_KeepsTheAck_ButHoldsAnExtrapolatedPosition()
    {
        (Vector3 atAck, ServerSnapshotEnvelope starved) = RunWalkThenStarve(walkTicks: 32);

        Assert.Equal(32UL, starved.LastAcceptedCommandSequences[Session]);
        Vector3 inSnapshot = Assert.Single(starved.Actors, actor => actor.SessionId == Session).Position;
        float extrapolated = (inSnapshot - atAck).Xz.Length;
        _output.WriteLine($"position at the acked tick {atAck}, in the snapshot after one starved tick {inSnapshot}: {extrapolated:F3} m apart");
        Assert.True(extrapolated > 0.05f, $"only {extrapolated} m");
    }

    // The client side, measured: a client whose prediction matched the server exactly is still
    // "corrected" by that extrapolated tick (a Smooth correction above the deadband).
    [Fact]
    public void PerfectPrediction_IsCorrectedByTheStarvedTick_WhenTheSnapshotFollowsIt()
    {
        (Vector3 atAck, ServerSnapshotEnvelope starved, IReadOnlyList<Vector3> path) = RunWalkThenStarveWithPath(walkTicks: 32);
        ClientPredictionReconciler reconciler = new();
        for (int i = 0; i < path.Count; i++)
        {
            reconciler.RecordPrediction((ulong)(i + 1), path[i]);
        }

        Assert.Equal(atAck, path[31]);
        Vector3 inSnapshot = Assert.Single(starved.Actors, actor => actor.SessionId == Session).Position;
        ClientReconciliationResult result = reconciler.Reconcile(starved.LastAcceptedCommandSequences[Session], inSnapshot);
        _output.WriteLine($"perfect prediction corrected by {result.Error.Length:F3} m ({result.Kind})");
        Assert.Equal(ClientReconciliationKind.Smooth, result.Kind);
    }

    // What it should be (W9-2): a prediction that matched the server's simulation of every acked
    // command is never corrected, whichever tick the snapshot was taken on.
    [Fact(Skip = "W9-2: a snapshot taken on a starved tick pairs the old ack with an extrapolated position")]
    public void PerfectPrediction_IsNotCorrected_ByASnapshotTakenOnAStarvedTick()
    {
        (_, ServerSnapshotEnvelope starved, IReadOnlyList<Vector3> path) = RunWalkThenStarveWithPath(walkTicks: 32);
        ClientPredictionReconciler reconciler = new();
        for (int i = 0; i < path.Count; i++)
        {
            reconciler.RecordPrediction((ulong)(i + 1), path[i]);
        }

        Vector3 inSnapshot = Assert.Single(starved.Actors, actor => actor.SessionId == Session).Position;
        ClientReconciliationResult result = reconciler.Reconcile(starved.LastAcceptedCommandSequences[Session], inSnapshot);
        Assert.Equal(ClientReconciliationKind.None, result.Kind);
    }

    private static (Vector3 AtAck, ServerSnapshotEnvelope Starved) RunWalkThenStarve(int walkTicks)
    {
        (Vector3 atAck, ServerSnapshotEnvelope starved, _) = RunWalkThenStarveWithPath(walkTicks);
        return (atAck, starved);
    }

    // Walks walkTicks ticks with one command per tick (sequence == tick), recording the position
    // after each, then runs one tick with no command and exports the snapshot a 32 Hz server would
    // send after that second tick of its interval.
    private static (Vector3 AtAck, ServerSnapshotEnvelope Starved, IReadOnlyList<Vector3> Path) RunWalkThenStarveWithPath(int walkTicks)
    {
        WorldSessionRuntime runtime = new(48, new Vector3(5f, 1.5f, 5f), new SceneEventHub());
        using RuntimeAuthoritativeSessionHost host = new(runtime);
        host.JoinSession(Session, Session);
        runtime.Enemies.Clear();
        PlayerSessionId id = new(Session);
        Vector3 facing = OpenFacing(runtime);
        List<Vector3> path = [];
        for (ulong tick = 1; tick <= (ulong)walkTicks; tick++)
        {
            host.AdvanceTick(
                tick,
                SimulationClock.FixedDeltaSeconds,
                [new PlayerCommandFrame(id, tick, PlayerModeSwitchIntent.None, new PlayerMoveIntent(1f, 0f), new PlayerLookIntent(0f, 0f, facing), PrimaryUsePressed: false)],
                PlayerSimulationTuning.AuthoritativeDefault,
                allowGameplaySimulation: false);
            path.Add(Position(runtime));
        }

        Vector3 atAck = path[^1];
        host.AdvanceTick(
            (ulong)walkTicks + 1,
            SimulationClock.FixedDeltaSeconds,
            [],
            PlayerSimulationTuning.AuthoritativeDefault,
            allowGameplaySimulation: false);
        return (atAck, host.ExportSnapshot(), path);
    }

    private static Vector3 Position(WorldSessionRuntime runtime) =>
        Assert.Single(runtime.CaptureActorSnapshots(), actor => actor.SessionId == Session).Position;

    // A facing whose 40-tick walk stays on the floor and is not blocked.
    private static Vector3 OpenFacing(WorldSessionRuntime template)
    {
        foreach (Vector3 facing in new[] { Vector3.UnitX, Vector3.UnitZ, -Vector3.UnitX, -Vector3.UnitZ })
        {
            WorldSessionRuntime probe = new(template.Map.Width, new Vector3(5f, 1.5f, 5f), new SceneEventHub());
            using RuntimeAuthoritativeSessionHost host = new(probe);
            host.JoinSession(Session, Session);
            probe.Enemies.Clear();
            Vector3 start = Position(probe);
            for (ulong tick = 1; tick <= 40; tick++)
            {
                host.AdvanceTick(
                    tick,
                    SimulationClock.FixedDeltaSeconds,
                    [new PlayerCommandFrame(new PlayerSessionId(Session), tick, PlayerModeSwitchIntent.None, new PlayerMoveIntent(1f, 0f), new PlayerLookIntent(0f, 0f, facing), PrimaryUsePressed: false)],
                    PlayerSimulationTuning.AuthoritativeDefault,
                    allowGameplaySimulation: false);
            }

            Vector3 end = Position(probe);
            if ((end - start).Xz.Length > 2.5f && MathF.Abs(end.Y - start.Y) < 0.1f)
            {
                return facing;
            }
        }

        throw new InvalidOperationException("No open walk from the spawn.");
    }
}
