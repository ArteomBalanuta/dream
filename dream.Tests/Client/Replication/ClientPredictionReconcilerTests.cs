using OpenTK.Mathematics;
using Xunit;
using Dream.Client.Replication;
using Dream.Client.World;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Combat;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;

namespace Dream.Tests.Client.Replication;

// The client used to hard-snap the local player to every snapshot. Snapshot positions trail the
// local prediction by the round trip, so the player was pulled back by speed × latency
// about 60 times a second, and velocity was zeroed each time.
public class ClientPredictionReconcilerTests
{
    [Fact]
    public void Reconcile_ServerBehindByLatencyButMatchingAckedPrediction_DoesNotCorrect()
    {
        ClientPredictionReconciler reconciler = new();
        RecordStraightRun(reconciler, firstSequence: 1, lastSequence: 10, stepPerTick: 0.3f);

        // The server has only processed command 5; the client is already at command 10.
        ClientReconciliationResult result = reconciler.Reconcile(5, PositionAt(5, 0.3f));

        Assert.Equal(ClientReconciliationKind.None, result.Kind);
        Assert.Equal(Vector3.Zero, reconciler.PendingOffset);
    }

    [Fact]
    public void Reconcile_SmallDivergence_SmoothsWithoutSnappingAndDoesNotDoubleCorrect()
    {
        ClientPredictionReconciler reconciler = new();
        RecordStraightRun(reconciler, firstSequence: 1, lastSequence: 10, stepPerTick: 0.1f);
        Vector3 error = new(0.4f, 0f, 0f);

        ClientReconciliationResult first = reconciler.Reconcile(4, PositionAt(4, 0.1f) + error);

        Assert.Equal(ClientReconciliationKind.Smooth, first.Kind);
        AssertVector(error, first.Error);
        AssertVector(error, reconciler.PendingOffset);

        // A later snapshot that agrees with the corrected path must not apply the error again.
        ClientReconciliationResult second = reconciler.Reconcile(7, PositionAt(7, 0.1f) + error);

        Assert.Equal(ClientReconciliationKind.None, second.Kind);
        AssertVector(error, reconciler.PendingOffset);
    }

    [Fact]
    public void TakeSmoothingStep_DrainsPendingOffsetCompletelyOverSmoothingWindow()
    {
        ClientPredictionReconciler reconciler = new(smoothingSeconds: 0.1f);
        RecordStraightRun(reconciler, firstSequence: 1, lastSequence: 3, stepPerTick: 0f);
        reconciler.Reconcile(2, new Vector3(0.5f, 0f, 0f));

        Vector3 applied = Vector3.Zero;
        for (int frame = 0; frame < 30; frame++)
        {
            applied += reconciler.TakeSmoothingStep(1f / 60f);
        }

        AssertVector(new Vector3(0.5f, 0f, 0f), applied);
        Assert.Equal(Vector3.Zero, reconciler.PendingOffset);
    }

    [Fact]
    public void RecordPrediction_DuringSmoothing_StoresCorrectedPosition()
    {
        ClientPredictionReconciler reconciler = new();
        RecordStraightRun(reconciler, firstSequence: 1, lastSequence: 2, stepPerTick: 0f);
        reconciler.Reconcile(1, new Vector3(0.5f, 0f, 0f));

        // The body has not been moved yet; the sample must still describe the corrected path.
        reconciler.RecordPrediction(3, Vector3.Zero);
        ClientReconciliationResult result = reconciler.Reconcile(3, new Vector3(0.5f, 0f, 0f));

        Assert.Equal(ClientReconciliationKind.None, result.Kind);
    }

