using OpenTK.Mathematics;
using Xunit;
using Xunit.Abstractions;
using Dream.Application;
using Dream.Client.Presentation;
using Dream.Client.World;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Tests.Utilities;

namespace Dream.Tests.Client.Presentation;

// ClientWorldSession.CapturePresentationState used to allocate two cameras per frame: one from
// Player.CreatePresentationCamera and a Clone of it in ThirdPersonCameraRig.Resolve. The first-
// person camera is now a reused scratch filled by Player.CopyPresentationCameraTo. The camera the
// presentation state carries stays a new instance per capture, because callers may keep a state
// across captures (ClientWorldSessionPresentationTests compares two captured states' cameras).
public class PresentationCameraScratchTests(ITestOutputHelper output)
{
    private static readonly PlayerSimulationTuning Tuning = new(
        EyeHeightOffset: 0.35f,
        MouseSensitivity: 2.5f,
        MoveSpeed: 6f,
        NoclipSpeed: 150f,
        JumpVelocity: 5f);

    [Theory]
    [InlineData(CameraViewMode.FirstPerson)]
    [InlineData(CameraViewMode.ThirdPerson)]
    public void CapturePresentationState_MatchesTheAllocatingPathOverSeveralFrames(CameraViewMode mode)
    {
        using ClientWorldSession session = new(
            floorSize: 20,
            playerStartPosition: new Vector3(10f, 1.5f, 10f),
            new SceneEventHub());
        List<Camera> captured = [];

        for (int frame = 0; frame < 24; frame++)
        {
            Vector2 look = new(frame % 5 - 2, (frame % 3) - 1);
            Vector3 move = new(MathF.Cos(frame * 0.4f), 0f, MathF.Sin(frame * 0.4f));
            session.Player.Update(
                SimulationClock.FixedDeltaSeconds,
                new PlayerSimulationInput(new MovementCommand(move), look * 4f, AllowGameplayInput: true),
                Tuning);
            session.Physics.StepSimulationFixed(SimulationClock.FixedDeltaSeconds);
            session.Player.SyncCameraFromBody(Tuning);
            float lead = SimulationClock.FixedDeltaSeconds * (frame % 4) * 0.25f;

            // The pre-change path: two fresh cameras.
            Camera expected = ResolveWithClone(
                session.Player.CreatePresentationCamera(lead),
                session.Player.GetWorldPosition(),
                mode);
            WorldSessionPresentationState state = session.CapturePresentationState(
                WorldVisibility.MaxRenderDistance,
                lead,
                mode);

            Assert.Equal(expected.Position, state.Camera.Position);
            Assert.Equal(expected.Front, state.Camera.Front);
            Assert.Equal(expected.Up, state.Camera.Up);
            Assert.Equal(expected.MouseSensitivity, state.Camera.MouseSensitivity);
            Assert.DoesNotContain(captured, previous => ReferenceEquals(previous, state.Camera));
            captured.Add(state.Camera);
        }
    }

    [Fact]
    public void ResolveInto_MatchesResolve_AndRejectsAliasedTarget()
    {
        Camera firstPerson = new(new Vector3(5f, 1.85f, 5f));
        firstPerson.SetFacing(new Vector3(1f, -0.4f, 0.3f));
        Camera target = new(Vector3.Zero);

        foreach (CameraViewMode mode in new[] { CameraViewMode.FirstPerson, CameraViewMode.ThirdPerson })
        {
            Camera resolved = ThirdPersonCameraRig.Resolve(firstPerson, new Vector3(5f, 1.5f, 5f), mode);
            ThirdPersonCameraRig.ResolveInto(firstPerson, new Vector3(5f, 1.5f, 5f), mode, target);
            Camera cloned = ResolveWithClone(firstPerson, new Vector3(5f, 1.5f, 5f), mode);

            Assert.Equal(cloned.Position, resolved.Position);
            Assert.Equal(cloned.Front, resolved.Front);
            Assert.Equal(cloned.Position, target.Position);
            Assert.Equal(cloned.Front, target.Front);
        }

        Assert.Throws<ArgumentException>(() => ThirdPersonCameraRig.ResolveInto(
            firstPerson, Vector3.Zero, CameraViewMode.ThirdPerson, firstPerson));
        Assert.Equal(0, AllocationAssert.MeasureBytesPerIteration(() => ThirdPersonCameraRig.ResolveInto(
            firstPerson, new Vector3(5f, 1.5f, 5f), CameraViewMode.ThirdPerson, target)));
    }

    [Fact]
    public void CapturePresentationState_AllocatesOneCameraLessThanTheAllocatingPath()
    {
        using ClientWorldSession session = new(
            floorSize: 10,
            playerStartPosition: new Vector3(5f, 1.5f, 5f),
            new SceneEventHub());
        Camera scratch = new(Vector3.Zero);
        Vector3 playerPosition = session.Player.GetWorldPosition();

        long oldCameraPathBytes = AllocationAssert.MeasureBytesPerIteration(() => GC.KeepAlive(
            ThirdPersonCameraRig.Resolve(session.Player.CreatePresentationCamera(0f), playerPosition, CameraViewMode.FirstPerson)));
        long newCameraPathBytes = AllocationAssert.MeasureBytesPerIteration(() =>
        {
            session.Player.CopyPresentationCameraTo(scratch, 0f);
            GC.KeepAlive(ThirdPersonCameraRig.Resolve(scratch, playerPosition, CameraViewMode.FirstPerson));
        });
        long captureBytes = AllocationAssert.MeasureBytesPerIteration(
            () => session.CapturePresentationState(WorldVisibility.MaxRenderDistance));

        output.WriteLine(
            $"Camera path: {oldCameraPathBytes} B before, {newCameraPathBytes} B after; " +
            $"CapturePresentationState: {captureBytes} B (previous budget 776 B)");
        Assert.InRange(oldCameraPathBytes - newCameraPathBytes, 32, 80);
        // GPU-5 added seven GPU/counter fields (3 doubles, a long, 3 ints) to the
        // FramePerformanceSnapshot inside the one presentation-state allocation: +48 B, not a new
        // allocation. See WorldSessionPresentationStateActiveEnemiesTests.
        const int Gpu5SnapshotGrowthBytes = 48;
        // Wave T2 fix loop 1: GeometryRenderExport.StaticCells, one reference in the same state.
        const int StaticCellsExportGrowthBytes = 8;
        long budget = 776 + Gpu5SnapshotGrowthBytes + StaticCellsExportGrowthBytes - (oldCameraPathBytes - newCameraPathBytes);
        Assert.True(
            captureBytes <= budget,
            $"CapturePresentationState allocated {captureBytes} B; expected at most {budget} B.");
    }

    // ThirdPersonCameraRig.Resolve as it was before ResolveInto: Clone, then the chase offset.
    private static Camera ResolveWithClone(Camera firstPersonCamera, Vector3 playerWorldPosition, CameraViewMode mode)
    {
        _ = playerWorldPosition;
        Camera camera = firstPersonCamera.Clone();
        if (mode == CameraViewMode.FirstPerson)
        {
            return camera;
        }

        Vector3 horizontalForward = firstPersonCamera.HorizontalForward;
        Vector3 followAnchor = firstPersonCamera.Position;
        Vector3 target = followAnchor + (firstPersonCamera.Front * 8f);
        camera.Position = followAnchor - (horizontalForward * 4.5f) + (Vector3.UnitY * 1.6f);
        camera.SetFacing(target - camera.Position);
        return camera;
    }
}
