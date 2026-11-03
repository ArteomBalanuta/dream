
using Dream.Rendering.Lighting;
using Dream.Rendering.Sky;
using Dream.Rendering.World;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using Dream.Tests.Utilities;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Rendering.Lighting;

// WS-B: PlacedLightIndex derives the brazier lights from replicated static content only. It is a
// pure function of the export, rescans only when the StaticContentVersion changes, allocates nothing
// in steady state, and keeps the lights far enough apart that LightList.MaxLights never overflows.
public class PlacedLightIndexTests
{
    private static readonly Vector3 Camera = new(8.5f, 2.5f, 8.5f);

    [Fact]
    public void BrazierRadius_DeliberatelyEqualsTheLampRadius_SoItInheritsTheLampFx()
    {
        // FxSimulation.TryClassify tags a light by radius (RadiusTolerance 0.01): equal to the lamp's
        // gives the brazier the fire halo and embers; any other value would give it no FX at all.
        Assert.Equal(LampLightIndex.LampRadius, PlacedLightIndex.BrazierRadius);
        Assert.NotEqual(HeldLightCatalog.TorchRadius, PlacedLightIndex.BrazierRadius);
        Assert.NotEqual(HeldLightCatalog.LanternRadius, PlacedLightIndex.BrazierRadius);
    }

    [Fact]
    public void Placements_SitOnTheTopOfTheAnchorColumn_OnePerBucket()
    {
        // A flat floor of 48 x 48 cells (buckets 0..2 in x and z), plus a 3-high pillar on the anchor
        // column of bucket (0, 0).
        (int ax, int az) = PlacedLightIndex.AnchorColumn(0, 0);
        List<Cube> cubes = Floor(48, 48);
        for (int y = 1; y <= 3; y++)
        {
            cubes.Add(CreateCube(ax, y, az, GeometryMaterialId.Stone));
        }

        PlacedLightIndex index = new();
        index.Collect(CreateExport(1, cubes), Camera, new LightCollector());

        Assert.Equal(9, index.Placements.Count);
        PlacedLight onPillar = Assert.Single(index.Placements, p => (int)p.Position.X == ax && (int)p.Position.Z == az);
        Assert.Equal(new Vector3(ax + 0.5f, 3f + 1f + PlacedLightIndex.FlameHeight, az + 0.5f), onPillar.Position);
        Assert.Equal(PlacedLightIndex.BrazierRadius, onPillar.Radius);
        Assert.Equal(PlacedLightIndex.BrazierColor, onPillar.Color);
        foreach (PlacedLight placed in index.Placements.Where(p => p != onPillar))
        {
            Assert.Equal(1f + PlacedLightIndex.FlameHeight, placed.Position.Y);
        }
    }

    [Fact]
    public void ABucketWhoseAnchorColumnIsEmptyOrTopsOutInALamp_GetsNoBrazier()
    {
        (int ex, int ez) = PlacedLightIndex.AnchorColumn(0, 0);
        (int lx, int lz) = PlacedLightIndex.AnchorColumn(1, 0);
        List<Cube> cubes = Floor(32, 16);
        cubes.RemoveAll(c => (int)c.Position.X == ex && (int)c.Position.Z == ez);
        cubes.Add(CreateCube(lx, 1, lz, GeometryMaterialId.Lamp));

        PlacedLightIndex index = new();
        index.Collect(CreateExport(1, cubes), Camera, new LightCollector());

        Assert.Empty(index.Placements);
    }

    [Fact]
    public void Placements_AreAPureFunctionOfTheCells_NotOfTheirOrderOrTheIndexInstance()
    {
        List<Cube> cubes = Floor(56, 56);
        cubes.Add(CreateCube(PlacedLightIndex.AnchorColumn(1, 1).X, 1, PlacedLightIndex.AnchorColumn(1, 1).Z, GeometryMaterialId.Rock));
        List<Cube> shuffled = [.. cubes];
        shuffled.Reverse();
        Random rng = new(7);
        for (int i = shuffled.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (shuffled[i], shuffled[j]) = (shuffled[j], shuffled[i]);
        }

        PlacedLightIndex a = new();
        PlacedLightIndex b = new();
        a.Collect(CreateExport(1, cubes), Camera, new LightCollector());
        b.Collect(CreateExport(99, shuffled), Camera, new LightCollector());

        Assert.NotEmpty(a.Placements);
        Assert.Equal(a.Placements, b.Placements);
    }

