using System.Runtime.Serialization;
using Xunit;
using Dream.Rendering.Models;
using Dream.Rendering.World;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;

namespace Dream.Tests.Rendering.World;


public class WorldRenderBatchPlannerTests
{
    private static readonly AnimatedModel GrassModel = CreateModel();
    private static readonly AnimatedModel StoneModel = CreateModel();
    private static readonly AnimatedModel DirtModel = CreateModel();

    [Fact]
    public void CreateBatches_Groups_Cubes_With_Same_Material_And_Shader()
    {
        var cubes = new[]
        {
            new Cube { MaterialId = GeometryMaterialId.Grass, ShaderName = "cube" },
            new Cube { MaterialId = GeometryMaterialId.Grass, ShaderName = "cube" }
        };

        List<WorldRenderBatch> batches = WorldRenderBatchPlanner.CreateBatches(cubes, ResolveTestModel);

        WorldRenderBatch batch = Assert.Single(batches);
        Assert.Same(GrassModel, batch.Animation);
        Assert.Equal("cube", batch.ShaderName);
        Assert.Equal(2, batch.Cubes.Count);
    }

    [Fact]
    public void CreateBatches_Splits_Different_Shader_Groups()
    {
        var cubes = new[]
        {
            new Cube { MaterialId = GeometryMaterialId.Grass, ShaderName = "default" },
            new Cube { MaterialId = GeometryMaterialId.Grass, ShaderName = "cube" }
        };

        List<WorldRenderBatch> batches = WorldRenderBatchPlanner.CreateBatches(cubes, ResolveTestModel);

        Assert.Equal(2, batches.Count);
    }

    [Fact]
    public void CreateBatches_Splits_Different_Materials()
    {
        var cubes = new[]
        {
            new Cube { MaterialId = GeometryMaterialId.Grass, ShaderName = "cube" },
            new Cube { MaterialId = GeometryMaterialId.Stone, ShaderName = "cube" }
        };

        List<WorldRenderBatch> batches = WorldRenderBatchPlanner.CreateBatches(cubes, ResolveTestModel);

        Assert.Equal(2, batches.Count);
        Assert.Same(GrassModel, batches[0].Animation);
        Assert.Same(StoneModel, batches[1].Animation);
    }

    [Fact]
    public void CreateBatches_Keeps_First_Seen_Order_And_Resolves_Each_Group_Once()
    {
        var resolved = new List<GeometryMaterialId>();
        AnimatedModel? CountingResolver(GeometryMaterialId materialId)
        {
            resolved.Add(materialId);
            return ResolveTestModel(materialId);
        }

        Cube firstStone = new() { MaterialId = GeometryMaterialId.Stone, ShaderName = "cube" };
        Cube firstDirt = new() { MaterialId = GeometryMaterialId.Dirt, ShaderName = "cube" };
        Cube secondStone = new() { MaterialId = GeometryMaterialId.Stone, ShaderName = "cube" };
        Cube grass = new() { MaterialId = GeometryMaterialId.Grass, ShaderName = "cube" };
        Cube secondDirt = new() { MaterialId = GeometryMaterialId.Dirt, ShaderName = "cube" };

        List<WorldRenderBatch> batches = WorldRenderBatchPlanner.CreateBatches(
            new[] { firstStone, firstDirt, secondStone, grass, secondDirt },
            CountingResolver);

        Assert.Equal(
            new[] { GeometryMaterialId.Stone, GeometryMaterialId.Dirt, GeometryMaterialId.Grass },
            resolved);
        Assert.Equal(3, batches.Count);
        Assert.Same(StoneModel, batches[0].Animation);
        Assert.Equal(new[] { firstStone, secondStone }, batches[0].Cubes);
        Assert.Same(DirtModel, batches[1].Animation);
        Assert.Equal(new[] { firstDirt, secondDirt }, batches[1].Cubes);
        Assert.Same(GrassModel, batches[2].Animation);
        Assert.Equal(new[] { grass }, batches[2].Cubes);
    }

