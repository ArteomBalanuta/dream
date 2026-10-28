using Dream.Client.Replication;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Tests.Utilities;
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
//
// Fixed (wave 9 fix loop 1): each player actor carries StarvedTicks, the ticks simulated since its
// ack, and the reconciler compares with its prediction that many ticks after the ack. The
// measurement above still holds for a caller that leaves starvedTicks out.
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
    // command is never corrected, whichever tick the snapshot was taken on. The fix: the snapshot
    // says how many starved ticks its position holds (ActorSnapshot.StarvedTicks) and the client
    // compares with its prediction that many ticks after the ack, as ClientCorrectionApplier does.
    // Here the client has not predicted the starved tick, so it is extrapolated.
    [Fact]
    public void PerfectPrediction_IsNotCorrected_ByASnapshotTakenOnAStarvedTick()
    {
        (_, ServerSnapshotEnvelope starved, IReadOnlyList<Vector3> path) = RunWalkThenStarveWithPath(walkTicks: 32);
        ClientPredictionReconciler reconciler = new();
        for (int i = 0; i < path.Count; i++)
        {
            reconciler.RecordPrediction((ulong)(i + 1), path[i]);
        }

        ActorSnapshot actor = Assert.Single(starved.Actors, actor => actor.SessionId == Session);
        Assert.Equal(1, actor.StarvedTicks);
        ClientReconciliationResult result = reconciler.Reconcile(
            starved.LastAcceptedCommandSequences[Session],
            actor.Position,
            starvedTicks: actor.StarvedTicks);
        _output.WriteLine($"perfect prediction, starved tick extrapolated: error {result.Error.Length:F4} m ({result.Kind})");
        Assert.Equal(ClientReconciliationKind.None, result.Kind);
    }

    // The client usually has predicted past the ack already: its own sample for the starved tick
    // (the same walk the server repeated) is what the snapshot is compared with, exactly.
    [Fact]
    public void PerfectPrediction_ComparesWithTheClientSampleForTheStarvedTick()
    {
        (Vector3 atAck, ServerSnapshotEnvelope starved, IReadOnlyList<Vector3> path) = RunWalkThenStarveWithPath(walkTicks: 32);
        ActorSnapshot actor = Assert.Single(starved.Actors, actor => actor.SessionId == Session);
        ClientPredictionReconciler reconciler = new();
        for (int i = 0; i < path.Count; i++)
        {
            reconciler.RecordPrediction((ulong)(i + 1), path[i]);
        }

        // The client walked tick 33 with the command the server repeated, and is a tick further on.
        reconciler.RecordPrediction(33, actor.Position);
        reconciler.RecordPrediction(34, actor.Position + (actor.Position - atAck));
        ClientReconciliationResult result = reconciler.Reconcile(32, actor.Position, starvedTicks: actor.StarvedTicks);
        Assert.Equal(ClientReconciliationKind.None, result.Kind);
        Assert.Equal(Vector3.Zero, result.Error);
    }

    // Snapshots on the tick that consumed the command hold no starved tick (the field is left off
    // the wire and the comparison is the acknowledged sample, as before the fix); each starved tick
    // after it counts one more under the same ack.
    [Fact]
    public void StarvedTicks_CountTheTicksSimulatedSinceTheAck()
    {
        WorldSessionRuntime runtime = new(48, new Vector3(5f, 1.5f, 5f), new SceneEventHub());
        using RuntimeAuthoritativeSessionHost host = new(runtime);
        host.JoinSession(Session, Session);
        runtime.Enemies.Clear();
        Assert.Equal(0, SessionActor(host.ExportSnapshot()).StarvedTicks);
        for (ulong tick = 1; tick <= 3; tick++)
        {
            host.AdvanceTick(tick, SimulationClock.FixedDeltaSeconds, [Walk(tick, Vector3.UnitX)], PlayerSimulationTuning.AuthoritativeDefault, allowGameplaySimulation: false);
            Assert.Equal(0, SessionActor(host.ExportSnapshot()).StarvedTicks);
        }

        for (int starvedTick = 1; starvedTick <= 20; starvedTick++)
        {
            host.AdvanceTick(3UL + (ulong)starvedTick, SimulationClock.FixedDeltaSeconds, [], PlayerSimulationTuning.AuthoritativeDefault, allowGameplaySimulation: false);
            ServerSnapshotEnvelope snapshot = host.ExportSnapshot();
            Assert.Equal(3UL, snapshot.LastAcceptedCommandSequences[Session]);
            Assert.Equal(starvedTick, SessionActor(snapshot).StarvedTicks);
        }

        host.AdvanceTick(24, SimulationClock.FixedDeltaSeconds, [Walk(24, Vector3.UnitX)], PlayerSimulationTuning.AuthoritativeDefault, allowGameplaySimulation: false);
        Assert.Equal(0, SessionActor(host.ExportSnapshot()).StarvedTicks);
    }

    // A secondary seat (any session but the runtime's primary one) counts its own starved ticks;
    // the other seats' commands do not reset them.
    [Fact]
    public void StarvedTicks_AreCountedPerSession_ForSecondarySeats()
    {
        const string Other = "w9-starved-other";
        WorldSessionRuntime runtime = new(48, new Vector3(5f, 1.5f, 5f), new SceneEventHub());
        using RuntimeAuthoritativeSessionHost host = new(runtime);
        host.JoinSession(Session, Session);
        host.JoinSession(Other, Other);
        runtime.Enemies.Clear();
        PlayerCommandFrame OtherWalk(ulong sequence) => Walk(sequence, -Vector3.UnitX) with { SessionId = new PlayerSessionId(Other) };
        for (ulong tick = 1; tick <= 3; tick++)
        {
            host.AdvanceTick(tick, SimulationClock.FixedDeltaSeconds, [Walk(tick, Vector3.UnitX), OtherWalk(tick)], PlayerSimulationTuning.AuthoritativeDefault, allowGameplaySimulation: false);
        }

        ServerSnapshotEnvelope fed = host.ExportSnapshot();
        Assert.All(fed.Actors, actor => Assert.Equal(0, actor.StarvedTicks));
        for (ulong tick = 4; tick <= 5; tick++)
        {
            host.AdvanceTick(tick, SimulationClock.FixedDeltaSeconds, [Walk(tick, Vector3.UnitX)], PlayerSimulationTuning.AuthoritativeDefault, allowGameplaySimulation: false);
        }

        ServerSnapshotEnvelope otherStarved = host.ExportSnapshot();
        Assert.Equal(0, SessionActor(otherStarved).StarvedTicks);
        Assert.Equal(3UL, otherStarved.LastAcceptedCommandSequences[Other]);
        Assert.Equal(2, Assert.Single(otherStarved.Actors, actor => actor.SessionId == Other).StarvedTicks);
    }

    // While falling a starved tick moves the player further than a walking one. The client's own
    // samples for the repeated ticks match exactly; extrapolating them (not yet predicted) stays
    // within the deadband.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FallingPrediction_IsNotCorrected_ByASnapshotTakenAfterStarvedTicks(bool clientPredictedTheStarvedTicks)
    {
        WorldSessionRuntime runtime = new(48, new Vector3(5f, 1.5f, 5f), new SceneEventHub());
        using RuntimeAuthoritativeSessionHost host = new(runtime);
        host.JoinSession(Session, Session);
        runtime.Enemies.Clear();
        runtime.Player.SetWorldPosition(new Vector3(5f, 30f, 5f));
        ClientPredictionReconciler reconciler = new();
        for (ulong tick = 1; tick <= 16; tick++)
        {
            host.AdvanceTick(tick, SimulationClock.FixedDeltaSeconds, [Walk(tick, Vector3.UnitX)], PlayerSimulationTuning.AuthoritativeDefault, allowGameplaySimulation: false);
            reconciler.RecordPrediction(tick, Position(runtime));
        }

        Vector3 atAck = Position(runtime);
        for (ulong tick = 17; tick <= 18; tick++)
        {
            host.AdvanceTick(tick, SimulationClock.FixedDeltaSeconds, [], PlayerSimulationTuning.AuthoritativeDefault, allowGameplaySimulation: false);
            if (clientPredictedTheStarvedTicks)
            {
                // A replica simulating the same repeated command lands on the same samples.
                reconciler.RecordPrediction(tick, Position(runtime));
            }
        }

        ActorSnapshot actor = SessionActor(host.ExportSnapshot());
        Assert.Equal(2, actor.StarvedTicks);
        Assert.True(atAck.Y - actor.Position.Y > 0.05f, $"not falling: {atAck} -> {actor.Position}");
        ClientReconciliationResult result = reconciler.Reconcile(16, actor.Position, starvedTicks: actor.StarvedTicks);
        _output.WriteLine($"falling, 2 starved ticks, predicted {clientPredictedTheStarvedTicks}: {(actor.Position - atAck).Length:F3} m moved, error {result.Error.Length:F4} m ({result.Kind})");
        Assert.Equal(ClientReconciliationKind.None, result.Kind);
        if (clientPredictedTheStarvedTicks)
        {
            Assert.Equal(Vector3.Zero, result.Error);
        }
    }

    private static PlayerCommandFrame Walk(ulong sequence, Vector3 facing) =>
        new(new PlayerSessionId(Session), sequence, PlayerModeSwitchIntent.None, new PlayerMoveIntent(1f, 0f), new PlayerLookIntent(0f, 0f, facing), PrimaryUsePressed: false);

    private static ActorSnapshot SessionActor(ServerSnapshotEnvelope snapshot) =>
        Assert.Single(snapshot.Actors, actor => actor.SessionId == Session);

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

    // A facing whose 40-tick walk stays on the floor and is not blocked. It depends only on the
    // floor size, so it is probed once per size for the class (ARCH-1c).
    private static readonly ProbeCache<int, Vector3> OpenFacings = new(ProbeOpenFacing);

    private static Vector3 OpenFacing(WorldSessionRuntime template) => OpenFacings.Get(template.Map.Width);

    private static Vector3 ProbeOpenFacing(int floorSize)
    {
        foreach (Vector3 facing in new[] { Vector3.UnitX, Vector3.UnitZ, -Vector3.UnitX, -Vector3.UnitZ })
        {
            WorldSessionRuntime probe = new(floorSize, new Vector3(5f, 1.5f, 5f), new SceneEventHub());
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
