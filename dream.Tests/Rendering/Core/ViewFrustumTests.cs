using Dream.Rendering.Core;
using Dream.Simulation.Players;
using Dream.Tests.Utilities;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Rendering.Core;

// GPU-2: Gribb-Hartmann plane extraction under OpenTK's row-vector convention. The view comes
// from Camera.GetViewMatrix and the projection is built exactly as Render.UpdateProjection does
// (CreatePerspectiveFieldOfView, near 0.1, far 100), with the default 70 degree FOV and the
// default 1440x1024 window.
public class ViewFrustumTests
{
    private const float Near = 0.1f;
    private const float Far = 100f;

    internal static Matrix4 GameProjection(float fieldOfViewDegrees = 70f, int width = 1440, int height = 1024)
    {
        return Matrix4.CreatePerspectiveFieldOfView(
            MathHelper.DegreesToRadians(fieldOfViewDegrees),
            (float)width / height,
            Near,
            Far);
    }

    private static ViewFrustum FrustumFor(Camera camera)
    {
        return ViewFrustum.FromViewAndProjection(camera.GetViewMatrix(), GameProjection());
    }

    private static float Distance(Vector4 plane, Vector3 point)
    {
        return (plane.X * point.X) + (plane.Y * point.Y) + (plane.Z * point.Z) + plane.W;
    }

    [Fact]
    public void DefaultCamera_PointInFrontIsInside_PointsBehindAndOffEachSideAreOutside()
    {
        // The default camera faces -Z with +X on its right and +Y up.
        Camera camera = new(Vector3.Zero);
        ViewFrustum frustum = FrustumFor(camera);

        Assert.True(frustum.IsValid);
        Assert.True(frustum.ContainsPoint(new Vector3(0f, 0f, -10f)));
        Assert.False(frustum.ContainsPoint(new Vector3(0f, 0f, 10f)), "behind");
        Assert.False(frustum.ContainsPoint(new Vector3(-100f, 0f, -10f)), "left");
        Assert.False(frustum.ContainsPoint(new Vector3(100f, 0f, -10f)), "right");
        Assert.False(frustum.ContainsPoint(new Vector3(0f, 100f, -10f)), "top");
        Assert.False(frustum.ContainsPoint(new Vector3(0f, -100f, -10f)), "bottom");
        Assert.False(frustum.ContainsPoint(new Vector3(0f, 0f, -0.05f)), "before the near plane");
        Assert.False(frustum.ContainsPoint(new Vector3(0f, 0f, -150f)), "beyond the far plane");
    }

    [Fact]
    public void EachPlane_RejectsThePointOnItsOwnSide()
    {
        Camera camera = new(Vector3.Zero);
        ViewFrustum frustum = FrustumFor(camera);

        Assert.True(Distance(frustum.Left, new Vector3(-100f, 0f, -10f)) < 0f);
        Assert.True(Distance(frustum.Right, new Vector3(-100f, 0f, -10f)) > 0f);
        Assert.True(Distance(frustum.Right, new Vector3(100f, 0f, -10f)) < 0f);
        Assert.True(Distance(frustum.Left, new Vector3(100f, 0f, -10f)) > 0f);
        Assert.True(Distance(frustum.Top, new Vector3(0f, 100f, -10f)) < 0f);
        Assert.True(Distance(frustum.Bottom, new Vector3(0f, 100f, -10f)) > 0f);
        Assert.True(Distance(frustum.Bottom, new Vector3(0f, -100f, -10f)) < 0f);
        Assert.True(Distance(frustum.Top, new Vector3(0f, -100f, -10f)) > 0f);
        Assert.True(Distance(frustum.Near, new Vector3(0f, 0f, 10f)) < 0f);
        Assert.True(Distance(frustum.Far, new Vector3(0f, 0f, -150f)) < 0f);
    }

    [Fact]
    public void Planes_AreNormalized_SoNearAndFarGiveMetricDistances()
    {
        Camera camera = new(new Vector3(3f, 2f, -4f));
        camera.SetFacing(new Vector3(1f, 0f, 1f));
        ViewFrustum frustum = FrustumFor(camera);

        foreach (Vector4 plane in new[] { frustum.Left, frustum.Right, frustum.Bottom, frustum.Top, frustum.Near, frustum.Far })
        {
            Assert.Equal(1f, plane.Xyz.Length, 4);
        }

        // At the eye the near plane is 0.1 m ahead and the far plane 100 m ahead.
        Assert.Equal(-Near, Distance(frustum.Near, camera.Position), 3);
        Assert.Equal(Far, Distance(frustum.Far, camera.Position), 2);
        Assert.Equal(5f - Near, Distance(frustum.Near, camera.Position + (camera.Front * 5f)), 3);
    }

    [Fact]
    public void CameraFacingPositiveX_KeepsPositiveXAndRejectsNegativeX()
    {
        Camera camera = new(new Vector3(28f, 2f, 28f));
        camera.SetFacing(Vector3.UnitX);
        ViewFrustum frustum = FrustumFor(camera);

        Assert.True(frustum.ContainsPoint(new Vector3(40f, 2f, 28f)));
        Assert.False(frustum.ContainsPoint(new Vector3(10f, 2f, 28f)));
        // Facing +X the camera's right is +Z.
        Assert.True(Distance(frustum.Right, new Vector3(40f, 2f, 128f)) < 0f);
        Assert.True(Distance(frustum.Left, new Vector3(40f, 2f, -72f)) < 0f);
    }