    [Fact]
    public void CreateBatches_Unknown_Material_Gets_Null_Model()
    {
        var cubes = new[]
        {
            new Cube { MaterialId = GeometryMaterialId.Sand, ShaderName = "cube" }
        };

        List<WorldRenderBatch> batches = WorldRenderBatchPlanner.CreateBatches(cubes, ResolveTestModel);

        WorldRenderBatch batch = Assert.Single(batches);
        Assert.Null(batch.Animation);
        Assert.Single(batch.Cubes);
    }

    [Fact]
    public void CreateBatches_From_GeometryRenderExport_Uses_Dynamic_Compatibility_Cubes()
    {
        var export = new GeometryRenderExport(
            Array.Empty<GeometryCellKey>(),
            Array.Empty<GeometryRenderCell>(),
            Array.Empty<GeometryRenderBlockDescriptor>(),
            Array.Empty<Cube>(),
            new[]
            {
                new Cube { MaterialId = GeometryMaterialId.Grass, ShaderName = "default" },
                new Cube { MaterialId = GeometryMaterialId.Grass, ShaderName = "cube" }
            });

        List<WorldRenderBatch> batches = WorldRenderBatchPlanner.CreateBatches(export, ResolveTestModel);

        Assert.Equal(2, batches.Count);
    }

    [Fact]
    public void CreateGeometryBlockBatches_Groups_Blocks_With_Same_Material_And_Shader()
    {
        var blocks = new[]
        {
            CreateBlock(GeometryMaterialId.Stone, "cube"),
            CreateBlock(GeometryMaterialId.Stone, "cube")
        };

        List<WorldGeometryRenderBatch> batches = WorldRenderBatchPlanner.CreateGeometryBlockBatches(blocks, ResolveTestModel);

        WorldGeometryRenderBatch batch = Assert.Single(batches);
        Assert.Same(StoneModel, batch.Animation);
        Assert.Equal("cube", batch.ShaderName);
        Assert.Equal(2, batch.Blocks.Count);
    }

    [Fact]
    public void CreateGeometryBlockBatches_Splits_Different_Materials_In_First_Seen_Order()
    {
        var blocks = new[]
        {
            CreateBlock(GeometryMaterialId.Dirt, "cube"),
            CreateBlock(GeometryMaterialId.Stone, "cube"),
            CreateBlock(GeometryMaterialId.Dirt, "cube")
        };

        List<WorldGeometryRenderBatch> batches = WorldRenderBatchPlanner.CreateGeometryBlockBatches(blocks, ResolveTestModel);

        Assert.Equal(2, batches.Count);
        Assert.Same(DirtModel, batches[0].Animation);
        Assert.Equal(2, batches[0].Blocks.Count);
        Assert.Same(StoneModel, batches[1].Animation);
        Assert.Single(batches[1].Blocks);
    }

    [Fact]
    public void CreateGeometryBlockBatches_Splits_Different_Shaders()
    {
        var blocks = new[]
        {
            CreateBlock(GeometryMaterialId.Stone, "cube"),
            CreateBlock(GeometryMaterialId.Stone, "default")
        };

        List<WorldGeometryRenderBatch> batches = WorldRenderBatchPlanner.CreateGeometryBlockBatches(blocks, ResolveTestModel);

        Assert.Equal(2, batches.Count);
        Assert.Equal("cube", batches[0].ShaderName);
        Assert.Equal("default", batches[1].ShaderName);
    }

    [Fact]
    public void CreateGeometryBlockBatches_Unknown_Material_Gets_Null_Model()
    {
        var blocks = new[]
        {
            CreateBlock(GeometryMaterialId.Sand, "cube")
        };

        List<WorldGeometryRenderBatch> batches = WorldRenderBatchPlanner.CreateGeometryBlockBatches(blocks, ResolveTestModel);

        WorldGeometryRenderBatch batch = Assert.Single(batches);
        Assert.Null(batch.Animation);
    }

    private static GeometryRenderBlockDescriptor CreateBlock(GeometryMaterialId materialId, string shaderName)
    {
        return new GeometryRenderBlockDescriptor(
            new GeometryCellKey(1, 1, 1),
            Center: default,
            SideLength: 0.25f,
            materialId,
            shaderName);
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
