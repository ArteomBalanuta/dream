using Dream.Rendering.Ground;
using Dream.Rendering.Props;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using OpenTK.Mathematics;
using Xunit;
using Xunit.Abstractions;

namespace Dream.Tests.Rendering.Ground;

// The ground scatter is a pure function of the replicated static ground, the props and the asset set. These tests
// run it on the real default terrain at both world sizes the game uses, with a synthetic set (one asset per class).
public class GroundScatterLayoutTests
{
    private readonly ITestOutputHelper _output;

    public GroundScatterLayoutTests(ITestOutputHelper output)
    {
        _output = output;
    }

    public static IEnumerable<object[]> WorldSizes() => [[WorldDefaults.StartupFloorSize], [WorldDefaults.ServerNewWorldSize]];

    private static (StaticGround Ground, IReadOnlyList<PropPlacement> Props, GroundAssetSet Assets, List<GroundPlacement> Placed) Build(int size)
    {
        (PropIndex props, StaticGround ground) = GroundTestSets.World(size);
        GroundAssetSet assets = GroundTestSets.Synthetic();
        return (ground, props.Placements, assets, GroundTestSets.Layout(ground, props.Placements, assets));
    }

    private static GroundAsset AssetOf(GroundAssetSet set, GroundPlacement p) => set.Assets[p.Asset];

    private static StaticGround.Column ColumnAt(StaticGround ground, GroundPlacement p)
    {
        Assert.True(ground.TryGet((int)MathF.Floor(p.Position.X), (int)MathF.Floor(p.Position.Z), out StaticGround.Column column));
        return column;
    }

    [Theory]
    [MemberData(nameof(WorldSizes))]
    public void TheLayout_IsDeterministic_AndSortedByAssetThenPosition(int size)
    {
        var (_, _, _, first) = Build(size);
        var (_, _, _, second) = Build(size);

        Assert.NotEmpty(first);
        Assert.Equal(first, second);
        for (int i = 1; i < first.Count; i++)
        {
            GroundPlacement a = first[i - 1];
            GroundPlacement b = first[i];
            bool ordered = a.Asset < b.Asset ||
                (a.Asset == b.Asset && (a.Position.X < b.Position.X || (a.Position.X == b.Position.X && a.Position.Z <= b.Position.Z)));
            Assert.True(ordered, $"placement {i} is out of order");
        }
    }

    [Theory]
    [MemberData(nameof(WorldSizes))]
    public void NothingStands_OnMissingBlockedOrLampColumns_OrWithinTwoCellsOfTheEdge(int size)
    {
        var (ground, _, _, placed) = Build(size);

        foreach (GroundPlacement p in placed)
        {
            StaticGround.Column column = ColumnAt(ground, p);
            Assert.False(column.Blocked);
            Assert.NotEqual(GeometryMaterialId.Lamp, column.Material);
            Assert.True(p.Position.X >= GroundScatterLayout.EdgeCells && p.Position.Z >= GroundScatterLayout.EdgeCells, $"{p} is within 2 cells of the low edge");
            Assert.True(p.Position.X < ground.MaxX - GroundScatterLayout.EdgeCells + 1 && p.Position.Z < ground.MaxZ - GroundScatterLayout.EdgeCells + 1, $"{p} is within 2 cells of the high edge");
        }
    }

    [Theory]
    [MemberData(nameof(WorldSizes))]
    public void EveryPlacedAsset_StandsOnAMaterial_ItsManifestAllows(int size)
    {
        var (ground, _, assets, placed) = Build(size);

        foreach (GroundPlacement p in placed)
        {
            GroundAsset asset = AssetOf(assets, p);
            Assert.Contains(ColumnAt(ground, p).Material, asset.Ground);
        }
    }

    [Theory]
    [MemberData(nameof(WorldSizes))]
    public void AccentsLandmarksAndCover_KeepOffTheRoadMaterial_AndBigPiecesKeepTheRoadsClear(int size)
    {
        var (ground, _, assets, placed) = Build(size);
        float width = ground.MaxX + 1;
        float depth = ground.MaxZ + 1;

        foreach (GroundPlacement p in placed)
        {
            GroundAsset asset = AssetOf(assets, p);
            if (asset.Layer == GroundLayer.Decal)
            {
                continue;
            }

            Assert.NotEqual(GeometryMaterialId.GrassPath, ColumnAt(ground, p).Material);
            if (asset.Layer == GroundLayer.Cover)
            {
                continue;
            }

            float x = p.Position.X;
            float z = p.Position.Z;
            Assert.True(MathF.Abs(x - 5.5f) >= PropLayout.RoadClearance && MathF.Abs(z - 5.5f) >= PropLayout.RoadClearance, $"{asset.Id} at {x}, {z} is on a spawn lane");
            Assert.True(MathF.Abs(x - ((int)width / 2)) >= PropLayout.RoadClearance && MathF.Abs(z - ((int)depth / 2)) >= PropLayout.RoadClearance, $"{asset.Id} at {x}, {z} is on the central cross");
        }
    }