    // NETR-4: a snap no longer drops the unacknowledged ticks. It lands on the server position
    // with commands 4 and 5 (0.1 each) replayed on top, keeps one sample per tick from the ack on,
    // and clears any pending smoothing.
    [Fact]
    public void Reconcile_LargeDivergence_SnapsToServerPlusReplayedUnacknowledgedTicks()
    {
        // The replay is under test here, not the discard of far snaps.
        ClientPredictionReconciler reconciler = new(replayDiscardDistance: float.PositiveInfinity);
        RecordStraightRun(reconciler, firstSequence: 1, lastSequence: 5, stepPerTick: 0.1f);
        Vector3 respawn = new(20f, 1.5f, 20f);

        ClientReconciliationResult result = reconciler.Reconcile(3, respawn);

        Assert.Equal(ClientReconciliationKind.Snap, result.Kind);
        AssertVector(respawn + new Vector3(0.2f, 0f, 0f), result.SnapPosition);
        AssertVector(respawn - PositionAt(3, 0.1f), result.Error);
        Assert.Equal(3, reconciler.HistoryCount);
        Assert.Equal(Vector3.Zero, reconciler.PendingOffset);
    }

    // A dead player sends idle commands that keep their sequence; the respawn teleport then arrives
    // with an ack of one of them, and a later ack compares against the path the snap left behind.
    [Fact]
    public void Reconcile_DeadPeriodOfIdleCommands_ThenTheRespawnTeleport_SnapsOnceAndAcceptsALaterAck()
    {
        ClientPredictionReconciler reconciler = new();
        Vector3 deathPlace = new(30f, 1.5f, 30f);
        Vector3 spawn = new(5f, 1.5f, 5f);
        for (ulong sequence = 1; sequence <= 6; sequence++)
        {
            reconciler.RecordPrediction(sequence, deathPlace, PlayerMoveIntent.None);
        }

        ClientReconciliationResult teleport = reconciler.Reconcile(4, spawn);

        Assert.Equal(ClientReconciliationKind.Snap, teleport.Kind);
        AssertVector(spawn, teleport.SnapPosition);
        Assert.Equal(Vector3.Zero, reconciler.PendingOffset);

        for (ulong sequence = 7; sequence <= 9; sequence++)
        {
            reconciler.RecordPrediction(sequence, spawn, PlayerMoveIntent.None);
        }

        ClientReconciliationResult later = reconciler.Reconcile(8, spawn);

        Assert.Equal(ClientReconciliationKind.None, later.Kind);
        Assert.Equal(Vector3.Zero, reconciler.PendingOffset);
    }

    // After the snap the history describes the replayed path, so a later snapshot that agrees
    // with it is not corrected again.
    [Fact]
    public void Reconcile_AfterSnap_ComparesLaterAcksAgainstTheReplayedPath()
    {
        // The replay is under test here, not the discard of far snaps.
        ClientPredictionReconciler reconciler = new(replayDiscardDistance: float.PositiveInfinity);
        RecordStraightRun(reconciler, firstSequence: 1, lastSequence: 5, stepPerTick: 0.1f);
        ClientReconciliationResult snap = reconciler.Reconcile(3, new Vector3(50f, 0f, 0f));
        Assert.Equal(ClientReconciliationKind.Snap, snap.Kind);
        AssertVector(new Vector3(50.2f, 0f, 0f), snap.SnapPosition);
        // The client keeps predicting from the snap position.
        for (ulong sequence = 6; sequence <= 9; sequence++)
        {
            reconciler.RecordPrediction(sequence, new Vector3(50.2f + (sequence - 5) * 0.1f, 0f, 0f));
        }

        ClientReconciliationResult result = reconciler.Reconcile(6, new Vector3(50.3f, 0f, 0f));

        Assert.Equal(ClientReconciliationKind.None, result.Kind);
        Assert.Equal(Vector3.Zero, reconciler.PendingOffset);
    }

    // With nothing acknowledged there is nothing to replay: the history is dropped, and an ack
    // that still predates every sample recorded after that waits instead of snapping again.
    [Fact]
    public void Reconcile_AfterUnacknowledgedSnap_WaitsForComparableSampleInsteadOfSnappingAgain()
    {
        ClientPredictionReconciler reconciler = new();
        RecordStraightRun(reconciler, firstSequence: 1, lastSequence: 5, stepPerTick: 0.1f);
        ClientReconciliationResult snap = reconciler.Reconcile(null, new Vector3(50f, 0f, 0f));
        Assert.Equal(ClientReconciliationKind.Snap, snap.Kind);
        Assert.Equal(new Vector3(50f, 0f, 0f), snap.SnapPosition);
        Assert.Equal(0, reconciler.HistoryCount);
        reconciler.RecordPrediction(9, new Vector3(50.2f, 0f, 0f));

        ClientReconciliationResult result = reconciler.Reconcile(6, new Vector3(49f, 0f, 0f));

        Assert.Equal(ClientReconciliationKind.None, result.Kind);
    }

