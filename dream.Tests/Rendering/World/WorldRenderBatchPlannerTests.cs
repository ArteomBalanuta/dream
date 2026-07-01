using System.Runtime.Serialization;
using Xunit;
using Dream.Rendering.Models;
using Dream.Rendering.World;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;

namespace Dream.Tests.Rendering.World;


public class WorldRenderBatchPlannerTests
{
    [Fact]
    public void CreateBatches_Groups_Cubes_With_Same_Shader_And_Model()
    {
#pragma warning disable SYSLIB0050
        var sharedModel = (AnimatedModel)FormatterServices.GetUninitializedObject(typeof(AnimatedModel));
#pragma warning restore SYSLIB0050

        var cubes = new[]
        {
            new Cube { Animation = sharedModel, ShaderName = "cube" },
            new Cube { Animation = sharedModel, ShaderName = "cube" }
        };

        List<WorldRenderBatch> batches = WorldRenderBatchPlanner.CreateBatches(cubes);

        Assert.Single(batches);
        Assert.Equal(2, batches[0].Cubes.Count);
    }

    [Fact]
    public void CreateBatches_Splits_Different_Shader_Groups()
    {
        var cubes = new[]
        {
            new Cube { ShaderName = "default" },
            new Cube { ShaderName = "cube" }
        };

        List<WorldRenderBatch> batches = WorldRenderBatchPlanner.CreateBatches(cubes);

        Assert.Equal(2, batches.Count);
    }

    [Fact]
    public void CreateBatches_Splits_Different_Model_Instances()
    {
#pragma warning disable SYSLIB0050
        var firstModel = (AnimatedModel)FormatterServices.GetUninitializedObject(typeof(AnimatedModel));
        var secondModel = (AnimatedModel)FormatterServices.GetUninitializedObject(typeof(AnimatedModel));
#pragma warning restore SYSLIB0050

        var cubes = new[]
        {
            new Cube { Animation = firstModel, ShaderName = "cube" },
            new Cube { Animation = secondModel, ShaderName = "cube" }
        };

        List<WorldRenderBatch> batches = WorldRenderBatchPlanner.CreateBatches(cubes);

        Assert.Equal(2, batches.Count);
    }

    [Fact]
    public void CreateBatches_From_GeometryRenderExport_Uses_Compatibility_Cubes()
    {
        var export = new GeometryRenderExport(
            Array.Empty<GeometryCellKey>(),
            Array.Empty<GeometryRenderCell>(),
            Array.Empty<GeometryRenderBlockDescriptor>(),
            new[]
            {
                new Cube { ShaderName = "default" },
                new Cube { ShaderName = "cube" }
            });

        List<WorldRenderBatch> batches = WorldRenderBatchPlanner.CreateBatches(export);

        Assert.Equal(2, batches.Count);
    }

    [Fact]
    public void CreateGeometryBlockBatches_Groups_Blocks_With_Same_Shader_And_Model()
    {
#pragma warning disable SYSLIB0050
        var sharedModel = (AnimatedModel)FormatterServices.GetUninitializedObject(typeof(AnimatedModel));
#pragma warning restore SYSLIB0050

        var blocks = new[]
        {
            new GeometryRenderBlockDescriptor(
                new GeometryCellKey(1, 1, 1),
                Center: default,
                SideLength: 0.25f,
                GeometryMaterialId.Stone,
                sharedModel,
                "cube"),
            new GeometryRenderBlockDescriptor(
                new GeometryCellKey(1, 1, 1),
                Center: default,
                SideLength: 0.25f,
                GeometryMaterialId.Stone,
                sharedModel,
                "cube")
        };

        List<WorldGeometryRenderBatch> batches = WorldRenderBatchPlanner.CreateGeometryBlockBatches(blocks);

        WorldGeometryRenderBatch batch = Assert.Single(batches);
        Assert.Equal(sharedModel, batch.Animation);
        Assert.Equal("cube", batch.ShaderName);
        Assert.Equal(2, batch.Blocks.Count);
    }

    [Fact]
    public void CreateGeometryBlockBatches_Splits_Different_Materials()
    {
#pragma warning disable SYSLIB0050
        var firstModel = (AnimatedModel)FormatterServices.GetUninitializedObject(typeof(AnimatedModel));
        var secondModel = (AnimatedModel)FormatterServices.GetUninitializedObject(typeof(AnimatedModel));
#pragma warning restore SYSLIB0050

        var blocks = new[]
        {
            new GeometryRenderBlockDescriptor(
                new GeometryCellKey(1, 1, 1),
                Center: default,
                SideLength: 0.25f,
                GeometryMaterialId.Stone,
                firstModel,
                "cube"),
            new GeometryRenderBlockDescriptor(
                new GeometryCellKey(1, 1, 1),
                Center: default,
                SideLength: 0.25f,
                GeometryMaterialId.Dirt,
                secondModel,
                "cube")
        };

        List<WorldGeometryRenderBatch> batches = WorldRenderBatchPlanner.CreateGeometryBlockBatches(blocks);

        Assert.Equal(2, batches.Count);
    }
}