    [Theory]
    [MemberData(nameof(WorldSizes))]
    public void Landmarks_StayOutOfTheAuthoredZone_AndAwayFromTheSpawn(int size)
    {
        var (_, _, assets, placed) = Build(size);

        foreach (GroundPlacement p in placed.Where(p => AssetOf(assets, p).Layer == GroundLayer.Landmark))
        {
            Assert.False(p.Position.X < PropLayout.AuthoredZoneX && p.Position.Z < PropLayout.AuthoredZoneZ, $"landmark at {p.Position} is in the authored zone");
            Assert.True(new Vector2(p.Position.X - 5f, p.Position.Z - 5f).Length >= GroundScatterLayout.SpawnClearance);
        }
    }

    [Theory]
    [MemberData(nameof(WorldSizes))]
    public void NothingOverlaps_AProp_Footprint(int size)
    {
        var (_, props, _, placed) = Build(size);

        foreach (GroundPlacement p in placed)
        {
            foreach (PropPlacement prop in props)
            {
                PropFootprint fp = PropCatalog.Footprint(prop.Kind);
                float yaw = MathHelper.DegreesToRadians(prop.YawDegrees);
                float dx = p.Position.X - prop.Position.X;
                float dz = p.Position.Z - prop.Position.Z;
                float localX = (dx * MathF.Cos(yaw)) - (dz * MathF.Sin(yaw));
                float localZ = (dx * MathF.Sin(yaw)) + (dz * MathF.Cos(yaw));
                bool inside = MathF.Abs(localX) <= (fp.HalfWidth * prop.Scale) + GroundScatterLayout.PropMargin &&
                    MathF.Abs(localZ) <= (fp.HalfDepth * prop.Scale) + GroundScatterLayout.PropMargin;
                Assert.False(inside, $"{p.Position} is inside the footprint of {prop.Kind} at {prop.Position}");
            }
        }
    }

    [Theory]
    [MemberData(nameof(WorldSizes))]
    public void AccentsAndLandmarks_KeepTheirRadiiApart_FromEachOther_AndFromTheProps(int size)
    {
        var (_, props, assets, placed) = Build(size);
        List<GroundPlacement> big = placed.Where(p => AssetOf(assets, p).Layer >= GroundLayer.Accent).ToList();

        Assert.NotEmpty(big);
        for (int i = 0; i < big.Count; i++)
        {
            float ri = AssetOf(assets, big[i]).Radius * big[i].Scale;
            for (int j = i + 1; j < big.Count; j++)
            {
                float reach = ri + (AssetOf(assets, big[j]).Radius * big[j].Scale);
                Assert.True(new Vector2(big[i].Position.X - big[j].Position.X, big[i].Position.Z - big[j].Position.Z).Length >= reach - 1e-3f, $"{big[i].Position} and {big[j].Position} overlap");
            }

            foreach (PropPlacement prop in props)
            {
                float reach = ri + (PropCatalog.Bounds(prop.Kind).HorizontalRadius * prop.Scale);
                Assert.True(new Vector2(big[i].Position.X - prop.Position.X, big[i].Position.Z - prop.Position.Z).Length >= reach - 1e-3f, $"{big[i].Position} is too close to {prop.Kind}");
            }
        }
    }