    [Fact]
    public void ContainsPoint_MatchesTheClipSpaceTest_UnderRowVectors()
    {
        // The reference transforms each point as a row vector, as OpenTK and the shaders do:
        // clip = (p, 1) * view * projection, inside when -w <= x, y, z <= w.
        Camera camera = new(new Vector3(5f, 1.85f, 5f));
        camera.SetFacing(new Vector3(0.6f, -0.3f, 0.7f));
        Matrix4 viewProjection = camera.GetViewMatrix() * GameProjection();
        ViewFrustum frustum = ViewFrustum.FromViewProjection(viewProjection);
        Random random = new(1234);
        int inside = 0;
        int outside = 0;

        for (int i = 0; i < 4000; i++)
        {
            Vector3 point = new(
                (float)((random.NextDouble() * 160d) - 75d),
                (float)((random.NextDouble() * 80d) - 40d),
                (float)((random.NextDouble() * 160d) - 75d));
            Vector4 clip = new Vector4(point, 1f) * viewProjection;
            float margin = 1e-3f * MathF.Abs(clip.W);
            bool clearlyInside = clip.W > 0f &&
                MathF.Abs(clip.X) < clip.W - margin &&
                MathF.Abs(clip.Y) < clip.W - margin &&
                MathF.Abs(clip.Z) < clip.W - margin;
            bool clearlyOutside = clip.W <= 0f ||
                MathF.Abs(clip.X) > clip.W + margin ||
                MathF.Abs(clip.Y) > clip.W + margin ||
                MathF.Abs(clip.Z) > clip.W + margin;

            if (clearlyInside)
            {
                inside++;
                Assert.True(frustum.ContainsPoint(point), $"{point} is inside clip space");
            }
            else if (clearlyOutside)
            {
                outside++;
                Assert.False(frustum.ContainsPoint(point), $"{point} is outside clip space");
            }
        }

        Assert.True(inside > 50, $"inside {inside}");
        Assert.True(outside > 50, $"outside {outside}");
    }

    [Fact]
    public void TransposedMatrix_WouldGiveDifferentPlanes()
    {
        // Guards the convention: extracting from rows (the column-vector formula) is wrong here.
        Camera camera = new(Vector3.Zero);
        Matrix4 viewProjection = camera.GetViewMatrix() * GameProjection();
        ViewFrustum correct = ViewFrustum.FromViewProjection(viewProjection);
        ViewFrustum transposed = ViewFrustum.FromViewProjection(Matrix4.Transpose(viewProjection));

        Assert.True(correct.ContainsPoint(new Vector3(0f, 0f, -10f)));
        Assert.False(transposed.IsValid && transposed.ContainsPoint(new Vector3(0f, 0f, -10f)));
    }

    [Fact]
    public void Aabb_InsideOutsideAndStraddling()
    {
        Camera camera = new(Vector3.Zero);
        ViewFrustum frustum = FrustumFor(camera);

        Assert.True(frustum.IntersectsAabb(new Vector3(-1f, -1f, -11f), new Vector3(1f, 1f, -9f)));
        Assert.False(frustum.IntersectsAabb(new Vector3(-1f, -1f, 9f), new Vector3(1f, 1f, 11f)), "behind");
        Assert.False(frustum.IntersectsAabb(new Vector3(-120f, -1f, -11f), new Vector3(-100f, 1f, -9f)), "left");
        Assert.False(frustum.IntersectsAabb(new Vector3(-1f, 50f, -11f), new Vector3(1f, 60f, -9f)), "top");

        // Straddles the left plane: its centre is outside, part of it is inside.
        Vector3 straddleMin = new(-100f, -1f, -11f);
        Vector3 straddleMax = new(0f, 1f, -9f);
        Assert.False(frustum.ContainsPoint((straddleMin + straddleMax) * 0.5f));
        Assert.True(frustum.IntersectsAabb(straddleMin, straddleMax));

        // Straddles the near plane: it contains the eye.
        Assert.True(frustum.IntersectsAabb(new Vector3(-0.5f), new Vector3(0.5f)));
        // A floor slab under and behind the eye that reaches in front of it.
        Assert.True(frustum.IntersectsAabb(new Vector3(-16f, -2f, -16f), new Vector3(16f, -1f, 16f)));
        Assert.True(frustum.IntersectsBox(new Vector3(0f, 0f, -20f), new Vector3(1f)));
    }

    [Fact]
    public void DegenerateMatrix_GivesAnInvalidFrustumThatAcceptsEverything()
    {
        ViewFrustum zero = ViewFrustum.FromViewProjection(default(Matrix4));
        ViewFrustum unset = default;

        Assert.False(zero.IsValid);
        Assert.False(unset.IsValid);
        Assert.True(zero.IntersectsAabb(new Vector3(1000f), new Vector3(1001f)));
        Assert.True(unset.ContainsPoint(new Vector3(-1000f)));
    }

    [Fact]
    public void ExtractionAndTests_AllocateNothing()
    {
        Camera camera = new(new Vector3(5f, 1.85f, 5f));
        Matrix4 view = camera.GetViewMatrix();
        Matrix4 projection = GameProjection();
        int hits = 0;

        long bytes = AllocationAssert.AtMost(0, () =>
        {
            ViewFrustum frustum = ViewFrustum.FromViewAndProjection(view, projection);
            for (int i = 0; i < 64; i++)
            {
                if (frustum.IntersectsAabb(new Vector3(i, 0f, -i), new Vector3(i + 1f, 1f, 1f - i)))
                {
                    hits++;
                }
            }
        });

        Assert.Equal(0, bytes);
        Assert.True(hits > 0);
    }
}