    [Fact]
    public void Reconcile_WithoutAcknowledgedCommand_Snaps()
    {
        ClientPredictionReconciler reconciler = new();
        RecordStraightRun(reconciler, firstSequence: 1, lastSequence: 3, stepPerTick: 0.1f);

        Assert.Equal(ClientReconciliationKind.Snap, reconciler.Reconcile(null, Vector3.One).Kind);
    }

    [Fact]
    public void ApplySnapshotToRuntime_WithReconciler_KeepsPredictedPositionAndVelocity()
    {
        using ClientWorldSession runtime = CreateRuntime();
        ClientPredictionReconciler reconciler = new();
        Vector3 predicted = runtime.Player.GetWorldPosition();
        reconciler.RecordPrediction(3, predicted);
        runtime.Player.PhysicsBody.LinearVelocity = new BulletSharp.Math.Vector3(18f, 0f, 0f);

        // Same position the client predicted for command 3, while the client moved on.
        ClientCorrectionApplier.ApplySnapshotToRuntime(
            runtime,
            CreateLocalSnapshot(runtime, predicted, acknowledgedSequence: 3),
            runtime.LocalPlayerSessionId.Value,
            reconciler);

        Assert.Equal(18f, runtime.Player.PhysicsBody.LinearVelocity.X, 3);
        AssertVector(predicted, runtime.Player.GetWorldPosition());
    }

    [Fact]
    public void ApplySnapshotToRuntime_WithReconciler_SnapsOnLargeDivergence()
    {
        using ClientWorldSession runtime = CreateRuntime();
        ClientPredictionReconciler reconciler = new();
        reconciler.RecordPrediction(3, runtime.Player.GetWorldPosition());
        Vector3 server = new(8f, 1.5f, 8f);

        ClientCorrectionApplier.ApplySnapshotToRuntime(
            runtime,
            CreateLocalSnapshot(runtime, server, acknowledgedSequence: 3),
            runtime.LocalPlayerSessionId.Value,
            reconciler);

        AssertVector(server, runtime.Player.GetWorldPosition());
    }

    [Fact]
    public void NudgeWorldPosition_MovesBodyAndCameraWithoutZeroingVelocity()
    {
        using ClientWorldSession runtime = CreateRuntime();
        Vector3 start = runtime.Player.GetWorldPosition();
        Vector3 cameraStart = runtime.Player.Camera.Position;
        runtime.Player.PhysicsBody.LinearVelocity = new BulletSharp.Math.Vector3(0f, 0f, 5f);

        runtime.Player.NudgeWorldPosition(new Vector3(0.25f, 0f, 0f));

        AssertVector(start + new Vector3(0.25f, 0f, 0f), runtime.Player.GetWorldPosition());
        AssertVector(cameraStart + new Vector3(0.25f, 0f, 0f), runtime.Player.Camera.Position);
        Assert.Equal(5f, runtime.Player.PhysicsBody.LinearVelocity.Z, 3);
    }

