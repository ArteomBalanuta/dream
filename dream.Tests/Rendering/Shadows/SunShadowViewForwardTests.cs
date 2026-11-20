using Dream.Client.CameraControl;
using Dream.Client.Presentation;
using Dream.Rendering.Shadows;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Rendering.Shadows;

// The receiver box is centred ahead of the focus along the camera's ground forward; a rotated camera moves it.
public class SunShadowViewForwardTests
{
    private const int Resolution = 1024;
    private static readonly Vector3 Focus = new(13.7f, 2f, -22.1f);
    private static readonly Vector3 ToLight = new Vector3(0.3f, 0.8f, 0.2f).Normalized();

    private static Vector2 ClipOf(Matrix4 lightViewProjection, Vector3 world)
    {
        Vector4 clip = new Vector4(world, 1f) * lightViewProjection;
        return new Vector2(clip.X / clip.W, clip.Y / clip.W);
    }

    [Fact]
    public void NullForward_IsTheStaticForward_BitForBit()
    {
        var implicitDefault = SunShadowVolume.Build(Focus, ToLight, 0.8f, Resolution);
        var explicitStatic = SunShadowVolume.Build(Focus, ToLight, 0.8f, Resolution, SunShadowVolume.ViewForwardGround);
        Assert.Equal(implicitDefault, explicitStatic);

        ShadowFrame a = ShadowFrame.ForSun(Focus, ToLight, 0.8f, Resolution, 1f);
        ShadowFrame b = ShadowFrame.ForSun(Focus, ToLight, 0.8f, Resolution, 1f, SunShadowVolume.ViewForwardGround);
        Assert.Equal(a.SunLightViewProjection, b.SunLightViewProjection);
        Assert.Equal(a.SunTextureMatrix, b.SunTextureMatrix);
    }

    [Fact]
    public void DefaultPoseForward_MatchesTheStaticForward()
    {
        Vector3 fromPose = SunShadowVolume.ViewForwardGroundFor(TacticalCameraPose.Default(Focus));
        Assert.Equal(SunShadowVolume.ViewForwardGround.X, fromPose.X, 4);
        Assert.Equal(SunShadowVolume.ViewForwardGround.Z, fromPose.Z, 4);
    }

    [Fact]
    public void ReceiverCentre_FollowsTheRotatedForward_AtYaw135()
    {
        TacticalCameraPose rotated = new(Focus, 135f, IsometricView.PitchDegrees, IsometricView.Distance);
        Vector3 forward135 = SunShadowVolume.ViewForwardGroundFor(rotated);
        Assert.True(Vector3.Dot(forward135, SunShadowVolume.ViewForwardGround) < 0.1f, "yaw 135 looks along a different ground direction");

        Vector3 centre135 = Focus + (forward135 * ShadowSettings.ReceiverCentreOffset);
        Vector3 centre45 = Focus + (SunShadowVolume.ViewForwardGround * ShadowSettings.ReceiverCentreOffset);

        (Matrix4 rotatedMap, _, _) = SunShadowVolume.Build(Focus, ToLight, 0.8f, Resolution, forward135);
        (Matrix4 staticMap, _, _) = SunShadowVolume.Build(Focus, ToLight, 0.8f, Resolution);

        // Each map is centred (within the texel snap) on its own receiver centre.
        float snap = 2f / Resolution;
        Assert.InRange(MathF.Abs(ClipOf(rotatedMap, centre135).X), 0f, snap);
        Assert.InRange(MathF.Abs(ClipOf(rotatedMap, centre135).Y), 0f, snap);
        Assert.InRange(MathF.Abs(ClipOf(staticMap, centre45).X), 0f, snap);
        Assert.InRange(MathF.Abs(ClipOf(staticMap, centre45).Y), 0f, snap);
        Assert.NotEqual(rotatedMap, staticMap);
    }
}
