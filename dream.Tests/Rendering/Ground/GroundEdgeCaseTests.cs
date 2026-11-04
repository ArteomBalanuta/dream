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

    [Fact]
    public void ADocument_WithAnotherSchema_OrNone_IsSkippedWithAFault()
    {
        GroundTestSets.Faults faults = new();
        string entry = GroundTestSets.Entry("flora.a", "grass", "cover", 0.2f, 0.1f, 0.2f, ["grass"]);
        string v2 = GroundTestSets.Manifest(entry).Replace("dream.ground/1", "dream.ground/2");
        string none = $$"""{ "assets": [ {{entry}} ] }""";
        string good = GroundTestSets.Manifest(GroundTestSets.Entry("stone.rock", "rock", "accent", 0.8f, 0.5f, 0.8f, ["stone"]));

        GroundAssetSet set = GroundAssetSet.Parse([("v2.json", v2), ("none.json", none), ("good.json", good)], GroundTestSets.Dir, faults);

        Assert.Equal(["stone.rock"], set.Assets.Select(a => a.Id));
        Assert.Equal(2, faults.Lines.Count);
        Assert.All(faults.Lines, l => Assert.Contains("schema", l));
    }

    [Fact]
    public void AColumn_WithAMaterialBeyondTheEnum_IsSkipped_NotIndexedOutOfRange()
    {
        StaticGround ground = new();
        for (int z = 0; z < 30; z++)
        {
            for (int x = 0; x < 30; x++)
            {
                ground.Add(x, 0, z, true, x >= 10 && x < 20 ? (GeometryMaterialId)99 : GeometryMaterialId.Grass);
            }
        }

        ground.Seal();

        List<GroundPlacement> placed = GroundTestSets.Layout(ground, [], GroundTestSets.Synthetic());

        Assert.NotEmpty(placed);
        Assert.All(placed, p => Assert.True(p.Position.X < 10f || p.Position.X >= 20f, $"{p} stands on the unknown material"));
    }

    [Fact]
    public void ADecal_NeverHangsOverAStep()
    {
        // Grass at level 0 on the west, a one-cell step up at x >= 20: any decal whose disc reaches the step is refused.
        StaticGround ground = new();
        for (int z = 0; z < 40; z++)
        {
            for (int x = 0; x < 40; x++)
            {
                ground.Add(x, x >= 20 ? 1 : 0, z, true, GeometryMaterialId.Grass);
            }
        }

        ground.Seal();
        GroundAssetSet assets = GroundAssetSet.Parse(
            [("d.json", GroundTestSets.Manifest(GroundTestSets.Entry("decals.dirt", "decal.dirt", "decal", 0.9f, 0f, 0f, ["grass"], scaleMin: 1f, scaleMax: 1f)))],
            GroundTestSets.Dir,
            new GroundTestSets.Faults());

        List<GroundPlacement> placed = GroundTestSets.Layout(ground, [], assets);

        Assert.NotEmpty(placed);
        foreach (GroundPlacement p in placed)
        {
            bool west = p.Position.X < 20f;
            float edgeDistance = MathF.Abs(p.Position.X - 20f);
            Assert.True(edgeDistance >= 0.9f, $"{p} is within its radius of the step");
            Assert.Equal(west ? 1f : 2f, MathF.Floor(p.Position.Y + 0.5f));
        }
    }

    [Fact]
    public void CoverAndDecals_KeepOffTheCellsUnderAnAccentOrLandmark()
    {
        (PropIndex props, StaticGround ground) = GroundTestSets.World(WorldDefaults.ServerNewWorldSize);
        GroundAssetSet assets = GroundAssetSet.LoadShipped();

        List<GroundPlacement> placed = GroundTestSets.Layout(ground, props.Placements, assets);

        List<GroundPlacement> big = placed.Where(p => assets.Assets[p.Asset].Layer >= GroundLayer.Accent).ToList();
        Assert.NotEmpty(big);
        foreach (GroundPlacement small in placed.Where(p => assets.Assets[p.Asset].Layer <= GroundLayer.Cover))
        {
            foreach (GroundPlacement b in big)
            {
                float radius = assets.Assets[b.Asset].Radius * b.Scale;
                float dx = (MathF.Floor(small.Position.X) + 0.5f) - b.Position.X;
                float dz = (MathF.Floor(small.Position.Z) + 0.5f) - b.Position.Z;
                Assert.False((dx * dx) + (dz * dz) <= radius * radius, $"{small} sits under {b}");
            }
        }
    }

    [Fact]
    public void EveryShippedAsset_IsPlacedAtLeastOnce_OnTheHundredWorld()
    {
        GroundAssetSet assets = GroundAssetSet.LoadShipped();
        (PropIndex props, StaticGround ground) = GroundTestSets.World(WorldDefaults.ServerNewWorldSize);

        List<GroundPlacement> placed = GroundTestSets.Layout(ground, props.Placements, assets);

        HashSet<int> seen = placed.Select(p => p.Asset).ToHashSet();
        string[] missing = assets.Assets.Where((_, i) => !seen.Contains(i)).Select(a => $"{a.Id} ({a.Layer}, {string.Join('/', a.Ground)})").ToArray();
        Assert.True(missing.Length == 0, $"{missing.Length} of {assets.Assets.Count} never placed: {string.Join("; ", missing)}");
    }
}
