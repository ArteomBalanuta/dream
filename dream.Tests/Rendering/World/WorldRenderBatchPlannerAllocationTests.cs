using System.Runtime.Serialization;
using OpenTK.Mathematics;
using Xunit;
using Dream.Rendering.Models;
using Dream.Rendering.World;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using Dream.Tests.Utilities;

namespace Dream.Tests.Rendering.World;


public class WorldRenderBatchPlannerAllocationTests
{
    private static readonly AnimatedModel GrassModel = CreateModel();
    private static readonly AnimatedModel StoneModel = CreateModel();
    private static readonly AnimatedModel DirtModel = CreateModel();
    private static readonly Func<GeometryMaterialId, AnimatedModel?> Resolver = ResolveTestModel;

    private static readonly GeometryMaterialId[] Materials =
    [
        GeometryMaterialId.Grass,
        GeometryMaterialId.Stone,
        GeometryMaterialId.Dirt,
        GeometryMaterialId.Sand
    ];

    private static readonly string[] Shaders = ["cube", "default", "lit"];

    [Fact]
    public void PlanCubeBatches_Reused_Across_Frames_Matches_Static_Path()
    {
        var planner = new WorldRenderBatchPlanner(Resolver);
        var random = new Random(1234);

        for (int frame = 0; frame < 40; frame++)
        {
            List<Cube> cubes = CreateCubes(random, random.Next(0, 300), keyVariety: random.Next(1, 12));

            List<WorldRenderBatch> expected = WorldRenderBatchPlanner.CreateBatches(cubes, Resolver);
            IReadOnlyList<WorldRenderBatch> actual = planner.PlanCubeBatches(cubes);

            List<(GeometryMaterialId MaterialId, string ShaderName, List<Cube> Items)> reference =
                GroupInFirstSeenOrder(cubes, cube => (cube.MaterialId, cube.ShaderName));

            Assert.Equal(reference.Count, expected.Count);
            Assert.Equal(expected.Count, actual.Count);
            for (int i = 0; i < expected.Count; i++)
            {
                Assert.Same(ResolveTestModel(reference[i].MaterialId), expected[i].Animation);
                Assert.Equal(reference[i].ShaderName, expected[i].ShaderName);
                Assert.Equal(reference[i].Items, expected[i].Cubes);
                Assert.Same(expected[i].Animation, actual[i].Animation);
                Assert.Equal(expected[i].ShaderName, actual[i].ShaderName);
                Assert.Equal(expected[i].Cubes, actual[i].Cubes);
            }
        }
    }

    [Fact]
    public void PlanGeometryBlockBatches_Reused_Across_Frames_Matches_Static_Path()
    {
        var planner = new WorldRenderBatchPlanner(Resolver);
        var random = new Random(4321);

        for (int frame = 0; frame < 40; frame++)
        {
            List<GeometryRenderBlockDescriptor> blocks = CreateBlocks(random, random.Next(0, 300), keyVariety: random.Next(1, 12));

            List<WorldGeometryRenderBatch> expected = WorldRenderBatchPlanner.CreateGeometryBlockBatches(blocks, Resolver);
            IReadOnlyList<WorldGeometryRenderBatch> actual = planner.PlanGeometryBlockBatches(blocks);

            List<(GeometryMaterialId MaterialId, string ShaderName, List<GeometryRenderBlockDescriptor> Items)> reference =
                GroupInFirstSeenOrder(blocks, block => (block.MaterialId, block.ShaderName));

            Assert.Equal(reference.Count, expected.Count);
            Assert.Equal(expected.Count, actual.Count);
            for (int i = 0; i < expected.Count; i++)
            {
                Assert.Same(ResolveTestModel(reference[i].MaterialId), expected[i].Animation);
                Assert.Equal(reference[i].ShaderName, expected[i].ShaderName);
                Assert.Equal(reference[i].Items, expected[i].Blocks);
                Assert.Same(expected[i].Animation, actual[i].Animation);
                Assert.Equal(expected[i].ShaderName, actual[i].ShaderName);
                Assert.Equal(expected[i].Blocks, actual[i].Blocks);
            }
        }
    }

    [Fact]
    public void PlanCubeBatches_Uses_First_Seen_Order_After_A_Frame_With_Other_Keys()
    {
        var planner = new WorldRenderBatchPlanner(Resolver);
        planner.PlanCubeBatches(
        [
            new Cube { MaterialId = GeometryMaterialId.Grass, ShaderName = "cube" },
            new Cube { MaterialId = GeometryMaterialId.Stone, ShaderName = "cube" },
            new Cube { MaterialId = GeometryMaterialId.Dirt, ShaderName = "cube" }
        ]);

        Cube dirt = new() { MaterialId = GeometryMaterialId.Dirt, ShaderName = "cube" };
        Cube grass = new() { MaterialId = GeometryMaterialId.Grass, ShaderName = "cube" };
        IReadOnlyList<WorldRenderBatch> batches = planner.PlanCubeBatches([dirt, grass]);

        Assert.Equal(2, batches.Count);
        Assert.Same(DirtModel, batches[0].Animation);
        Assert.Same(dirt, Assert.Single(batches[0].Cubes));
        Assert.Same(GrassModel, batches[1].Animation);
        Assert.Same(grass, Assert.Single(batches[1].Cubes));
    }

