using Dream.Rendering.Ground;
using Dream.Rendering.Props;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using Dream.Tests.Utilities;
using Xunit;

namespace Dream.Tests.Rendering.Ground;

// Gaps left by the main Ground suites: ids repeated across manifest families, inverted scale ranges, a world that
// matches no asset, the shipped set on two independent maps, and the steady-state cost of the scatter index.
public class GroundEdgeCaseTests
{
    [Fact]
    public void AnIdRepeatedInAnotherFamilysManifest_KeepsTheFirstAndReportsTheSecond()
    {
        GroundTestSets.Faults faults = new();
        string a = GroundTestSets.Manifest(GroundTestSets.Entry("shared.thing", "grass", "cover", 0.2f, 0.1f, 0.2f, ["grass"]));
        string b = GroundTestSets.Manifest(
            GroundTestSets.Entry("shared.thing", "rock", "accent", 0.8f, 0.5f, 0.8f, ["stone"]),
            GroundTestSets.Entry("stone.other", "rock", "accent", 0.8f, 0.5f, 0.8f, ["stone"]));

        GroundAssetSet set = GroundAssetSet.Parse([("flora.json", a), ("stone.json", b)], GroundTestSets.Dir, faults);

        Assert.Equal(["shared.thing", "stone.other"], set.Assets.Select(x => x.Id));
        Assert.Equal(GroundAssetClass.Grass, set.Assets[0].Class);
        Assert.Single(faults.Lines);
        Assert.Contains("repeated id", faults.Lines[0]);
    }

    [Theory]
    [InlineData(1.5f, 0.5f)]
    [InlineData(0f, 1f)]
    [InlineData(-1f, 1f)]
    public void AnInvertedOrNonPositiveScaleRange_IsSkippedAndReported(float min, float max)
    {
        GroundTestSets.Faults faults = new();
        string json = GroundTestSets.Manifest(
            GroundTestSets.Entry("flora.bad", "grass", "cover", 0.2f, 0.1f, 0.2f, ["grass"], scaleMin: min, scaleMax: max),
            GroundTestSets.Entry("flora.good", "grass", "cover", 0.2f, 0.1f, 0.2f, ["grass"], scaleMin: 1f, scaleMax: 1f));

        GroundAssetSet set = GroundAssetSet.Parse([("flora.json", json)], GroundTestSets.Dir, faults);

        Assert.Equal(["flora.good"], set.Assets.Select(x => x.Id));
        Assert.Single(faults.Lines);
        Assert.Contains("bad scale", faults.Lines[0]);
    }

    [Fact]
    public void AnAssetWhoseGroundMatchesNothingInTheWorld_PlacesNothing()
    {
        StaticGround ground = GroundTestSets.Flat(40, GeometryMaterialId.Sand);
        GroundAssetSet snowOnly = GroundAssetSet.Parse(
            [("t.json", GroundTestSets.Manifest(
                GroundTestSets.Entry("flora.snowgrass", "grass", "cover", 0.2f, 0.1f, 0.2f, ["snow"]),
                GroundTestSets.Entry("stone.snowrock", "rock", "accent", 0.8f, 0.5f, 0.8f, ["snow"]),
                GroundTestSets.Entry("flora.snowtree", "tree", "landmark", 1.6f, 0.45f, 4.2f, ["snow"])))],
            GroundTestSets.Dir, new GroundTestSets.Faults());
        Assert.Equal(3, snowOnly.Assets.Count);

        List<GroundPlacement> placed = GroundTestSets.Layout(ground, [], snowOnly);

        Assert.Empty(placed);
        GroundScatterIndex index = new();
        index.Rebuild(ground, [], snowOnly);
        Assert.Empty(index.Placements);
        Assert.Empty(index.Blocks);
    }

    [Fact]
    public void TheShippedSet_IsIdenticalOnTwoSeparatelyBuiltMaps_AndStaysTwoCellsOffTheStartupEdge()
    {
        GroundAssetSet assets = GroundAssetSet.LoadShipped();
        (PropIndex propsA, StaticGround groundA) = GroundTestSets.World(WorldDefaults.StartupFloorSize);
        (PropIndex propsB, StaticGround groundB) = GroundTestSets.World(WorldDefaults.StartupFloorSize);

        List<GroundPlacement> a = GroundTestSets.Layout(groundA, propsA.Placements, assets);
        List<GroundPlacement> b = GroundTestSets.Layout(groundB, propsB.Placements, assets);

        Assert.NotEmpty(a);
        Assert.True(a.SequenceEqual(b));
        foreach (GroundPlacement p in a)
        {
            Assert.True(p.Position.X >= GroundScatterLayout.EdgeCells && p.Position.Z >= GroundScatterLayout.EdgeCells, $"{p} is within 2 cells of the low edge");
            Assert.True(p.Position.X < groundA.MaxX - GroundScatterLayout.EdgeCells + 1 && p.Position.Z < groundA.MaxZ - GroundScatterLayout.EdgeCells + 1, $"{p} is within 2 cells of the high edge");
        }
    }

    [Fact]
    public void ScatterIndexUpdate_WithTheShippedSet_AllocatesNothingInSteadyState()
    {
        GroundAssetSet assets = GroundAssetSet.LoadShipped();
        (PropIndex props, _) = GroundTestSets.World(WorldDefaults.StartupFloorSize);
        GroundScatterIndex index = new();
        index.Update(props, assets);
        Assert.NotEmpty(index.Placements);
        int rebuilds = index.RebuildCount;

        AllocationAssert.AtMost(0, () => index.Update(props, assets));

        Assert.Equal(rebuilds, index.RebuildCount);
    }
}
