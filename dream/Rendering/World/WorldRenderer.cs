using Dream.Rendering.Core;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;

namespace Dream.Rendering.World;

public class WorldRenderer
{
    private readonly Render _render;

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
        WorldRenderStats cubeStats = DrawWorld(camera, export.CompatibilityCubes);

        List<GeometryRenderBlockDescriptor> visibleBlocks = [];
        foreach (GeometryRenderBlockDescriptor block in export.Blocks)
        {
            if (!WorldVisibility.IsGeometryBlockVisible(camera.Position, camera.Front, block))
            {
                continue;
            }

            visibleBlocks.Add(block);
        }

        List<WorldGeometryRenderBatch> geometryBatches = WorldRenderBatchPlanner
            .CreateGeometryBlockBatches(visibleBlocks)
            .ToList();
        foreach (WorldGeometryRenderBatch batch in geometryBatches)
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
            cubeStats.VisibleCompatibilityCubes,
            visibleBlocks.Count,
            cubeStats.CompatibilityBatchCount,
            geometryBatches.Count);
    }

    // Remote players intentionally stay out of the world-geometry renderer so
    // replicated actor ownership does not drift back into the cube/world path.
}
