using OpenTK.Mathematics;
using Xunit;

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
}
