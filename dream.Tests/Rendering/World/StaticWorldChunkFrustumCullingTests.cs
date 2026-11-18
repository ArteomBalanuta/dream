using System.Reflection;
using System.Runtime.CompilerServices;
using Dream.Rendering.Core;
using Dream.Rendering.World;
using Dream.Simulation.Players;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using Dream.Tests.Rendering.Core;
using Dream.Tests.Utilities;
using OpenTK.Mathematics;
using Xunit;
using Xunit.Abstractions;

namespace Dream.Tests.Rendering.World;

// GPU-2: static chunk meshes are culled against the view frustum as well as by distance, and the
// stats count what was actually drawn. A recording mesh store stands in for GL, and the Render
// carries the game's projection so the public Draw path builds the frustum itself.
public class StaticWorldChunkFrustumCullingTests
{
    // The spawn camera: PlayScene's player start (5, 1.5, 5) plus the default eye height 0.35,
    // facing the Camera default (-Z).
    private static readonly Vector3 SpawnEye = new(5f, 1.5f + 0.35f, 5f);
    private readonly ITestOutputHelper _output;

    public StaticWorldChunkFrustumCullingTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void CameraFacingPositiveX_DoesNotDrawChunksBehindIt()
    {
        Camera camera = new(new Vector3(28f, 2f, 28f));
        camera.SetFacing(Vector3.UnitX);
        CountingMeshStore store = new();
        using StaticWorldChunkRenderer renderer = CreateRenderer(store);
        Render render = RenderWithGameProjection();
        GeometryRenderExport export = CaptureStartupExport();
        DrawUntilApplied(renderer, render, camera, export);

        store.ResetDraws();
        StaticWorldChunkRenderStats stats = renderer.Draw(render, camera, export);

        Assert.NotEmpty(store.Drawn);
        // Every mesh entirely at -X of the eye is behind the near plane and must not be drawn.
        Assert.Contains(store.Live.Values, bounds => bounds.Max.X < camera.Position.X);
        Assert.DoesNotContain(store.Drawn, mesh => mesh.Bounds.Max.X < camera.Position.X);
        Assert.Contains(store.Drawn, mesh => mesh.Bounds.Min.X > camera.Position.X);
        Assert.True(stats.FrustumCulledMeshes > 0);
        Assert.Equal(store.Drawn.Count, stats.MeshCount);
        Assert.Equal(store.Drawn.Select(mesh => mesh.Key.ChunkKey).Distinct().Count(), stats.VisibleChunks);
        Assert.Equal(store.Live.Count, stats.MeshCount + stats.FrustumCulledMeshes + stats.DistanceCulledMeshes);
    }

    [Fact]
    public void ExplicitFrustum_MatchesTheProjectionFromRender()
    {
        Camera camera = new(new Vector3(28f, 2f, 28f));
        camera.SetFacing(Vector3.UnitX);
        CountingMeshStore store = new();
        using StaticWorldChunkRenderer renderer = CreateRenderer(store);
        Render render = RenderWithGameProjection();
        GeometryRenderExport export = CaptureStartupExport();
        DrawUntilApplied(renderer, render, camera, export);

        StaticWorldChunkRenderStats implicitStats = renderer.Draw(render, camera, export);
        ViewFrustum frustum = ViewFrustum.FromViewAndProjection(camera.GetViewMatrix(), ViewFrustumTests.GameProjection());
        StaticWorldChunkRenderStats explicitStats = renderer.Draw(render, camera, frustum, export);

        Assert.Equal(implicitStats.MeshCount, explicitStats.MeshCount);
        Assert.Equal(implicitStats.VisibleChunks, explicitStats.VisibleChunks);
        Assert.Equal(implicitStats.FrustumCulledMeshes, explicitStats.FrustumCulledMeshes);
    }

