using Dream.Application;
using Dream.Client.CameraControl;
using Dream.Client.Presentation;
using Dream.Client.World;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Client.Presentation;

// The isometric capture takes its pose from an optional delegate; without one it is the fixed view, exactly.
public class ClientWorldSessionTacticalCameraTests
{
    private static ClientWorldSession NewSession() =>
        new(floorSize: 10, playerStartPosition: new Vector3(5f, 1.5f, 5f), new SceneEventHub());

    [Fact]
    public void BeforeAnyCapture_PoseIsTheDefaultOnTheOrigin()
    {
        using ClientWorldSession session = NewSession();
        Assert.Equal(TacticalCameraPose.Default(Vector3.Zero), session.LastCameraPose);
        Assert.Equal(TacticalCameraPose.Default(Vector3.Zero).GroundForward, session.CurrentGroundForward);
    }

    [Fact]
    public void NullDelegate_CameraEqualsTheFixedIsometricView()
    {
        using ClientWorldSession session = NewSession();
        WorldSessionPresentationState state = session.CapturePresentationState(
            WorldVisibility.MaxRenderDistance, cameraViewMode: CameraViewMode.Isometric, frameSeconds: 0.016f);

        Vector3 focus = session.LastCameraFocus;
        Camera expected = new(focus);
        IsometricView.ResolveInto(focus, expected);

        Assert.Equal(expected.Position, state.Camera.Position);
        Assert.Equal(expected.Front, state.Camera.Front);
        Assert.Equal(focus, session.LastPlayerFocus);
        Assert.Equal(focus, session.LastViewTarget);
        Assert.Equal(TacticalCameraPose.Default(focus), session.LastCameraPose);
    }

    [Fact]
    public void RotatedZoomedPannedPose_PlacesTheCamera_AndKeepsThePlayerFocus()
    {
        using ClientWorldSession session = NewSession();
        Vector3 received = default;
        int calls = 0;
        TacticalCameraPose pose = default;
        Func<Vector3, TacticalCameraPose> delegateForPose = playerFocus =>
        {
            calls++;
            received = playerFocus;
            pose = new TacticalCameraPose(playerFocus + new Vector3(4f, 0f, -3f), 135f, IsometricView.PitchDegrees, 24f);
            return pose;
        };

        WorldSessionPresentationState state = session.CapturePresentationState(
            WorldVisibility.MaxRenderDistance,
            cameraViewMode: CameraViewMode.Isometric,
            frameSeconds: 0.016f,
            poseForPlayerFocus: delegateForPose);

        Assert.Equal(1, calls);
        Assert.Equal(pose.Position, state.Camera.Position);
        Vector3 toTarget = (pose.Target - state.Camera.Position).Normalized();
        Assert.Equal(toTarget.X, state.Camera.Front.X, 4);
        Assert.Equal(toTarget.Y, state.Camera.Front.Y, 4);
        Assert.Equal(toTarget.Z, state.Camera.Front.Z, 4);

        // The war mist stays on the player; the view target is the pose's.
        Assert.Equal(received, session.LastCameraFocus);
        Assert.Equal(received, session.LastPlayerFocus);
        Assert.Equal(pose.Target, session.LastViewTarget);
        Assert.NotEqual(session.LastPlayerFocus, session.LastViewTarget);
        Assert.Equal(pose, session.LastCameraPose);
        Assert.Equal(pose.GroundForward, session.CurrentGroundForward);
    }

    [Fact]
    public void FirstPerson_IgnoresTheDelegate()
    {
        using ClientWorldSession session = NewSession();
        int calls = 0;
        session.CapturePresentationState(
            WorldVisibility.MaxRenderDistance,
            cameraViewMode: CameraViewMode.FirstPerson,
            poseForPlayerFocus: f => { calls++; return TacticalCameraPose.Default(f); });
        Assert.Equal(0, calls);
    }
}
