using Dream.Rendering.Models;
using Dream.Simulation.World;

namespace Dream.Rendering.World;

public readonly record struct WorldRenderBatch(
    AnimatedModel? Animation,
    string ShaderName,
    IReadOnlyList<Cube> Cubes);

public static class WorldRenderBatchPlanner
{
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
