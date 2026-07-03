using Dream.Rendering.Models;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;

namespace Dream.Rendering.World;

public readonly record struct WorldRenderBatch(
    AnimatedModel? Animation,
    string ShaderName,
    IReadOnlyList<Cube> Cubes);

public readonly record struct WorldGeometryRenderBatch(
    AnimatedModel? Animation,
    string ShaderName,
    IReadOnlyList<GeometryRenderBlockDescriptor> Blocks);

public static class WorldRenderBatchPlanner
{
    public static List<WorldRenderBatch> CreateBatches(GeometryRenderExport export)
    {
        return CreateBatches(export.DynamicCompatibilityCubes);
    }

    public static List<WorldGeometryRenderBatch> CreateGeometryBlockBatches(IEnumerable<GeometryRenderBlockDescriptor> blocks)
    {
        var grouped = new Dictionary<(AnimatedModel? Animation, string ShaderName), List<GeometryRenderBlockDescriptor>>();

        foreach (GeometryRenderBlockDescriptor block in blocks)
        {
            var key = (block.Animation, block.ShaderName);
            if (!grouped.TryGetValue(key, out List<GeometryRenderBlockDescriptor>? batch))
            {
                batch = new List<GeometryRenderBlockDescriptor>();
                grouped[key] = batch;
            }

            batch.Add(block);
        }

        return grouped
            .Select(pair => new WorldGeometryRenderBatch(pair.Key.Animation, pair.Key.ShaderName, pair.Value))
            .ToList();
    }

    public static List<WorldRenderBatch> CreateBatches(IEnumerable<Cube> cubes)
    {
        var grouped = new Dictionary<(AnimatedModel? Animation, string ShaderName), List<Cube>>();

        foreach (Cube cube in cubes)
        {
            var key = (cube.Animation, cube.ShaderName);
            if (!grouped.TryGetValue(key, out List<Cube>? batch))
            {
                batch = new List<Cube>();
                grouped[key] = batch;
            }

            batch.Add(cube);
        }

        return grouped
            .Select(pair => new WorldRenderBatch(pair.Key.Animation, pair.Key.ShaderName, pair.Value))
            .ToList();
    }
}
