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
    public static List<WorldRenderBatch> CreateBatches(
        GeometryRenderExport export,
        Func<GeometryMaterialId, AnimatedModel?>? modelResolver = null)
    {
        return CreateBatches(export.DynamicCompatibilityCubes, modelResolver);
    }

    public static List<WorldGeometryRenderBatch> CreateGeometryBlockBatches(
        IEnumerable<GeometryRenderBlockDescriptor> blocks,
        Func<GeometryMaterialId, AnimatedModel?>? modelResolver = null)
    {
        Func<GeometryMaterialId, AnimatedModel?> resolve = modelResolver ?? CubeMaterialModelCatalog.Resolve;
        var batchIndices = new Dictionary<(GeometryMaterialId MaterialId, string ShaderName), int>();
        var batches = new List<WorldGeometryRenderBatch>();
        var members = new List<List<GeometryRenderBlockDescriptor>>();

        foreach (GeometryRenderBlockDescriptor block in blocks)
        {
            var key = (block.MaterialId, block.ShaderName);
            if (!batchIndices.TryGetValue(key, out int index))
            {
                index = batches.Count;
                batchIndices[key] = index;
                var batchBlocks = new List<GeometryRenderBlockDescriptor>();
                members.Add(batchBlocks);
                batches.Add(new WorldGeometryRenderBatch(resolve(block.MaterialId), block.ShaderName, batchBlocks));
            }

            members[index].Add(block);
        }

        return batches;
    }

    public static List<WorldRenderBatch> CreateBatches(
        IEnumerable<Cube> cubes,
        Func<GeometryMaterialId, AnimatedModel?>? modelResolver = null)
    {
        Func<GeometryMaterialId, AnimatedModel?> resolve = modelResolver ?? CubeMaterialModelCatalog.Resolve;
        var batchIndices = new Dictionary<(GeometryMaterialId MaterialId, string ShaderName), int>();
        var batches = new List<WorldRenderBatch>();
        var members = new List<List<Cube>>();

        foreach (Cube cube in cubes)
        {
            var key = (cube.MaterialId, cube.ShaderName);
            if (!batchIndices.TryGetValue(key, out int index))
            {
                index = batches.Count;
                batchIndices[key] = index;
                var batchCubes = new List<Cube>();
                members.Add(batchCubes);
                batches.Add(new WorldRenderBatch(resolve(cube.MaterialId), cube.ShaderName, batchCubes));
            }

            members[index].Add(cube);
        }

        return batches;
    }
}
