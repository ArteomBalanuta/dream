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
}