    [Fact]
    public void WithAuthoritativeMovement_KeepsViewSettingsAndUsesServerMovement()
    {
        PlayerSimulationTuning local = new(
            EyeHeightOffset: 0.5f,
            MouseSensitivity: 4f,
            MoveSpeed: 12f,
            NoclipSpeed: 150f,
            JumpVelocity: 9f,
            WalkSpeed: 3f,
            SprintSpeed: 20f,
            GroundAcceleration: 100f,
            GroundDeceleration: 200f);

        PlayerSimulationTuning predicted = local.WithAuthoritativeMovement();

        Assert.Equal(0.5f, predicted.EyeHeightOffset);
        Assert.Equal(4f, predicted.MouseSensitivity);
        Assert.Equal(PlayerSimulationTuning.AuthoritativeDefault.MoveSpeed, predicted.MoveSpeed);
        Assert.Equal(PlayerSimulationTuning.AuthoritativeDefault.NoclipSpeed, predicted.NoclipSpeed);
        Assert.Equal(PlayerSimulationTuning.AuthoritativeDefault.JumpVelocity, predicted.JumpVelocity);
        // The other two tiers and both ramp rates are movement too: predicting a sprint, a walk or
        // an acceleration the server does not simulate drifts and gets pulled back on every snapshot.
        Assert.Equal(PlayerSimulationTuning.AuthoritativeDefault.WalkSpeed, predicted.WalkSpeed);
        Assert.Equal(PlayerSimulationTuning.AuthoritativeDefault.SprintSpeed, predicted.SprintSpeed);
        Assert.Equal(
            PlayerSimulationTuning.AuthoritativeDefault.GroundAcceleration,
            predicted.GroundAcceleration);
        Assert.Equal(
            PlayerSimulationTuning.AuthoritativeDefault.GroundDeceleration,
            predicted.GroundDeceleration);
    }

    // Reviewer S11: a snap replays the unacknowledged ticks as a translation of the client's path.
    // When that path would end inside a solid cell of the client's map, the snap lands on the
    // server position alone instead of walking the player into the cell.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Snap_WhoseReplayEntersASolidCell_LandsOnTheServerPositionOnly(bool cellIsSolid)
    {
        using ClientWorldSession runtime = CreateRuntime();
        if (cellIsSolid)
        {
            Assert.NotNull(runtime.Map.TryAddCube(new Vector3(2f, 1f, 6f), isStatic: true, withPhysics: false));
        }

        ClientPredictionReconciler reconciler = new();
        for (ulong sequence = 1; sequence <= 5; sequence++)
        {
            reconciler.RecordPrediction(sequence, new Vector3(2f + sequence * 0.1f, 1.5f, 2f));
        }

        Vector3 server = new(2.3f, 1.5f, 6.3f);
        ClientReconciliationResult result = reconciler.Reconcile(3, server, runtime.Map);

        Assert.Equal(ClientReconciliationKind.Snap, result.Kind);
        if (cellIsSolid)
        {
            AssertVector(server, result.SnapPosition);
            Assert.Equal(1, reconciler.DiscardedReplays);
        }
        else
        {
            AssertVector(server + new Vector3(0.2f, 0f, 0f), result.SnapPosition);
            Assert.Equal(0, reconciler.DiscardedReplays);
        }
    }

    // Reviewer S11, an obstacle only the server has: the server pushes the player 7 m back against
    // a wall 1 m behind it, which the client's map does not know, and the input stays held. The
    // snap used to land on the server position plus 2 m of replayed walking, inside the wall;
    // beyond ReplayDiscardDistance it lands on the server position. Holding the input then only
    // smooths (the client walks into the wall, each ack pulls it back): no repeated snapping.
    [Fact]
    public void FarSnap_AgainstAnObstacleTheClientDoesNotKnow_LandsOnTheServer_AndDoesNotSnapAgainWhileHeld()
    {
        const float Step = 0.1f;
        const int LatencyTicks = 20;
        const int PushTick = 60;
        const int Ticks = 400;
        const float SecondsPerTick = 1f / SimulationClock.AuthoritativeTicksPerSecond;
        ClientPredictionReconciler reconciler = new();
        float[] serverX = new float[Ticks + 1];
        float wallX = 0f;
        float clientX = 0f;
        int snaps = 0;
        float landing = float.NaN;
        float maxPastWallAfterSnap = 0f;
        for (int tick = 1; tick <= Ticks; tick++)
        {
            // Server: walks the same commands; at PushTick it is pushed 7 m back, 1 m in front of a wall.
            if (tick == PushTick)
            {
                serverX[tick] = serverX[tick - 1] - 7f;
                wallX = serverX[tick] + 1f;
            }
            else
            {
                serverX[tick] = serverX[tick - 1] + Step;
                if (tick > PushTick)
                {
                    serverX[tick] = Math.Min(serverX[tick], wallX);
                }
            }

            // Client: walks every tick (no wall in its map) and drains its smoothing.
            clientX += Step + reconciler.TakeSmoothingStep(SecondsPerTick).X;
            reconciler.RecordPrediction((ulong)tick, new Vector3(clientX, 0f, 0f));
            int ack = tick - LatencyTicks;
            if (ack >= 1 && tick % 2 == 0)
            {
                ClientReconciliationResult result = reconciler.Reconcile((ulong)ack, new Vector3(serverX[ack], 0f, 0f));
                if (result.Kind == ClientReconciliationKind.Snap)
                {
                    snaps++;
                    clientX = result.SnapPosition.X;
                    landing = clientX;
                }
            }

            if (snaps > 0)
            {
                maxPastWallAfterSnap = Math.Max(maxPastWallAfterSnap, clientX - wallX);
            }
        }

        Assert.Equal(1, snaps);
        // The server position at the ack, in front of the wall, not 2 m of replay past it.
        Assert.True(landing <= wallX, $"landed {landing - wallX:F2} m past the wall");
        Assert.Equal(1, reconciler.DiscardedReplays);
        // Held against the wall the client runs ahead by the unacknowledged ticks' walk (2 m) plus
        // the correction still smoothing in, and stays there.
        Assert.True(maxPastWallAfterSnap <= LatencyTicks * Step + 1f, $"{maxPastWallAfterSnap:F2} m past the wall");
        Assert.InRange(clientX - wallX, LatencyTicks * Step - 0.3f, LatencyTicks * Step + 1f);
    }

