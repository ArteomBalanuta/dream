using Dream.Client.Presentation;
using Dream.Rendering.Core;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;

namespace Dream.Rendering.World;

public class WorldRenderer : IDisposable
{
    private readonly Render _render;
    private readonly StaticWorldChunkRenderer _staticChunkRenderer = new();
    private readonly WorldRenderBatchPlanner _batchPlanner = new();

    // Reused every frame and cleared before use. They and the planner's batches only hold this
    // frame's visible items, and nothing here or in Render keeps them past the draw calls.
    private readonly List<Cube> _visibleCubes = new();
    private readonly List<GeometryRenderBlockDescriptor> _visibleBlocks = new();
    private bool _disposed;

    public WorldRenderer(Render render)
    {
        _render = render;
    }

    public WorldRenderStats DrawWorld(Camera camera, IEnumerable<Cube> cubes)
    {
        _visibleCubes.Clear();
        foreach (Cube cube in cubes)
        {
            if (!WorldVisibility.IsCubeVisible(camera.Position, camera.Front, cube))
            {
                continue;
            }

            _visibleCubes.Add(cube);
        }

        IReadOnlyList<WorldRenderBatch> batches = _batchPlanner.PlanCubeBatches(_visibleCubes);
        DrawCubeBatches(camera, batches);

        return new WorldRenderStats(
            _visibleCubes.Count,
            VisibleGeometryBlocks: 0,
            batches.Count,
            GeometryBatchCount: 0);
    }

    public WorldRenderStats DrawWorld(WorldSessionPresentationState state)
    {
        return DrawWorld(state.Camera, state.VisibleWorld);
    }

    public WorldRenderStats DrawWorld(Camera camera, GeometryRenderExport export)
    {
        StaticWorldChunkRenderStats staticStats = _staticChunkRenderer.Draw(_render, camera, export);
        _visibleCubes.Clear();
        IReadOnlyList<Cube> dynamicCubes = export.DynamicCompatibilityCubes;
        for (int i = 0; i < dynamicCubes.Count; i++)
        {
            Cube cube = dynamicCubes[i];
            if (!WorldVisibility.IsCubeVisible(camera.Position, camera.Front, cube))
            {
                continue;
            }

            _visibleCubes.Add(cube);
        }

        IReadOnlyList<WorldRenderBatch> dynamicBatches = _batchPlanner.PlanCubeBatches(_visibleCubes);
        DrawCubeBatches(camera, dynamicBatches);

        _visibleBlocks.Clear();
        IReadOnlyList<GeometryRenderBlockDescriptor> dynamicBlocks = export.DynamicBlocks;
        for (int i = 0; i < dynamicBlocks.Count; i++)
        {
            GeometryRenderBlockDescriptor block = dynamicBlocks[i];
            if (!WorldVisibility.IsGeometryBlockVisible(camera.Position, camera.Front, block))
            {
                continue;
            }

            _visibleBlocks.Add(block);
        }

        IReadOnlyList<WorldGeometryRenderBatch> dynamicBlockBatches = _batchPlanner.PlanGeometryBlockBatches(_visibleBlocks);
        for (int batchIndex = 0; batchIndex < dynamicBlockBatches.Count; batchIndex++)
        {
            WorldGeometryRenderBatch batch = dynamicBlockBatches[batchIndex];
            if (batch.Animation != null)
            {
                _render.DrawGeometryBlockBatch(camera, batch.Animation, batch.ShaderName, batch.Blocks);
                continue;
            }

            IReadOnlyList<GeometryRenderBlockDescriptor> blocks = batch.Blocks;
            for (int i = 0; i < blocks.Count; i++)
            {
                _render.DrawGeometryBlock(camera, blocks[i]);
            }
        }

        return new WorldRenderStats(
            VisibleCompatibilityCubes: _visibleCubes.Count,
            VisibleGeometryBlocks: _visibleBlocks.Count,
            CompatibilityBatchCount: dynamicBatches.Count,
            GeometryBatchCount: dynamicBlockBatches.Count,
            VisibleStaticChunks: staticStats.VisibleChunks,
            StaticChunkMeshCount: staticStats.MeshCount,
            StaticChunkExposedFaces: staticStats.ExposedFaces,
            StaticChunkTriangles: staticStats.Triangles,
            StaticChunkSignatureMilliseconds: staticStats.SignatureMilliseconds,
            StaticChunkExtractMilliseconds: staticStats.ExtractMilliseconds,
            StaticChunkPlanMilliseconds: staticStats.PlanMilliseconds,
            StaticChunkSyncMilliseconds: staticStats.SyncMilliseconds,
            StaticChunkDrawMilliseconds: staticStats.DrawMilliseconds,
            StaticChunkRebuiltMeshes: staticStats.RebuiltMeshes,
            StaticChunkReusedMeshes: staticStats.ReusedMeshes);
    }

    // Releases the static chunk meshes. Render calls this on the render thread at teardown.
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _staticChunkRenderer.Dispose();
    }

    private void DrawCubeBatches(Camera camera, IReadOnlyList<WorldRenderBatch> batches)
    {
        for (int batchIndex = 0; batchIndex < batches.Count; batchIndex++)
        {
            WorldRenderBatch batch = batches[batchIndex];
            if (batch.Animation != null)
            {
                _render.DrawAnimatedCubeBatch(camera, batch.Animation, batch.ShaderName, batch.Cubes);
                continue;
            }

            IReadOnlyList<Cube> cubes = batch.Cubes;
            for (int i = 0; i < cubes.Count; i++)
            {
                _render.DrawCube(camera, cubes[i]);
            }
        }
    }

    // Remote players intentionally stay out of the world-geometry renderer so
    // replicated actor ownership does not drift back into the cube/world path.
}
