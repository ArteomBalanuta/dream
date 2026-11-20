using Dream.Client.CameraControl;
using Dream.Client.Presentation;
using Dream.Simulation.Players;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Client.CameraControl;

public class ZoomAnchorSolverTests
{
    private const float Width = 1440f;
    private const float Height = 884f;

    internal static Vector2 ProjectToPixels(Vector3 world, TacticalCameraPose pose)
    {
        Camera camera = new(pose.Target);
        pose.ResolveInto(camera);
        Matrix4 view = camera.GetViewMatrix();
        Matrix4 projection = IsometricView.CreateProjection(Width / Height, pose.Distance);
        Vector4 clip = new Vector4(world, 1f) * view * projection;
        float ndcX = clip.X / clip.W;
        float ndcY = clip.Y / clip.W;
        return new Vector2((ndcX * 0.5f + 0.5f) * Width, (1f - (ndcY * 0.5f + 0.5f)) * Height);
    }

    [Theory]
    [InlineData(18f, 12f, 0f)]
    [InlineData(18f, 24f, 0f)]
    [InlineData(18f, 12f, 1f)]
    [InlineData(18f, 24f, 1f)]
    [InlineData(18f, 12f, -2f)]
    [InlineData(18f, 24f, -2f)]
    [InlineData(14f, 20f, -2f)]
    public void SolvedTargetKeepsTheAnchorOnTheSamePixel(float fromDistance, float toDistance, float anchorHeight)
    {
        float yaw = 45f;
        float pitch = IsometricView.PitchDegrees;
        Vector3 target0 = new(10f, 2f, -4f);
        TacticalCameraPose before = new(target0, yaw, pitch, fromDistance);
        Vector3 anchor = new(target0.X + 3.5f, target0.Y + anchorHeight, target0.Z - 2.25f);

        Assert.True(ZoomAnchorSolver.TrySolveTarget(anchor, before.Position, yaw, pitch, toDistance, target0.Y, out Vector3 target1));
        TacticalCameraPose after = new(target1, yaw, pitch, toDistance);

        Vector2 pixelBefore = ProjectToPixels(anchor, before);
        Vector2 pixelAfter = ProjectToPixels(anchor, after);
        Assert.InRange((pixelAfter - pixelBefore).Length, 0f, 0.5f);
        Assert.Equal(target0.Y, target1.Y);
    }

    [Fact]
    public void FlatRayIsRejected()
    {
        // Camera level with the anchor: r.Y is zero.
        Assert.False(ZoomAnchorSolver.TrySolveTarget(
            new Vector3(0f, 2f, 0f), new Vector3(10f, 2f, 0f), 45f, 50f, 18f, 2f, out Vector3 target));
        Assert.Equal(default, target);
    }

    [Fact]
    public void NonFiniteAndDegenerateInputsAreRejected()
    {
        Assert.False(ZoomAnchorSolver.TrySolveTarget(
            new Vector3(float.NaN, 0f, 0f), new Vector3(5f, 5f, 5f), 45f, 50f, 18f, 0f, out _));
        Assert.False(ZoomAnchorSolver.TrySolveTarget(
            Vector3.Zero, new Vector3(5f, 5f, 5f), 45f, 50f, float.PositiveInfinity, 0f, out _));
        Assert.False(ZoomAnchorSolver.TrySolveTarget(
            Vector3.One, Vector3.One, 45f, 50f, 18f, 0f, out _));
    }

    [Fact]
    public void AnchoredZoomThroughTheRigKeepsThePixelWhileYawIsSettled()
    {
        TacticalCameraRig rig = new(TacticalCameraSettings.Default);
        Vector3 focus = new(4f, 1f, 6f);
        rig.Reset(focus);
        Vector3 anchor = new(focus.X + 4f, focus.Y, focus.Z - 3f);
        Vector2 expected = ProjectToPixels(anchor, rig.Pose);

        rig.LatchZoomAnchor(anchor);
        float maxError = 0f;
        for (int frame = 0; frame < 90; frame++)
        {
            float notches = frame == 0 ? 3f : frame == 30 ? -2f : 0f;
            CameraInputFrame input = CameraInputFrame.None with { ZoomNotches = notches };
            TacticalCameraPose pose = rig.Advance(focus, input, 1f / 60f);
            maxError = MathF.Max(maxError, (ProjectToPixels(anchor, pose) - expected).Length);
        }

        Assert.InRange(maxError, 0f, 1f);
        Assert.NotEqual(IsometricView.Distance, rig.Pose.Distance);
    }
}