    [Fact]
    public void StaticCellsAndTheCompatibilityCubeFallback_GiveTheSamePlacements()
    {
        Map map = new(width: 56, depth: 56);
        GeometryRenderExport export = map.CapturePresentationGeometryRenderExport(new Vector3(28f, 2f, 28f), 200f);
        Assert.NotNull(export.StaticCells);
        GeometryRenderExport cubesOnly = new(export.DirtyCells, export.Cells, export.Blocks, export.CompatibilityCubes)
        {
            StaticContentVersion = export.StaticContentVersion,
        };

        PlacedLightIndex fromCells = new();
        PlacedLightIndex fromCubes = new();
        fromCells.Collect(export, Camera, new LightCollector());
        fromCubes.Collect(cubesOnly, Camera, new LightCollector());

        Assert.NotEmpty(fromCells.Placements);
        Assert.Equal(fromCells.Placements, fromCubes.Placements);
    }

    [Fact]
    public void StaticContent_IsRescannedOnlyWhenTheVersionChanges_VersionZeroEveryFrame()
    {
        List<Cube> cubes = Floor(16, 16);
        PlacedLightIndex index = new();
        LightCollector lights = new();

        index.Collect(CreateExport(1, cubes), Camera, lights);
        Assert.Equal(1, index.StaticRescanCount);
        Assert.Single(index.Placements);

        // Same version: a column added behind the index's back is not seen.
        (int ax, int az) = PlacedLightIndex.AnchorColumn(0, 0);
        cubes.Add(CreateCube(ax, 1, az, GeometryMaterialId.Stone));
        index.Collect(CreateExport(1, cubes), Camera, lights);
        Assert.Equal(1, index.StaticRescanCount);
        Assert.Equal(1f + PlacedLightIndex.FlameHeight, index.Placements[0].Position.Y);

        // New version: rescanned, the flame rises with the surface.
        index.Collect(CreateExport(2, cubes), Camera, lights);
        Assert.Equal(2, index.StaticRescanCount);
        Assert.Equal(2f + PlacedLightIndex.FlameHeight, index.Placements[0].Position.Y);

        // Version 0 is unknown content: rescanned every frame.
        index.Collect(CreateExport(0, cubes), Camera, lights);
        index.Collect(CreateExport(0, cubes), Camera, lights);
        Assert.Equal(4, index.StaticRescanCount);
    }

    [Fact]
    public void SteadyStateCollect_AllocatesNothing()
    {
        GeometryRenderExport export = CreateExport(1, Floor(56, 56));
        PlacedLightIndex index = new();
        LightCollector lights = new();
        index.Collect(export, Camera, lights);

        long bytes = AllocationAssert.MeasureBytesPerIteration(() =>
        {
            lights.Clear();
            index.Collect(export, Camera, lights);
        });

        Assert.Equal(0, bytes);
        Assert.Equal(1, index.StaticRescanCount);
        Assert.Equal(index.LastCollectedCount, lights.Count);
        Assert.NotEqual(0, lights.Count);
    }

    [Fact]
    public void Collect_AddsOnlyBraziersWithinTheCollectDistance_AsPointLights()
    {
        PlacedLightIndex index = new();
        LightCollector lights = new();
        index.Collect(CreateExport(1, Floor(100, 100)), new Vector3(50f, 3f, 50f), lights);

        Assert.True(index.Placements.Count >= 30);
        Assert.Equal(index.LastCollectedCount, lights.Count);
        foreach (PointLight light in lights.Lights)
        {
            Assert.True(Vector3.Distance(light.Position, new Vector3(50f, 3f, 50f)) <= PlacedLightIndex.CollectDistance + 1e-3f);
            Assert.Equal(PlacedLightIndex.BrazierRadius, light.Radius);
            Assert.Equal(PlacedLightIndex.BrazierColor, light.Color);
        }

        int inRange = index.Placements.Count(p => Vector3.Distance(p.Position, new Vector3(50f, 3f, 50f)) <= PlacedLightIndex.CollectDistance);
        Assert.Equal(inRange, lights.Count);
    }

