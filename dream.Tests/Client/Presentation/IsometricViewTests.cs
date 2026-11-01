using Dream.Client.Presentation;
using Dream.Simulation.Players;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Client.Presentation;

public sealed class IsometricViewTests
{
    [Fact]
    public void ResolveInto_PlacesCameraAtFixedOffsetLookingAtFocus()
    {
        Vector3 focus = new(10f, 2f, 7f);
        Camera camera = new(Vector3.Zero);

        IsometricView.ResolveInto(focus, camera);

        Assert.Equal(IsometricView.Distance, (camera.Position - focus).Length, 3);
        Vector3 toFocus = (focus - camera.Position).Normalized();
        Assert.Equal(1f, Vector3.Dot(toFocus, camera.Front), 3);
        Assert.True(camera.Position.Y > focus.Y);
        float pitch = MathHelper.RadiansToDegrees(MathF.Asin(-camera.Front.Y));
        Assert.Equal(IsometricView.PitchDegrees, pitch, 2);
    }

    [Fact]
    public void CreateProjection_UsesNearOneAndFarDistancePlus80()
    {
        Matrix4 expected = Matrix4.CreatePerspectiveFieldOfView(
            MathHelper.DegreesToRadians(35f), 1.5f, 1f, 98f);
        Assert.Equal(expected, IsometricView.CreateProjection(1.5f));
    }

    [Fact]
    public void Focus_SmoothsYAndSnapsOnLargeJump()
    {
        IsometricFocus focus = new();
        Assert.Equal(1f, focus.Advance(new Vector3(1f, 1f, 1f), 0.016f).Y);

        Vector3 after = focus.Advance(new Vector3(3f, 2f, 4f), 0.016f);
        Assert.Equal(3f, after.X);
        Assert.Equal(4f, after.Z);
        Assert.InRange(after.Y, 1.01f, 1.5f);

        Assert.Equal(20f, focus.Advance(new Vector3(3f, 20f, 4f), 0.016f).Y);
    }
}
