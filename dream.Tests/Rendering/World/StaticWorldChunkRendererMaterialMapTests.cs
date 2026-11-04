using System.Runtime.CompilerServices;
using Dream.Rendering.Core;
using Dream.Rendering.World;
using Dream.Simulation.Players;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using Dream.Tests.Rendering.Core;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Rendering.World;

// The renderer keeps the material map in step with the extracted export and hands it to the store
// once per change, before the pass that draws.
public class StaticWorldChunkRendererMaterialMapTests
{
    private static readonly Render UnusedRender = (Render)RuntimeHelpers.GetUninitializedObject(typeof(Render));

    [Fact]
    public void SyncsOnceAfterTheFirstBuild_NotOnUnchangedFrames_AndOnceAfterAnEdit()
    {
        MapStore store = new();
        using StaticWorldChunkRenderer renderer = new(
            request => Task.FromResult(StaticWorldChunkRenderer.BuildBatch(request)),
            new RenderFaultLog(_ => { }),
            store);
        Camera camera = new(new Vector3(3f, 2f, 3f));
        camera.SetFacing(new Vector3(-1f, -0.5f, -1f));
        ViewFrustum frustum = ViewFrustum.FromViewAndProjection(camera.GetViewMatrix(), ViewFrustumTests.GameProjection());

        Dictionary<(int, int, int), GeometryMaterialId> slots = new()
        {
            [(0, 0, 0)] = GeometryMaterialId.Grass,
            [(1, 0, 0)] = GeometryMaterialId.Stone
        };
        GeometryRenderExport export = CreateExport(slots, 1, []);
        for (int i = 0; i < 6; i++)
        {
            renderer.Draw(UnusedRender, camera, in frustum, export);
        }

        Assert.True(store.Drawn > 0, "no mesh was drawn, so no pass ran");
        Assert.Equal(1, store.SyncCount);
        Assert.Equal(new Vector3i(2, 1, 1), store.LastSize);
        Assert.Equal(1, store.LastTexels[0]);
        Assert.Equal(3, store.LastTexels[1]);

        slots[(0, 1, 0)] = GeometryMaterialId.Dirt;
        export = CreateExport(slots, 2, [new GeometryCellKey(0, 1, 0)]);
        for (int i = 0; i < 6; i++)
        {
            renderer.Draw(UnusedRender, camera, in frustum, export);
        }

        Assert.Equal(2, store.SyncCount);
        Assert.Equal(new Vector3i(2, 2, 1), store.LastSize);
        Assert.Equal(TerrainMaterialMap.CoveredTexel, store.LastTexels[0]);

        slots.Remove((0, 1, 0));
        export = CreateExport(slots, 3, [new GeometryCellKey(0, 1, 0)]);
        for (int i = 0; i < 6; i++)
        {
            renderer.Draw(UnusedRender, camera, in frustum, export);
        }

        Assert.Equal(3, store.SyncCount);
        Assert.Equal(new Vector3i(2, 1, 1), store.LastSize);
    }

    [Fact]
    public void FramesThatDrawNothing_DoNotSync_AndTheNextDrawingFrameSyncsOnce()
    {
        MapStore store = new();
        using StaticWorldChunkRenderer renderer = new(
            request => Task.FromResult(StaticWorldChunkRenderer.BuildBatch(request)),
            new RenderFaultLog(_ => { }),
            store);
        Camera away = new(new Vector3(3f, 2f, 3f));
        away.SetFacing(new Vector3(1f, 0.5f, 1f));
        ViewFrustum awayFrustum = ViewFrustum.FromViewAndProjection(away.GetViewMatrix(), ViewFrustumTests.GameProjection());
        Camera toward = new(new Vector3(3f, 2f, 3f));
        toward.SetFacing(new Vector3(-1f, -0.5f, -1f));
        ViewFrustum towardFrustum = ViewFrustum.FromViewAndProjection(toward.GetViewMatrix(), ViewFrustumTests.GameProjection());
        GeometryRenderExport export = CreateExport(new() { [(0, 0, 0)] = GeometryMaterialId.Grass }, 1, []);

        for (int i = 0; i < 6; i++)
        {
            renderer.Draw(UnusedRender, away, in awayFrustum, export);
        }

        Assert.Equal(0, store.Drawn);
        Assert.Equal(0, store.SyncCount);

        for (int i = 0; i < 6; i++)
        {
            renderer.Draw(UnusedRender, toward, in towardFrustum, export);
        }

        Assert.True(store.Drawn > 0);
        Assert.Equal(1, store.SyncCount);
        Assert.Equal(new Vector3i(1, 1, 1), store.LastSize);
    }

