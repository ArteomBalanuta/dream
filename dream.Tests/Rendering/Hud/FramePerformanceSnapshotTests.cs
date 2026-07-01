using Xunit;
using Dream.Rendering.Hud;

namespace Dream.Tests.Rendering.Hud;

public class FramePerformanceSnapshotTests
{
    [Fact]
    public void HasCounters_Returns_False_For_Empty_Snapshot()
    {
        Assert.False(FramePerformanceSnapshot.Empty.HasCounters);
    }

    [Fact]
    public void ToHudLines_Formats_Render_And_Physics_Counters()
    {
        FramePerformanceSnapshot snapshot = new(
            RenderExportMilliseconds: 1.25,
            PhysicsStepMilliseconds: 0.75,
            ExportedCompatibilityCubes: 300,
            ExportedGeometryBlocks: 600,
            VisibleCompatibilityCubes: 120,
            VisibleGeometryBlocks: 240,
            CompatibilityBatchCount: 2,
            GeometryBatchCount: 3,
            CollisionBodyCount: 900);

        string[] lines = snapshot.ToHudLines().ToArray();

        Assert.Contains("Export: 1.25 ms", lines);
        Assert.Contains("Physics: 0.75 ms", lines);
        Assert.Contains("Visible: cubes 120/300, blocks 240/600", lines);
        Assert.Contains("Batches: cubes 2, blocks 3", lines);
        Assert.Contains("Collision Bodies: 900", lines);
    }
}