    // The 16-light cap would pop lights in and out if more braziers than slots were in reach. Braziers
    // are at least 14 apart, and for any camera on a 100 x 100 floor the collected count stays well
    // under LightList.MaxLights, leaving slots for the held torch and a few lamps.
    [Fact]
    public void Braziers_AreAtLeast14Apart_AndNoCameraCollectsMoreThan13()
    {
        PlacedLightIndex index = new();
        LightCollector lights = new();
        GeometryRenderExport export = CreateExport(1, Floor(100, 100));
        index.Collect(export, Camera, lights);

        for (int i = 0; i < index.Placements.Count; i++)
        {
            for (int j = i + 1; j < index.Placements.Count; j++)
            {
                Vector3 d = index.Placements[i].Position - index.Placements[j].Position;
                Assert.True(new Vector2(d.X, d.Z).Length >= 14f - 1e-4f, $"braziers {i} and {j} are {new Vector2(d.X, d.Z).Length} apart");
            }
        }

        int most = 0;
        for (float x = -10f; x <= 110f; x += 1.5f)
        {
            for (float z = -10f; z <= 110f; z += 1.5f)
            {
                lights.Clear();
                index.Collect(export, new Vector3(x, 12f, z), lights);
                most = Math.Max(most, lights.Count);
            }
        }

        Assert.InRange(most, 1, 13);
        Assert.True(most + 3 <= LightList.MaxLights, "braziers must leave slots for held and lamp lights");
    }

    [Fact]
    public void Braziers_FadeOutWithTheSun_NightAndDuskFull_NoonGone()
    {
        Assert.Equal(1f, PlacedLightIndex.DaylightFactor(0f));
        Assert.Equal(0f, PlacedLightIndex.DaylightFactor(PlacedLightIndex.SunFadeEnd));
        Assert.Equal(0f, PlacedLightIndex.DaylightFactor(SkyModelKeyframes.DaySunIntensity));
        float previous = 1f;
        for (float sun = 0f; sun <= 1f; sun += 0.01f)
        {
            float f = PlacedLightIndex.DaylightFactor(sun);
            Assert.InRange(f, 0f, previous + 1e-6f);
            previous = f;
        }

        // The sky model's own sun: full at midnight, 05:00, 19:00 and 22:00, off by 08:00 and at noon.
        foreach ((float hour, bool lit) in new[] { (0f, true), (5f, true), (19f, true), (22f, true), (8f, false), (12f, false) })
        {
            float factor = PlacedLightIndex.DaylightFactor(SkyModel.Evaluate(hour / 24f).Lighting.SunDirection.W);
            Assert.True(lit ? factor > 0.99f : factor < 0.01f, $"{hour:F0}:00 factor {factor}");
        }

        PlacedLightIndex index = new();
        LightCollector night = new();
        LightCollector day = new();
        LightCollector dusk = new();
        GeometryRenderExport export = CreateExport(1, Floor(48, 48));
        index.Collect(export, Camera, night);
        index.Collect(export, Camera, day, SkyModelKeyframes.DaySunIntensity);
        Assert.NotEqual(0, night.Count);
        Assert.Equal(0, day.Count);
        index.Collect(export, Camera, dusk, PlacedLightIndex.SunFadeEnd / 2f);
        Assert.Equal(night.Count, dusk.Count);
        Assert.True(dusk.Lights[0].Color.X < night.Lights[0].Color.X);
    }

    private static List<Cube> Floor(int width, int depth)
    {
        List<Cube> cubes = new(width * depth);
        for (int x = 0; x < width; x++)
        {
            for (int z = 0; z < depth; z++)
            {
                cubes.Add(CreateCube(x, 0, z, GeometryMaterialId.Grass));
            }
        }

        return cubes;
    }

    private static GeometryRenderExport CreateExport(long version, IReadOnlyList<Cube> staticCubes) =>
        new(DirtyCells: [], Cells: [], Blocks: [], CompatibilityCubes: staticCubes)
        {
            StaticContentVersion = version,
        };

    private static Cube CreateCube(int x, int y, int z, GeometryMaterialId material) =>
        new(isEmpty: false, isSolid: true)
        {
            Position = new Vector3(x + 0.5f, y + 0.5f, z + 0.5f),
            MaterialId = material,
        };
}