    [Theory]
    [MemberData(nameof(WorldSizes))]
    public void Decals_LieOnLevelGround_JustAboveTheFloor_AndDoNotOverlapEachOther(int size)
    {
        var (ground, _, assets, placed) = Build(size);
        List<GroundPlacement> decals = placed.Where(p => AssetOf(assets, p).Layer == GroundLayer.Decal).ToList();

        Assert.NotEmpty(decals);
        for (int i = 0; i < decals.Count; i++)
        {
            GroundPlacement d = decals[i];
            float top = ColumnAt(ground, d).TopY + 1f;
            float lift = d.Position.Y - top;
            Assert.True(MathF.Abs(lift - GroundScatterLayout.DecalLiftA) < 1e-4f || MathF.Abs(lift - GroundScatterLayout.DecalLiftB) < 1e-4f, $"decal lift {lift}");

            // Level under its whole footprint: every column it covers has the same top.
            float r = AssetOf(assets, d).Radius * d.Scale;
            for (int z = (int)MathF.Floor(d.Position.Z - r); z <= (int)MathF.Floor(d.Position.Z + r); z++)
            {
                for (int x = (int)MathF.Floor(d.Position.X - r); x <= (int)MathF.Floor(d.Position.X + r); x++)
                {
                    Assert.True(ground.TryGet(x, z, out StaticGround.Column c) && c.TopY + 1f == top, $"decal at {d.Position} is not level at {x}, {z}");
                }
            }

            for (int j = i + 1; j < decals.Count; j++)
            {
                float reach = r + (AssetOf(assets, decals[j]).Radius * decals[j].Scale);
                Assert.True(new Vector2(d.Position.X - decals[j].Position.X, d.Position.Z - decals[j].Position.Z).Length >= reach - 1e-3f, $"decals at {d.Position} and {decals[j].Position} overlap");
            }
        }
    }

    [Theory]
    [MemberData(nameof(WorldSizes))]
    public void Cover_StandsOnTheTopFaceOfItsColumn(int size)
    {
        var (ground, _, assets, placed) = Build(size);

        foreach (GroundPlacement p in placed.Where(p => AssetOf(assets, p).Layer == GroundLayer.Cover))
        {
            Assert.Equal(ColumnAt(ground, p).TopY + 1f, p.Position.Y);
        }
    }

    [Theory]
    [MemberData(nameof(WorldSizes))]
    public void Scale_StaysInsideTheManifestRange(int size)
    {
        var (_, _, assets, placed) = Build(size);

        foreach (GroundPlacement p in placed)
        {
            GroundAsset asset = AssetOf(assets, p);
            Assert.InRange(p.Scale, asset.ScaleMin, asset.ScaleMax);
            Assert.InRange(p.YawDegrees, 0f, 360f);
        }
    }

    [Fact]
    public void PerLayerCounts_OnTheHundredWorld_StayInTheirBands()
    {
        var (_, _, assets, placed) = Build(WorldDefaults.ServerNewWorldSize);
        int[] counts = new int[4];
        foreach (GroundPlacement p in placed)
        {
            counts[(int)AssetOf(assets, p).Layer]++;
        }

        _output.WriteLine($"decal {counts[0]} cover {counts[1]} accent {counts[2]} landmark {counts[3]}");
        Assert.InRange(counts[(int)GroundLayer.Decal], 400, 1100);
        Assert.InRange(counts[(int)GroundLayer.Cover], 1500, 6000);
        Assert.InRange(counts[(int)GroundLayer.Accent], 120, 500);
        Assert.InRange(counts[(int)GroundLayer.Landmark], 25, 120);
    }

    [Fact]
    public void AnEmptySet_PlacesNothing_AndClearsTheOutput()
    {
        (PropIndex props, StaticGround ground) = GroundTestSets.World(WorldDefaults.StartupFloorSize);
        List<GroundPlacement> output = [new GroundPlacement(0, Vector3.Zero, 0f, 1f)];

        GroundScatterLayout.Build(ground, props.Placements, GroundAssetSet.Empty, output);

        Assert.Empty(output);
    }

    [Fact]
    public void MushroomsGatherNearTreesAndStumps()
    {
        var (ground, _, assets, placed) = Build(WorldDefaults.ServerNewWorldSize);
        List<Vector2> hosts = placed
            .Where(p => AssetOf(assets, p).Class is GroundAssetClass.Tree or GroundAssetClass.Stump)
            .Select(p => new Vector2(p.Position.X, p.Position.Z))
            .ToList();
        Assert.NotEmpty(hosts);

        int mushroomsNear = 0;
        int coverNear = 0;
        int mushroomsFar = 0;
        int coverFar = 0;
        foreach (GroundPlacement p in placed.Where(p => AssetOf(assets, p).Layer == GroundLayer.Cover))
        {
            Vector2 at = new(p.Position.X, p.Position.Z);
            bool near = hosts.Any(h => (h - at).Length <= 2.5f);
            bool mushroom = AssetOf(assets, p).Class == GroundAssetClass.Mushroom;
            if (near)
            {
                coverNear++;
                mushroomsNear += mushroom ? 1 : 0;
            }
            else
            {
                coverFar++;
                mushroomsFar += mushroom ? 1 : 0;
            }
        }

        Assert.True(coverNear > 0 && coverFar > 0);
        Assert.True((double)mushroomsNear / coverNear > 1.5 * ((double)mushroomsFar / coverFar), $"near {mushroomsNear}/{coverNear}, far {mushroomsFar}/{coverFar}");
    }