    [Fact]
    public void PlanGeometryBlockBatches_Returns_No_Batches_For_Empty_Frame_After_Full_Frame()
    {
        var planner = new WorldRenderBatchPlanner(Resolver);
        planner.PlanGeometryBlockBatches(CreateBlocks(new Random(7), 50, keyVariety: 6));

        Assert.Empty(planner.PlanGeometryBlockBatches(Array.Empty<GeometryRenderBlockDescriptor>()));
    }

    [Fact]
    public void PlanCubeBatches_Allocates_Nothing_After_First_Frame()
    {
        // Before MEM-5 the static path allocated 26,712 B per frame for 1,000 cubes.
        var planner = new WorldRenderBatchPlanner(Resolver);
        List<Cube> cubes = CreateCubes(new Random(5), 1000, keyVariety: 12);

        AllocationAssert.AtMost(0, () => planner.PlanCubeBatches(cubes));
    }

    [Fact]
    public void PlanGeometryBlockBatches_Allocates_Nothing_After_First_Frame()
    {
        // Before MEM-5 the static path allocated 221,848 B per frame for 1,000 blocks, mostly on
        // the large object heap.
        var planner = new WorldRenderBatchPlanner(Resolver);
        List<GeometryRenderBlockDescriptor> blocks = CreateBlocks(new Random(5), 1000, keyVariety: 12);

        AllocationAssert.AtMost(0, () => planner.PlanGeometryBlockBatches(blocks));
    }

    [Fact]
    public void Planner_Allocates_Nothing_When_Frames_Alternate_Between_Inputs()
    {
        var planner = new WorldRenderBatchPlanner(Resolver);
        List<Cube> manyCubes = CreateCubes(new Random(11), 800, keyVariety: 12);
        List<Cube> fewCubes = CreateCubes(new Random(12), 40, keyVariety: 3);
        List<GeometryRenderBlockDescriptor> manyBlocks = CreateBlocks(new Random(13), 800, keyVariety: 12);
        List<GeometryRenderBlockDescriptor> fewBlocks = CreateBlocks(new Random(14), 40, keyVariety: 3);

        AllocationAssert.AtMost(0, () =>
        {
            planner.PlanCubeBatches(manyCubes);
            planner.PlanGeometryBlockBatches(manyBlocks);
            planner.PlanCubeBatches(fewCubes);
            planner.PlanGeometryBlockBatches(fewBlocks);
        });
    }

    // An independent grouping by linear search, so the test does not trust either planner path.
    private static List<(GeometryMaterialId MaterialId, string ShaderName, List<T> Items)> GroupInFirstSeenOrder<T>(
        IReadOnlyList<T> items,
        Func<T, (GeometryMaterialId MaterialId, string ShaderName)> keyOf)
    {
        List<(GeometryMaterialId MaterialId, string ShaderName, List<T> Items)> groups = [];
        foreach (T item in items)
        {
            (GeometryMaterialId materialId, string shaderName) = keyOf(item);
            int index = groups.FindIndex(group => group.MaterialId == materialId && group.ShaderName == shaderName);
            if (index < 0)
            {
                groups.Add((materialId, shaderName, [item]));
                continue;
            }

            groups[index].Items.Add(item);
        }

        return groups;
    }

    private static List<Cube> CreateCubes(Random random, int count, int keyVariety)
    {
        List<Cube> cubes = new(count);
        for (int i = 0; i < count; i++)
        {
            int key = random.Next(keyVariety);
            cubes.Add(new Cube
            {
                MaterialId = Materials[key % Materials.Length],
                ShaderName = Shaders[(key / Materials.Length) % Shaders.Length],
                Position = new Vector3(i, 0f, 0f)
            });
        }

        return cubes;
    }

    private static List<GeometryRenderBlockDescriptor> CreateBlocks(Random random, int count, int keyVariety)
    {
        List<GeometryRenderBlockDescriptor> blocks = new(count);
        for (int i = 0; i < count; i++)
        {
            int key = random.Next(keyVariety);
            blocks.Add(new GeometryRenderBlockDescriptor(
                new GeometryCellKey(i, 1, 1),
                Center: new Vector3(i, 0f, 0f),
                SideLength: 0.25f,
                Materials[key % Materials.Length],
                Shaders[(key / Materials.Length) % Shaders.Length]));
        }

        return blocks;
    }

    private static AnimatedModel? ResolveTestModel(GeometryMaterialId materialId)
    {
        return materialId switch
        {
            GeometryMaterialId.Grass => GrassModel,
            GeometryMaterialId.Stone => StoneModel,
            GeometryMaterialId.Dirt => DirtModel,
            _ => null
        };
    }

    private static AnimatedModel CreateModel()
    {
#pragma warning disable SYSLIB0050
        return (AnimatedModel)FormatterServices.GetUninitializedObject(typeof(AnimatedModel));
#pragma warning restore SYSLIB0050
    }
}
