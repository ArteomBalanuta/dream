using Dream.Rendering.Core;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;

namespace Dream.Rendering.World;

public class WorldRenderer
{
    private readonly Render _render;
    private readonly StaticWorldChunkRenderer _staticChunkRenderer = new();

    public WorldRenderer(Render render)
    {
        _render = render;
    }

    public WorldRenderStats DrawWorld(Camera camera, IEnumerable<Cube> cubes)
    {
        List<Cube> visibleCubes = new();
        foreach (Cube cube in cubes)
        {
            if (!WorldVisibility.IsCubeVisible(camera.Position, camera.Front, cube))
            {
                continue;
            }

            visibleCubes.Add(cube);
        }

        List<WorldRenderBatch> batches = WorldRenderBatchPlanner.CreateBatches(visibleCubes).ToList();
        foreach (WorldRenderBatch batch in batches)
        {
            if (batch.Animation != null)
            {
                _render.DrawAnimatedCubeBatch(camera, batch.Animation, batch.ShaderName, batch.Cubes);
                continue;
            }

            foreach (Cube cube in batch.Cubes)
            {
                _render.DrawCube(camera, cube);
            }
        }

        return new WorldRenderStats(
            visibleCubes.Count,
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
        List<Cube> visibleDynamicCubes = new();
        foreach (Cube cube in export.DynamicCompatibilityCubes)
        {
            if (!WorldVisibility.IsCubeVisible(camera.Position, camera.Front, cube))
            {
                continue;
            }

            visibleDynamicCubes.Add(cube);
        }

        List<WorldRenderBatch> dynamicBatches = WorldRenderBatchPlanner.CreateBatches(visibleDynamicCubes);
        foreach (WorldRenderBatch batch in dynamicBatches)
        {
            if (batch.Animation != null)
            {
                _render.DrawAnimatedCubeBatch(camera, batch.Animation, batch.ShaderName, batch.Cubes);
                continue;
            }

            foreach (Cube cube in batch.Cubes)
            {
                _render.DrawCube(camera, cube);
            }
        }

        List<GeometryRenderBlockDescriptor> visibleDynamicBlocks = new();
        foreach (GeometryRenderBlockDescriptor block in export.DynamicBlocks)
        {
            if (!WorldVisibility.IsGeometryBlockVisible(camera.Position, camera.Front, block))
            {
                continue;
            }

            visibleDynamicBlocks.Add(block);
        }

        List<WorldGeometryRenderBatch> dynamicBlockBatches = WorldRenderBatchPlanner.CreateGeometryBlockBatches(visibleDynamicBlocks);
        foreach (WorldGeometryRenderBatch batch in dynamicBlockBatches)
        {
            if (batch.Animation != null)
            {
                _render.DrawGeometryBlockBatch(camera, batch.Animation, batch.ShaderName, batch.Blocks);
                continue;
            }

            foreach (GeometryRenderBlockDescriptor block in batch.Blocks)
            {
                _render.DrawGeometryBlock(camera, block);
            }
        }

        return new WorldRenderStats(
            VisibleCompatibilityCubes: visibleDynamicCubes.Count,
            VisibleGeometryBlocks: visibleDynamicBlocks.Count,
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

    // Remote players intentionally stay out of the world-geometry renderer so
    // replicated actor ownership does not drift back into the cube/world path.
}
