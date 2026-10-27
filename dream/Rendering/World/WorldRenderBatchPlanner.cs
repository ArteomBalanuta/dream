using Dream.Rendering.Models;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;

namespace Dream.Rendering.World;

// A batch's item list is backed by a list the planner reuses. Batches from an instance planner
// are valid only until that planner plans the next frame, so callers must not keep them.
public readonly record struct WorldRenderBatch(
    AnimatedModel? Animation,
    string ShaderName,
    IReadOnlyList<Cube> Cubes);

public readonly record struct WorldGeometryRenderBatch(
    AnimatedModel? Animation,
    string ShaderName,
    IReadOnlyList<GeometryRenderBlockDescriptor> Blocks);

// Groups cubes and blocks into draw batches by (MaterialId, ShaderName) in first-seen order.
// An instance keeps its key map, per-key lists and batch lists across frames and clears them on
// each call, so planning allocates nothing once the lists have grown to the frame's size. The
// returned lists and the batches in them are only valid until the next call of the same kind.
// The static methods build fresh lists on every call for callers that keep the result.
public sealed class WorldRenderBatchPlanner
{
    private readonly Func<GeometryMaterialId, AnimatedModel?> _resolve;
    private readonly BatchGroups<Cube> _cubeGroups = new();
    private readonly BatchGroups<GeometryRenderBlockDescriptor> _blockGroups = new();
    private readonly List<WorldRenderBatch> _cubeBatches = new();
    private readonly List<WorldGeometryRenderBatch> _blockBatches = new();

    public WorldRenderBatchPlanner(Func<GeometryMaterialId, AnimatedModel?>? modelResolver = null)
    {
        _resolve = modelResolver ?? CubeMaterialModelCatalog.Resolve;
    }

    public IReadOnlyList<WorldRenderBatch> PlanCubeBatches(IReadOnlyList<Cube> cubes)
    {
        _cubeGroups.Reset();
        _cubeBatches.Clear();
        for (int i = 0; i < cubes.Count; i++)
        {
            Cube cube = cubes[i];
            List<Cube> members = _cubeGroups.GetOrAdd(cube.MaterialId, cube.ShaderName, out bool isNew);
            if (isNew)
            {
                _cubeBatches.Add(new WorldRenderBatch(_resolve(cube.MaterialId), cube.ShaderName, members));
            }

            members.Add(cube);
        }

        return _cubeBatches;
    }

    public IReadOnlyList<WorldGeometryRenderBatch> PlanGeometryBlockBatches(IReadOnlyList<GeometryRenderBlockDescriptor> blocks)
    {
        _blockGroups.Reset();
        _blockBatches.Clear();
        for (int i = 0; i < blocks.Count; i++)
        {
            GeometryRenderBlockDescriptor block = blocks[i];
            List<GeometryRenderBlockDescriptor> members = _blockGroups.GetOrAdd(block.MaterialId, block.ShaderName, out bool isNew);
            if (isNew)
            {
                _blockBatches.Add(new WorldGeometryRenderBatch(_resolve(block.MaterialId), block.ShaderName, members));
            }

            members.Add(block);
        }

        return _blockBatches;
    }

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
        // A planner used once owns its lists exclusively, so the returned batches stay valid.
        var planner = new WorldRenderBatchPlanner(modelResolver);
        IReadOnlyList<GeometryRenderBlockDescriptor> list = blocks as IReadOnlyList<GeometryRenderBlockDescriptor> ?? blocks.ToList();
        return new List<WorldGeometryRenderBatch>(planner.PlanGeometryBlockBatches(list));
    }

    public static List<WorldRenderBatch> CreateBatches(
        IEnumerable<Cube> cubes,
        Func<GeometryMaterialId, AnimatedModel?>? modelResolver = null)
    {
        // A planner used once owns its lists exclusively, so the returned batches stay valid.
        var planner = new WorldRenderBatchPlanner(modelResolver);
        IReadOnlyList<Cube> list = cubes as IReadOnlyList<Cube> ?? cubes.ToList();
        return new List<WorldRenderBatch>(planner.PlanCubeBatches(list));
    }

    // Keeps the key-to-index map and one member list per key. A cleared Dictionary can hand out
    // entries in a different order after removals, so batch order comes from the index, which
    // is assigned in first-seen order, never from enumerating the map.
    private sealed class BatchGroups<TItem>
    {
        private readonly Dictionary<(GeometryMaterialId MaterialId, string ShaderName), int> _indices = new();
        private readonly List<List<TItem>> _members = new();
        private int _usedCount;

        public void Reset()
        {
            _indices.Clear();
            for (int i = 0; i < _usedCount; i++)
            {
                _members[i].Clear();
            }

            _usedCount = 0;
        }

        public List<TItem> GetOrAdd(GeometryMaterialId materialId, string shaderName, out bool isNew)
        {
            var key = (materialId, shaderName);
            if (_indices.TryGetValue(key, out int index))
            {
                isNew = false;
                return _members[index];
            }

            index = _usedCount++;
            _indices.Add(key, index);
            if (index == _members.Count)
            {
                _members.Add(new List<TItem>());
            }

            isNew = true;
            return _members[index];
        }
    }
}