    [Fact]
    public void VisibleChunks_CountsDistinctChunks_NotMeshes()
    {
        CountingMeshStore store = new();
        using StaticWorldChunkRenderer renderer = CreateRenderer(store);
        Render render = RenderWithGameProjection();
        Camera camera = new(new Vector3(28f, 12f, 28f));
        camera.SetFacing(new Vector3(0f, -1f, 0.001f));
        GeometryRenderExport export = CaptureStartupExport();
        DrawUntilApplied(renderer, render, camera, export);

        store.ResetDraws();
        StaticWorldChunkRenderStats stats = renderer.Draw(render, camera, export);

        int distinctChunks = store.Drawn.Select(mesh => mesh.Key.ChunkKey).Distinct().Count();
        Assert.Equal(distinctChunks, stats.VisibleChunks);
        Assert.Equal(store.Drawn.Count, stats.MeshCount);
        Assert.True(stats.VisibleChunks <= stats.MeshCount);
    }

    // (0, 0, -1) is the spawn facing; (1, 0, 1) looks across the startup floor toward its centre.
    [Theory]
    [InlineData(0f, 0f, -1f)]
    [InlineData(1f, 0f, 1f)]
    public void SpawnCamera_DrawsFewerMeshesWithTheFrustum(float facingX, float facingY, float facingZ)
    {
        Camera camera = new(SpawnEye);
        camera.SetFacing(new Vector3(facingX, facingY, facingZ));
        GeometryRenderExport export = CaptureStartupExport();

        // Before: an uninitialized Render has a zero projection, so only the distance cull runs,
        // which is what the renderer did before the frustum.
        CountingMeshStore beforeStore = new();
        using StaticWorldChunkRenderer before = CreateRenderer(beforeStore);
        Render noProjection = (Render)RuntimeHelpers.GetUninitializedObject(typeof(Render));
        DrawUntilApplied(before, noProjection, camera, export);
        StaticWorldChunkRenderStats beforeStats = before.Draw(noProjection, camera, export);

        CountingMeshStore afterStore = new();
        using StaticWorldChunkRenderer after = CreateRenderer(afterStore);
        Render render = RenderWithGameProjection();
        DrawUntilApplied(after, render, camera, export);
        StaticWorldChunkRenderStats afterStats = after.Draw(render, camera, export);

        _output.WriteLine(
            $"spawn camera {SpawnEye} facing {camera.Front}: meshes {beforeStats.MeshCount} -> {afterStats.MeshCount}, " +
            $"chunks {beforeStats.VisibleChunks} -> {afterStats.VisibleChunks}, " +
            $"frustum culled {afterStats.FrustumCulledMeshes}, distance culled {afterStats.DistanceCulledMeshes}, " +
            $"triangles {beforeStats.Triangles} -> {afterStats.Triangles}");

        Assert.Equal(0, beforeStats.FrustumCulledMeshes);
        Assert.True(afterStats.MeshCount > 0);
        Assert.True(afterStats.MeshCount < beforeStats.MeshCount, $"{afterStats.MeshCount} < {beforeStats.MeshCount}");
        Assert.Equal(beforeStats.MeshCount, afterStats.MeshCount + afterStats.FrustumCulledMeshes);
    }

    [Fact]
    public void CulledDraw_AllocatesNothing()
    {
        Camera camera = new(new Vector3(28f, 2f, 28f));
        camera.SetFacing(Vector3.UnitX);
        CountingMeshStore store = new() { RecordDraws = false };
        using StaticWorldChunkRenderer renderer = CreateRenderer(store);
        Render render = RenderWithGameProjection();
        GeometryRenderExport export = CaptureStartupExport();
        DrawUntilApplied(renderer, render, camera, export);

        long bytes = AllocationAssert.AtMost(0, () => renderer.Draw(render, camera, export));

        Assert.Equal(0, bytes);
        StaticWorldChunkRenderStats stats = renderer.Draw(render, camera, export);
        Assert.True(stats.FrustumCulledMeshes > 0);
        Assert.True(stats.MeshCount > 0);
    }