    [Fact]
    public void Dispose_ClearsTheMap()
    {
        MapStore store = new();
        StaticWorldChunkRenderer renderer = new(
            request => Task.FromResult(StaticWorldChunkRenderer.BuildBatch(request)),
            new RenderFaultLog(_ => { }),
            store);
        Camera camera = new(new Vector3(3f, 2f, 3f));
        camera.SetFacing(new Vector3(-1f, -0.5f, -1f));
        ViewFrustum frustum = ViewFrustum.FromViewAndProjection(camera.GetViewMatrix(), ViewFrustumTests.GameProjection());
        GeometryRenderExport export = CreateExport(new() { [(0, 0, 0)] = GeometryMaterialId.Grass }, 1, []);
        for (int i = 0; i < 4; i++)
        {
            renderer.Draw(UnusedRender, camera, in frustum, export);
        }

        Assert.False(renderer.TerrainMaterialMap.IsEmpty);

        renderer.Dispose();

        Assert.True(renderer.TerrainMaterialMap.IsEmpty);
        Assert.Equal(Vector3i.Zero, renderer.TerrainMaterialMap.Size);
    }

    private static GeometryRenderExport CreateExport(
        Dictionary<(int X, int Y, int Z), GeometryMaterialId> slots,
        long version,
        IReadOnlyList<GeometryCellKey> dirtyCells)
    {
        List<Cube> cubes = [];
        foreach (((int x, int y, int z), GeometryMaterialId material) in slots
            .OrderBy(pair => pair.Key.X).ThenBy(pair => pair.Key.Y).ThenBy(pair => pair.Key.Z))
        {
            cubes.Add(new Cube(isEmpty: false, isSolid: true)
            {
                Position = new Vector3(x + 0.5f, y + 0.5f, z + 0.5f),
                MaterialId = material,
                ShaderName = "default"
            });
        }

        return new GeometryRenderExport(
            DirtyCells: dirtyCells,
            Cells: [],
            Blocks: [],
            CompatibilityCubes: cubes)
        {
            StaticContentVersion = version
        };
    }

    private sealed class MapStore : IStaticWorldChunkMeshStore
    {
        private readonly HashSet<StaticWorldChunkMeshKey> _keys = new();

        public int SyncCount { get; private set; }

        public int Drawn { get; private set; }

        public Vector3i LastSize { get; private set; }

        public byte[] LastTexels { get; private set; } = [];

        public void SyncMaterialMap(TerrainMaterialMap map)
        {
            SyncCount++;
            LastSize = map.Size;
            LastTexels = map.Texels.AsSpan(0, map.Count).ToArray();
        }

        public void Upload(StaticWorldChunkMeshKey key, StaticWorldChunkMesh mesh) => _keys.Add(key);

        public void Remove(StaticWorldChunkMeshKey key) => _keys.Remove(key);

        public bool Contains(StaticWorldChunkMeshKey key) => _keys.Contains(key);

        public void BeginPass(Render render, Camera camera)
        {
        }

        public void BindMaterial(Render render, int materialSlot)
        {
        }

        public void Draw(Render render, in StaticWorldChunkMeshInfo mesh) => Drawn++;

        public void EndPass(Render render)
        {
        }

        public void Clear() => _keys.Clear();
    }
}
