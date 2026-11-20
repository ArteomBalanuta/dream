using Dream.Client.CameraControl;
using Dream.Client.Presentation;
using Dream.Simulation.Players;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Client.CameraControl;

public class TacticalCameraPoseTests
{
    [Fact]
    public void DefaultPoseIsExactlyTheFixedIsometricView()
    {
        Vector3 focus = new(12.25f, 3.5f, -7.75f);
        Camera fixedView = new(focus);
        IsometricView.ResolveInto(focus, fixedView);
        Camera pose = new(focus);
        TacticalCameraPose.Default(focus).ResolveInto(pose);

        Assert.Equal(fixedView.Position, pose.Position);
        Assert.Equal(fixedView.Front, pose.Front);
        Assert.Equal(fixedView.GetViewMatrix(), pose.GetViewMatrix());
        Assert.Equal(IsometricView.FocusToCamera, TacticalCameraPose.Default(focus).FocusToCamera);
    }

    [Fact]
    public void DefaultProjectionIsUnchanged()
    {
        Assert.Equal(IsometricView.FarPlane, IsometricView.FarPlaneFor(IsometricView.Distance));
        Assert.Equal(
            IsometricView.CreateProjection(1.5f),
            Matrix4.CreatePerspectiveFieldOfView(
                MathHelper.DegreesToRadians(IsometricView.FieldOfViewDegrees), 1.5f, IsometricView.NearPlane, IsometricView.FarPlane));
    }

    [Theory]
    [InlineData(45f)]
    [InlineData(135f)]
    [InlineData(-90f)]
    [InlineData(0f)]
    public void GroundBasisIsScreenUpAndRight(float yaw)
    {
        TacticalCameraPose pose = new(Vector3.Zero, yaw, IsometricView.PitchDegrees, IsometricView.Distance);
        Vector3 forward = pose.GroundForward;
        Vector3 right = pose.GroundRight;

        Assert.Equal(0f, forward.Y);
        Assert.Equal(1f, forward.Length, 4);
        Assert.Equal(0f, Vector3.Dot(forward, right), 4);
        // Forward points from the camera towards the target on the ground.
        Vector3 camToTarget = -pose.FocusToCamera;
        Assert.True(Vector3.Dot(forward, new Vector3(camToTarget.X, 0f, camToTarget.Z)) > 0f);
        // Right matches the camera's own right vector.
        Camera camera = new(Vector3.Zero);
        pose.ResolveInto(camera);
        Assert.Equal(1f, Vector3.Dot(right, camera.Right), 4);
    }

    [Fact]
    public void DefaultSettingsSurviveSanitising()
    {
        Assert.Equal(TacticalCameraSettings.Default, TacticalCameraSettings.Default.Sanitized());
    }

    [Fact]
    public void SanitisingPullsValuesIntoRange()
    {
        TacticalCameraSettings bad = TacticalCameraSettings.Default with
        {
            PanSpeed = float.NaN,
            EdgeMarginPixels = 1000f,
            RotateSnapStepDegrees = 50f,
            ConfineMode = (CameraConfineMode)77,
            MaxDistance = 1f,
        };

        TacticalCameraSettings fixedUp = bad.Sanitized();
        Assert.Equal(14f, fixedUp.PanSpeed);
        Assert.Equal(48f, fixedUp.EdgeMarginPixels);
        Assert.Equal(45f, fixedUp.RotateSnapStepDegrees);
        Assert.Equal(CameraConfineMode.FullscreenOnly, fixedUp.ConfineMode);
        Assert.Equal(IsometricView.Distance, fixedUp.MaxDistance);
    }
}
