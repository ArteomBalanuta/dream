using System.Text.RegularExpressions;
using OpenTK.Mathematics;
using Xunit;
using Dream.Rendering.Core;
using Dream.Rendering.World;
using Dream.Simulation.World;

namespace Dream.Tests.Rendering.Core;

// Wave 8 tester, GPU-2 (ViewFrustum culling in WorldRenderer and StaticWorldChunkRenderer): cubes
// and chunks touching the near plane (0.1 m, Render.UpdateProjection) are never culled, wherever
// the camera looks, and the first-person viewmodel is drawn outside the culling path.
public class Wave8FrustumNearPlaneTests
{
    private const float NearPlane = 0.1f;

    public static TheoryData<Vector3> Fronts() => new()
    {
        Vector3.UnitZ,
        -Vector3.UnitZ,
        Vector3.UnitX,
        Vector3.Normalize(new Vector3(1f, -1f, 1f)),
        Vector3.Normalize(new Vector3(0.01f, -1f, 0f)),
        Vector3.Normalize(new Vector3(0.01f, 1f, 0f))
    };

    [Theory]
    [MemberData(nameof(Fronts))]
    public void CubeJustInFrontOfTheCamera_CrossingTheNearPlane_IsDrawn(Vector3 front)
    {
        Vector3 camera = new(10.5f, 2.5f, 10.5f);
        ViewFrustum frustum = Frustum(camera, front);
        // The cube's near face 0.05 m in front of the eye: closer than the near plane, so the
        // cube straddles it.
        Cube cube = CubeAt(camera + front * 0.55f);
        Assert.True(WorldRenderer.IsCubeInView(camera, front, frustum, cube));
    }

    [Theory]
    [MemberData(nameof(Fronts))]
    public void CameraInsideACube_TheCubeIsDrawn(Vector3 front)
    {
        Vector3 camera = new(10.3f, 2.7f, 10.6f);
        Assert.True(WorldRenderer.IsCubeInView(camera, front, Frustum(camera, front), CubeAt(new Vector3(10.5f, 2.5f, 10.5f))));
    }

    [Theory]
    [MemberData(nameof(Fronts))]
    public void CubeAtTheScreenEdge_InsideTheNearPlaneDistance_IsDrawn(Vector3 front)
    {
        Vector3 camera = new(10.5f, 2.5f, 10.5f);
        Vector3 right = Vector3.Normalize(Vector3.Cross(front, MathF.Abs(front.Y) > 0.9f ? Vector3.UnitZ : Vector3.UnitY));
        // Beside the eye, half in the view: its centre is outside the side plane at the near
        // distance, a corner is inside.
        Cube cube = CubeAt(camera + front * 0.3f + right * 0.7f);
        Assert.True(WorldRenderer.IsCubeInView(camera, front, Frustum(camera, front), cube));
    }

    [Theory]
    [MemberData(nameof(Fronts))]
    public void CubeWhollyBehindTheCamera_IsCulled(Vector3 front)
    {
        Vector3 camera = new(10.5f, 2.5f, 10.5f);
        // The bounding box (half extent 0.866) ends 0.134 m behind the eye.
        Cube cube = CubeAt(camera - front * 1.0f - front * 0.866f * (MathF.Abs(front.X) + MathF.Abs(front.Y) + MathF.Abs(front.Z)));
        Assert.False(WorldRenderer.IsCubeInView(camera, front, Frustum(camera, front), cube));
    }

    [Theory]
    [MemberData(nameof(Fronts))]
    public void ChunkHoldingTheCamera_IsDrawn(Vector3 front)
    {
        Vector3 camera = new(10.5f, 2.5f, 10.5f);
        StaticWorldChunkBounds chunk = new(new Vector3(8f, 0f, 8f), new Vector3(24f, 3f, 24f));
        Assert.True(StaticWorldChunkRenderer.IsChunkVisible(camera, front, Frustum(camera, front), chunk));
        // And the chunk the player stands on (its top face just below the eye), unless the camera
        // looks straight up and it really is out of view.
        StaticWorldChunkBounds below = new(new Vector3(8f, 0f, 8f), new Vector3(24f, 2.45f, 24f));
        if (front.Y < 0.5f)
        {
            Assert.True(StaticWorldChunkRenderer.IsChunkVisible(camera, front, Frustum(camera, front), below));
        }
    }

    // The viewmodel pass has its own projection (near 0.01 m) and no culling: ViewModelRenderer
    // never asks a frustum, WorldVisibility or a bounds test whether to draw a slot.
    [Fact]
    public void ViewModelRenderer_NeverCulls()
    {
        string source = Regex.Replace(
            File.ReadAllText(Path.Combine(Integration.RepoRoot.Find(), "dream", "Rendering", "ViewModels", "ViewModelRenderer.cs")),
            @"//[^\n]*",
            string.Empty);
        Assert.DoesNotContain("ViewFrustum", source);
        Assert.DoesNotContain("IntersectsBox", source);
        Assert.DoesNotContain("IntersectsAabb", source);
        Assert.DoesNotContain("WorldVisibility", source);
        Assert.DoesNotContain("IsCubeInView", source);
        Assert.Contains("0.01f", source);
    }

    private static ViewFrustum Frustum(Vector3 camera, Vector3 front)
    {
        Vector3 up = MathF.Abs(front.Y) > 0.99f ? Vector3.UnitZ : Vector3.UnitY;
        Matrix4 view = Matrix4.LookAt(camera, camera + front, up);
        Matrix4 projection = Matrix4.CreatePerspectiveFieldOfView(MathHelper.DegreesToRadians(70f), 16f / 9f, NearPlane, 100f);
        ViewFrustum frustum = ViewFrustum.FromViewAndProjection(view, projection);
        Assert.True(frustum.IsValid);
        return frustum;
    }

    private static Cube CubeAt(Vector3 center)
    {
        return new Cube { Position = center, IsSolid = true, IsEmpty = false, ShaderName = "cube" };
    }
}
