using Dream.Client.CameraControl;
using Dream.Client.Input;
using Dream.Client.Presentation;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Client.Input;

// W follows the camera's ground forward at any yaw; the two-argument Project is the fixed view's, bit for bit.
public class PlayerControllerGroundForwardTests
{
    private static Vector3 Facing(float degrees)
    {
        float r = MathHelper.DegreesToRadians(degrees);
        return new Vector3(MathF.Cos(r), 0f, MathF.Sin(r));
    }

    private static Vector3 Moved((float Forward, float Strafe) p, Vector3 facing) =>
        (facing * p.Forward) + (Vector3.Cross(facing, Vector3.UnitY) * p.Strafe);

    [Theory]
    [InlineData(45f)]
    [InlineData(135f)]
    [InlineData(-90f)]
    [InlineData(0f)]
    public void W_MovesAlongGroundForward_AndDAlongItsRight(float yaw)
    {
        TacticalCameraPose pose = new(Vector3.Zero, yaw, IsometricView.PitchDegrees, IsometricView.Distance);
        foreach (float facingDegrees in new[] { 0f, 77f, 200f })
        {
            Vector3 facing = Facing(facingDegrees);
            Vector3 w = Moved(PlayerController.Project(new Vector2(0f, 1f), facing, pose.GroundForward), facing);
            Vector3 d = Moved(PlayerController.Project(new Vector2(1f, 0f), facing, pose.GroundForward), facing);

            Assert.Equal(pose.GroundForward.X, w.X, 4);
            Assert.Equal(pose.GroundForward.Z, w.Z, 4);
            Assert.Equal(pose.GroundRight.X, d.X, 4);
            Assert.Equal(pose.GroundRight.Z, d.Z, 4);
        }
    }

    [Fact]
    public void TwoArgumentProject_IsTheDefaultGroundForwardProject_BitForBit()
    {
        Vector3 away = -IsometricView.FocusToCamera;
        Vector3 defaultForward = new Vector3(away.X, 0f, away.Z).Normalized();

        foreach (float facingDegrees in new[] { 0f, 33f, 91f, 250f })
        {
            Vector3 facing = Facing(facingDegrees);
            foreach (Vector2 axes in new[] { new Vector2(0f, 1f), new Vector2(1f, 0f), new Vector2(-1f, -1f), Vector2.Zero })
            {
                Assert.Equal(
                    PlayerController.Project(axes, facing, defaultForward),
                    PlayerController.Project(axes, facing));
            }
        }
    }
}
