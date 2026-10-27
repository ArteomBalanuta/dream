using OpenTK.Mathematics;
using Xunit;
using Dream.Client.Replication;
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

    [Fact]
    public void Reconcile_LargeDivergence_SnapsAndClearsHistory()
    {
        ClientPredictionReconciler reconciler = new();
        RecordStraightRun(reconciler, firstSequence: 1, lastSequence: 5, stepPerTick: 0.1f);
        Vector3 respawn = new(20f, 1.5f, 20f);

        ClientReconciliationResult result = reconciler.Reconcile(3, respawn);

        Assert.Equal(ClientReconciliationKind.Snap, result.Kind);
        Assert.Equal(respawn, result.SnapPosition);
        Assert.Equal(0, reconciler.HistoryCount);
        Assert.Equal(Vector3.Zero, reconciler.PendingOffset);
    }

    [Fact]
    public void Reconcile_AfterSnap_WaitsForComparableSampleInsteadOfSnappingAgain()
    {
        ClientPredictionReconciler reconciler = new();
        RecordStraightRun(reconciler, firstSequence: 1, lastSequence: 5, stepPerTick: 0.1f);
        reconciler.Reconcile(3, new Vector3(50f, 0f, 0f));
        reconciler.RecordPrediction(9, new Vector3(50.2f, 0f, 0f));

        // The server still lags: its ack predates every sample recorded since the snap.
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
        using WorldSessionRuntime runtime = CreateRuntime();
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
        using WorldSessionRuntime runtime = CreateRuntime();
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
        using WorldSessionRuntime runtime = CreateRuntime();
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
            JumpVelocity: 9f);

        PlayerSimulationTuning predicted = local.WithAuthoritativeMovement();

        Assert.Equal(0.5f, predicted.EyeHeightOffset);
        Assert.Equal(4f, predicted.MouseSensitivity);
        Assert.Equal(PlayerSimulationTuning.AuthoritativeDefault.MoveSpeed, predicted.MoveSpeed);
        Assert.Equal(PlayerSimulationTuning.AuthoritativeDefault.NoclipSpeed, predicted.NoclipSpeed);
        Assert.Equal(PlayerSimulationTuning.AuthoritativeDefault.JumpVelocity, predicted.JumpVelocity);
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
        WorldSessionRuntime runtime,
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

    private static WorldSessionRuntime CreateRuntime()
    {
        return new WorldSessionRuntime(
            floorSize: 10,
            playerStartPosition: new Vector3(5f, 1.5f, 5f),
            new SceneEventHub(),
            loadVisualAssets: false);
    }

    private static void AssertVector(Vector3 expected, Vector3 actual)
    {
        Assert.Equal(expected.X, actual.X, 3);
        Assert.Equal(expected.Y, actual.Y, 3);
        Assert.Equal(expected.Z, actual.Z, 3);
    }
}