    // Character-gaits fix loop 3. A starve whose commands diverge mid-way is the one case the
    // extrapolation is actually used for (when they keep matching, the client's own sample carries
    // the real ramp and is compared instead). Past BufferedPlayerCommandInbox.ServerRepeatTicks
    // the server idles, and the motor brakes at GroundDeceleration instead of stopping at once, so
    // the server coasts dt x sum(max(v - 24 dt k, 0)) from the cruise.
    // The old fixed-step extrapolation predicted an instant stop and was that much short.
    [Fact]
    public void Reconcile_StarvedPastTheRepeatWindow_WithDivergedCommands_ExtrapolatesTheIdleCoast()
    {
        const float CruiseStep = PlayerSimulationTuning.DefaultRunSpeed * Dt;
        // dt x sum over k of max(cruise - decel x dt x k, 0): the motor's braking tail.
        float expectedCoast = 0f;
        for (int k = 1; ; k++)
        {
            float v = PlayerSimulationTuning.DefaultRunSpeed - (PlayerSimulationTuning.DefaultGroundDeceleration * Dt * k);
            if (v <= 0f)
            {
                break;
            }

            expectedCoast += v * Dt;
        }

        const int StarvedTicks = 28;
        ClientPredictionReconciler reconciler = new();
        RecordRun(reconciler, firstSequence: 1, lastSequence: 10, stepPerTick: CruiseStep, Forward);
        Vector3 acknowledged = PositionAt(10, CruiseStep);
        // The player turns during the starve, so no later sample is what the server simulated and
        // the comparison has to be with the extrapolation.
        RecordRun(reconciler, firstSequence: 11, lastSequence: 18, stepPerTick: CruiseStep, Turned, acknowledged);

        Vector3 serverPosition = acknowledged
            + new Vector3((RepeatTicks * CruiseStep) + expectedCoast, 0f, 0f);
        ClientReconciliationResult result = reconciler.Reconcile(10, serverPosition, map: null, StarvedTicks);
        Vector3 predicted = serverPosition - result.Error;
        float coast = predicted.X - (acknowledged.X + (RepeatTicks * CruiseStep));

        // The repeat window is at the cruise tier already, so it holds the ack's step; what the
        // fixed-step model missed is the whole braking tail after it.
        Assert.Equal(expectedCoast, coast, 2);
        Assert.Equal(ClientReconciliationKind.None, result.Kind);
    }

