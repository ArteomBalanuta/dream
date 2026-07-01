using OpenTK.Mathematics;
using Xunit;
using Dream.Simulation.Players;

namespace Dream.Tests.Simulation.Players;


public class CameraTests
{
    [Fact]
    public void ApplyLookDelta_RotatesCameraWithoutDeltaTimeScaling()
    {
        Camera camera = new(Vector3.Zero)
        {
            MouseSensitivity = 0.1f
        };

        camera.ApplyLookDelta(new Vector2(10f, 0f));

        Assert.NotEqual(-Vector3.UnitZ, camera.Front);
        Assert.True(camera.Front.X > 0f);
    }

    [Fact]
    public void ApplyLookDelta_WithDefaultSensitivity_UsesSmallPerFrameRotationScale()
    {
        Camera camera = new(Vector3.Zero)
        {
            MouseSensitivity = 2.5f
        };

        camera.ApplyLookDelta(new Vector2(1f, 0f));

        Assert.InRange(camera.Front.X, 0.01f, 0.1f);
    }

    [Fact]
    public void HorizontalForward_PreservesYaw_WhenPitchApproachesVertical()
    {
        Camera camera = new(Vector3.Zero)
        {
            MouseSensitivity = 1f
        };

        camera.ApplyLookDelta(new Vector2(94.24778f, 0f));
        Vector3 horizontalBeforePitch = camera.HorizontalForward;

        camera.ApplyLookDelta(new Vector2(0f, -5000f));

        Assert.InRange(horizontalBeforePitch.X, 0.99f, 1f);
        Assert.InRange(camera.HorizontalForward.X, 0.99f, 1f);
        Assert.InRange(camera.HorizontalForward.Z, -0.05f, 0.05f);
    }
}