    [Fact]
    public void ChunkVisibility_KeepsTheDistanceCull()
    {
        Camera camera = new(Vector3.Zero);
        ViewFrustum frustum = ViewFrustum.FromViewAndProjection(camera.GetViewMatrix(), ViewFrustumTests.GameProjection());
        StaticWorldChunkBounds near = new(new Vector3(-1f, -1f, -12f), new Vector3(1f, 1f, -10f));
        // Inside the frustum (far plane 100) but past the 48 m render distance.
        StaticWorldChunkBounds far = new(new Vector3(-1f, -1f, -82f), new Vector3(1f, 1f, -80f));
        StaticWorldChunkBounds behind = new(new Vector3(-1f, -1f, 10f), new Vector3(1f, 1f, 12f));

        Assert.True(StaticWorldChunkRenderer.IsChunkVisible(camera.Position, camera.Front, frustum, near));
        Assert.True(frustum.IntersectsAabb(far.Min, far.Max));
        Assert.False(StaticWorldChunkRenderer.IsChunkVisible(camera.Position, camera.Front, frustum, far));
        Assert.True(StaticWorldChunkRenderer.IsChunkVisible(camera.Position, camera.Front, behind));
        Assert.False(StaticWorldChunkRenderer.IsChunkVisible(camera.Position, camera.Front, frustum, behind));
    }

    internal static Render RenderWithGameProjection()
    {
        Render render = (Render)RuntimeHelpers.GetUninitializedObject(typeof(Render));
        typeof(Render)
            .GetField("_projection", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(render, ViewFrustumTests.GameProjection());
        Assert.Equal(ViewFrustumTests.GameProjection(), render.ProjectionMatrix);
        return render;
    }

    private static StaticWorldChunkRenderer CreateRenderer(CountingMeshStore store)
    {
        return new StaticWorldChunkRenderer(
            request => Task.FromResult(StaticWorldChunkRenderer.BuildBatch(request)),
            new RenderFaultLog(_ => { }),
            store);
    }

    private static void DrawUntilApplied(StaticWorldChunkRenderer renderer, Render render, Camera camera, GeometryRenderExport export)
    {
        for (int frame = 0; frame < 64; frame++)
        {
            StaticWorldChunkRenderStats stats = renderer.Draw(render, camera, export);
            if (frame > 0 && stats.RebuiltMeshes == 0)
            {
                return;
            }
        }

        Assert.Fail("The static chunk batch was not applied.");
    }

    private static GeometryRenderExport CaptureStartupExport()
    {
        Map map = new(
            physics: null,
            generateStartupTerrain: true,
            width: WorldDefaults.StartupFloorSize,
            depth: WorldDefaults.StartupFloorSize);
        return map.CapturePresentationGeometryRenderExport(SpawnEye, WorldVisibility.MaxRenderDistance);
    }

    private sealed class CountingMeshStore : IStaticWorldChunkMeshStore
    {
        public Dictionary<StaticWorldChunkMeshKey, StaticWorldChunkBounds> Live { get; } = new();

        public List<StaticWorldChunkMeshInfo> Drawn { get; } = new();

        public bool RecordDraws { get; init; } = true;

        public void ResetDraws()
        {
            Drawn.Clear();
        }

        public void Upload(StaticWorldChunkMeshKey key, StaticWorldChunkMesh mesh)
        {
            if (mesh.ExposedFaceCount > 0)
            {
                Live[key] = mesh.Bounds;
            }
            else
            {
                Live.Remove(key);
            }
        }

        public void Remove(StaticWorldChunkMeshKey key)
        {
            Live.Remove(key);
        }

        public bool Contains(StaticWorldChunkMeshKey key)
        {
            return Live.ContainsKey(key);
        }

        public void BeginPass(Render render, Camera camera)
        {
        }

        public void BindMaterial(Render render, int materialSlot)
        {
        }

        public void Draw(Render render, in StaticWorldChunkMeshInfo mesh)
        {
            Assert.True(Live.ContainsKey(mesh.Key));
            if (RecordDraws)
            {
                Drawn.Add(mesh);
            }
        }

        public void EndPass(Render render)
        {
        }

        public void Clear()
        {
            Live.Clear();
        }
    }
}