    // The other half: a starve short enough to stay inside the repeat window, acknowledged
    // mid-ramp (two ticks after a standing start). The server keeps accelerating toward the cruise,
    // covering dt x sum(min(entry + accel x k, cruise)), k = 1..5, where repeating the ack tick's
    // step gives only 5 x entry x dt. The difference is the first starve that crosses the
    // deadband, and it has no idle part at all.
    [Fact]
    public void Reconcile_StarvedInsideTheRepeatWindow_FromAMidRampAck_KeepsAcceleratingTowardTheTier()
    {
        const int RampTicks = 2;
        const int StarvedTicks = 5;
        const float AccelerationPerTick = PlayerSimulationTuning.DefaultGroundAcceleration * Dt;
        const float EntrySpeed = RampTicks * AccelerationPerTick;
        float expectedRampDistance = 0f;
        for (int k = 1; k <= StarvedTicks; k++)
        {
            expectedRampDistance += MathF.Min(
                EntrySpeed + (AccelerationPerTick * k), PlayerSimulationTuning.AuthoritativeDefault.MoveSpeed) * Dt;
        }

        ClientPredictionReconciler reconciler = new();
        // A standing start: tick k ends at speed k x 0.28125, so its step grows every tick.
        Vector3 position = Vector3.Zero;
        float speed = 0f;
        for (ulong sequence = 1; sequence <= RampTicks; sequence++)
        {
            speed += AccelerationPerTick;
            position += new Vector3(speed * Dt, 0f, 0f);
            reconciler.RecordPrediction(sequence, position, Forward);
        }

        Vector3 acknowledged = position;
        RecordRun(reconciler, firstSequence: RampTicks + 1, lastSequence: RampTicks + 3, stepPerTick: EntrySpeed * Dt, Turned, acknowledged);

        Vector3 serverPosition = acknowledged + new Vector3(expectedRampDistance, 0f, 0f);
        ClientReconciliationResult result = reconciler.Reconcile(RampTicks, serverPosition, map: null, StarvedTicks);
        Vector3 predicted = serverPosition - result.Error;

        Assert.Equal(expectedRampDistance, predicted.X - acknowledged.X, 3);
        Assert.Equal(ClientReconciliationKind.None, result.Kind);
        // The fixed-step model's answer, for the record: over the deadband, so it corrected.
        Assert.True(
            expectedRampDistance - (StarvedTicks * EntrySpeed * Dt) > ClientPredictionReconciler.DefaultDeadbandDistance,
            "the un-ramped extrapolation should be over the deadband");
    }

    private const float Dt = SimulationClock.FixedDeltaSeconds;
    private const int RepeatTicks = BufferedPlayerCommandInbox.ServerRepeatTicks;
    private static readonly PlayerMoveIntent Forward = new(1f, 0f);
    private static readonly PlayerMoveIntent Turned = new(1f, 1f);

    private static void RecordRun(
        ClientPredictionReconciler reconciler,
        ulong firstSequence,
        ulong lastSequence,
        float stepPerTick,
        PlayerMoveIntent move,
        Vector3 origin = default)
    {
        for (ulong sequence = firstSequence; sequence <= lastSequence; sequence++)
        {
            reconciler.RecordPrediction(sequence, origin + PositionAt(sequence - firstSequence + 1, stepPerTick), move);
        }
    }

    private static void RecordStraightRun(
        ClientPredictionReconciler reconciler,
        ulong firstSequence,
        ulong lastSequence,
        float stepPerTick)
    {
        for (ulong sequence = firstSequence; sequence <= lastSequence; sequence++)
        {
            reconciler.RecordPrediction(sequence, PositionAt(sequence, stepPerTick));
        }
    }

    private static Vector3 PositionAt(ulong sequence, float stepPerTick)
    {
        return new Vector3(sequence * stepPerTick, 0f, 0f);
    }

    private static ServerSnapshotEnvelope CreateLocalSnapshot(
        ClientWorldSession runtime,
        Vector3 position,
        ulong acknowledgedSequence)
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
        return new ClientWorldSession(
            floorSize: 10,
            playerStartPosition: new Vector3(5f, 1.5f, 5f),
            new SceneEventHub());
    }

    private static void AssertVector(Vector3 expected, Vector3 actual)
    {
        Assert.Equal(expected.X, actual.X, 3);
        Assert.Equal(expected.Y, actual.Y, 3);
        Assert.Equal(expected.Z, actual.Z, 3);
    }
}
