using Dream.Rendering.Core;
using Dream.Rendering.World;
using Dream.Simulation.Players;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using Dream.Tests.Rendering.Core;
using Dream.Tests.Utilities;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Rendering.World;

// GPU-2: dynamic and fallback cubes and dynamic geometry blocks pass the distance cull and the
// view frustum before they are batched.
public class WorldRendererFrustumCullingTests
{
    private static Camera CameraFacingPositiveX(out ViewFrustum frustum)
    {
        Camera camera = new(new Vector3(28f, 2f, 28f));
        camera.SetFacing(Vector3.UnitX);
        frustum = ViewFrustum.FromViewAndProjection(camera.GetViewMatrix(), ViewFrustumTests.GameProjection());
        return camera;
    }

    private static Cube CubeAt(Vector3 position)
    {
        return new Cube(isEmpty: false, isSolid: true)
        {
            Position = position,
            MaterialId = GeometryMaterialId.Stone,
            ShaderName = "default"
        };
    }

    [Fact]
    public void Cubes_BehindOrBesideTheView_AreCulled_AndCubesAheadAreKept()
    {
        Camera camera = CameraFacingPositiveX(out ViewFrustum frustum);

        Assert.True(WorldRenderer.IsCubeInView(camera.Position, camera.Front, frustum, CubeAt(new Vector3(36f, 2f, 28f))));
        // Within the 1.5 m "always visible" radius of the old cull, but behind the eye.
        Assert.False(WorldRenderer.IsCubeInView(camera.Position, camera.Front, frustum, CubeAt(new Vector3(26.5f, 2f, 28f))));
        Assert.False(WorldRenderer.IsCubeInView(camera.Position, camera.Front, frustum, CubeAt(new Vector3(20f, 2f, 28f))));
        // In front but far off to the side (the old dot-product cull kept it).
        Assert.True(WorldVisibility.IsCubeVisible(camera.Position, camera.Front, CubeAt(new Vector3(32f, 2f, 60f))));
        Assert.False(WorldRenderer.IsCubeInView(camera.Position, camera.Front, frustum, CubeAt(new Vector3(32f, 2f, 60f))));
        // Inside the frustum but past the render distance.
        Assert.False(WorldRenderer.IsCubeInView(camera.Position, camera.Front, frustum, CubeAt(new Vector3(90f, 2f, 28f))));
    }

    [Fact]
    public void CubeStraddlingTheViewEdge_IsKept()
    {
        Camera camera = CameraFacingPositiveX(out ViewFrustum frustum);
        // The eye sits inside this cube.
        Assert.True(WorldRenderer.IsCubeInView(camera.Position, camera.Front, frustum, CubeAt(camera.Position)));
    }

    [Fact]
    public void GeometryBlocks_AreFrustumCulled()
    {
        Camera camera = CameraFacingPositiveX(out ViewFrustum frustum);
        GeometryRenderBlockDescriptor ahead = new(new GeometryCellKey(36, 2, 28), new Vector3(36.5f, 2.5f, 28.5f), 1f, GeometryMaterialId.Stone, "default");
        GeometryRenderBlockDescriptor behind = ahead with { Center = new Vector3(20.5f, 2.5f, 28.5f) };

        Assert.True(WorldRenderer.IsGeometryBlockInView(camera.Position, camera.Front, frustum, ahead));
        Assert.False(WorldRenderer.IsGeometryBlockInView(camera.Position, camera.Front, frustum, behind));
    }

    [Fact]
    public void InvalidFrustum_FallsBackToTheDistanceCull()
    {
        Camera camera = new(new Vector3(28f, 2f, 28f));
        camera.SetFacing(Vector3.UnitX);
        ViewFrustum unset = default;
        Cube beside = CubeAt(new Vector3(32f, 2f, 60f));

        Assert.Equal(
            WorldVisibility.IsCubeVisible(camera.Position, camera.Front, beside),
            WorldRenderer.IsCubeInView(camera.Position, camera.Front, unset, beside));
    }

    [Fact]
    public void CubeCull_AllocatesNothing()
    {
        Camera camera = CameraFacingPositiveX(out ViewFrustum frustum);
        Cube[] cubes = new Cube[64];
        for (int i = 0; i < cubes.Length; i++)
        {
            cubes[i] = CubeAt(new Vector3(i, 2f, 28f));
        }

        int kept = 0;
        long bytes = AllocationAssert.MeasureBytesPerIteration(() =>
        {
            for (int i = 0; i < cubes.Length; i++)
            {
                if (WorldRenderer.IsCubeInView(camera.Position, camera.Front, frustum, cubes[i]))
                {
                    kept++;
                }
            }
        });

        Assert.Equal(0, bytes);
        Assert.True(kept > 0);
    }

    [Fact]
    public void DrawWorld_CullsWithTheFrustum()
    {
        string source = File.ReadAllText(Path.Combine(FindRepoRoot(), "dream", "Rendering", "World", "WorldRenderer.cs"));
        Assert.Contains("CreateFrustum(camera, _render)", source);
        Assert.Contains("_staticChunkRenderer.Draw(_render, camera, frustum, export)", source);
        Assert.DoesNotContain("WorldVisibility.IsCubeVisible(camera.Position, camera.Front, cube)", source);
        Assert.DoesNotContain("WorldVisibility.IsGeometryBlockVisible(camera.Position, camera.Front, block)", source);
    }

    [Fact]
    public void DrawCubeTransparent_IsRemovedFromRender()
    {
        string source = PartialClassSource.Read(Path.Combine(FindRepoRoot(), "dream", "Rendering", "Core", "Render.cs"));
        Assert.DoesNotContain("DrawCubeTransparent", source);
    }

    private static string FindRepoRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current != null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "dream")) &&
                Directory.Exists(Path.Combine(current.FullName, "dream.Tests")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root.");
    }
}