    [Fact]
    public void OnSnow_OnlyScorchDecalsAppear()
    {
        StaticGround snow = GroundTestSets.Flat(60, GeometryMaterialId.Snow);
        GroundAssetSet assets = GroundTestSets.Synthetic();

        List<GroundPlacement> placed = GroundTestSets.Layout(snow, [], assets);

        Assert.NotEmpty(placed);
        Assert.All(placed, p => Assert.Equal(GroundAssetClass.DecalScorch, assets.Assets[p.Asset].Class));
    }

    [Fact]
    public void DecalPath_FavoursThePathCells()
    {
        // A grass plain with a two-cell-wide path across the middle: path decals concentrate on and beside it.
        StaticGround ground = new();
        for (int z = 0; z < 80; z++)
        {
            for (int x = 0; x < 80; x++)
            {
                ground.Add(x, 0, z, true, z is 39 or 40 ? GeometryMaterialId.GrassPath : GeometryMaterialId.Grass);
            }
        }

        ground.Seal();
        GroundAssetSet assets = GroundAssetSet.Parse(
            [("t.json", GroundTestSets.Manifest(
                GroundTestSets.Entry("decals.decal_dirt", "decal.dirt", "decal", 0.5f, 0f, 0f, ["grass"]),
                GroundTestSets.Entry("decals.decal_path", "decal.path", "decal", 0.5f, 0f, 0f, ["grass_path", "grass"])))],
            GroundTestSets.Dir);

        List<GroundPlacement> placed = GroundTestSets.Layout(ground, [], assets);

        double near = placed.Count(p => assets.Assets[p.Asset].Class == GroundAssetClass.DecalPath && MathF.Abs(p.Position.Z - 40f) <= 2.5f);
        double nearAll = placed.Count(p => MathF.Abs(p.Position.Z - 40f) <= 2.5f);
        double farPath = placed.Count(p => assets.Assets[p.Asset].Class == GroundAssetClass.DecalPath && MathF.Abs(p.Position.Z - 40f) > 2.5f);
        double farAll = placed.Count(p => MathF.Abs(p.Position.Z - 40f) > 2.5f);
        Assert.True(nearAll > 0 && farAll > 0);
        Assert.True(near / nearAll > (farPath / farAll) + 0.15, $"near {near}/{nearAll}, far {farPath}/{farAll}");
    }

    [Fact]
    public void TheLayoutDoesNotReadAnyStateButItsInputs()
    {
        // Same ground, props and set from two independently built worlds: identical output.
        var (_, _, _, a) = Build(WorldDefaults.ServerNewWorldSize);
        var (_, _, _, b) = Build(WorldDefaults.ServerNewWorldSize);

        Assert.Equal(a.Count, b.Count);
        Assert.True(a.SequenceEqual(b));
    }

    // The shipped set (flora, stone, decals manifests) on the real hundred world: every placement stands on a
    // material its own manifest allows, and each layer lands in the plan's band. Measured at integration: decal 494,
    // cover 5052, accent 185, landmark 49 (the default terrain is all grass, so grass-free assets are not placed here).
    [Fact]
    public void TheShippedSet_OnTheHundredWorld_StandsOnAllowedMaterials_InMeasuredBands()
    {
        GroundAssetSet assets = GroundAssetSet.LoadShipped();
        Assert.Equal(100, assets.Assets.Count);
        (PropIndex props, StaticGround ground) = GroundTestSets.World(WorldDefaults.ServerNewWorldSize);
        List<GroundPlacement> placed = GroundTestSets.Layout(ground, props.Placements, assets);
        int[] counts = new int[4];
        foreach (GroundPlacement p in placed)
        {
            GroundAsset asset = AssetOf(assets, p);
            Assert.Contains(ColumnAt(ground, p).Material, asset.Ground);
            counts[(int)asset.Layer]++;
        }

        _output.WriteLine($"shipped: decal {counts[0]} cover {counts[1]} accent {counts[2]} landmark {counts[3]}");
        Assert.InRange(counts[(int)GroundLayer.Decal], 400, 1100);
        Assert.InRange(counts[(int)GroundLayer.Cover], 1500, 6000);
        Assert.InRange(counts[(int)GroundLayer.Accent], 120, 500);
        Assert.InRange(counts[(int)GroundLayer.Landmark], 25, 120);
    }
}
