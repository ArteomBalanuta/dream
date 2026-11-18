using OpenTK.Mathematics;
using Xunit;
using Dream.Client.Presentation;
using Dream.Client.Scenes;
using Dream.Rendering.Effects;
using Dream.Rendering.Lighting;
using Dream.Rendering.World;
using Dream.Simulation.World;
using Dream.Simulation.Inventory;
using Dream.Simulation.World.Geometry;
using Dream.Tests.Utilities;

namespace Dream.Tests.Rendering.World;

// Wave T2c: LampLightIndex turns the lamp blocks of a render export into point lights. Static
// lamps are rescanned only when the static content version changes; dynamic lamps every frame.
public class LampLightIndexTests
{
    private static readonly Vector3 Camera = new(2f, 1.6f, 2f);

    [Fact]
    public void Collect_AddsOneLightPerStaticAndDynamicLamp_AtTheCubeCenter()
    {
        Cube staticLamp = CreateCube(new Vector3(2.5f, 1.5f, 2.5f), GeometryMaterialId.Lamp);
        Cube dynamicLamp = CreateCube(new Vector3(6.25f, 1.5f, 3.75f), GeometryMaterialId.Lamp);
        GeometryRenderExport export = CreateExport(
            version: 4,
            [CreateCube(new Vector3(0.5f, 0.5f, 0.5f), GeometryMaterialId.Grass), staticLamp],
            [CreateCube(new Vector3(4.5f, 1.5f, 4.5f), GeometryMaterialId.Stone), dynamicLamp]);
        LampLightIndex index = new();
        LightCollector lights = new();

        index.Collect(export, Camera, lights);

        Assert.Equal(2, lights.Count);
        Assert.Equal(2, index.LastLampCount);
        Assert.Equal(1, index.StaticLampCount);
        Assert.Equal(new PointLight(staticLamp.Position, 10f, new Vector3(1f, 0.78f, 0.5f) * 1.8f), lights.Lights[0]);
        Assert.Equal(new PointLight(dynamicLamp.Position, LampLightIndex.LampRadius, LampLightIndex.LampColor), lights.Lights[1]);
    }

    [Fact]
    public void StaticLamps_AreRescannedOnlyWhenTheVersionChanges_DynamicLampsEveryFrame()
    {
        List<Cube> staticCubes = [CreateCube(new Vector3(2.5f, 1.5f, 2.5f), GeometryMaterialId.Lamp)];
        Cube movingLamp = CreateCube(new Vector3(4.5f, 1.5f, 4.5f), GeometryMaterialId.Lamp);
        LampLightIndex index = new();
        LightCollector lights = new();

        lights.Clear();
        index.Collect(CreateExport(version: 1, staticCubes, [movingLamp]), Camera, lights);
        Assert.Equal(1, index.StaticRescanCount);

        // Same version: a static lamp added behind the index's back is not seen (no rescan), and the
        // moved dynamic lamp is.
        staticCubes.Add(CreateCube(new Vector3(8.5f, 1.5f, 8.5f), GeometryMaterialId.Lamp));
        movingLamp.Position = new Vector3(5f, 1.5f, 4.5f);
        lights.Clear();
        index.Collect(CreateExport(version: 1, staticCubes, [movingLamp]), Camera, lights);
        Assert.Equal(1, index.StaticRescanCount);
        Assert.Equal(2, lights.Count);
        Assert.Equal(movingLamp.Position, lights.Lights[1].Position);

        // New version: rescanned, both static lamps now light.
        lights.Clear();
        index.Collect(CreateExport(version: 2, staticCubes, []), Camera, lights);
        Assert.Equal(2, index.StaticRescanCount);
        Assert.Equal(2, lights.Count);

        // Version 0 is unknown content: rescanned every frame.
        index.Collect(CreateExport(version: 0, staticCubes, []), Camera, lights);
        index.Collect(CreateExport(version: 0, staticCubes, []), Camera, lights);
        Assert.Equal(4, index.StaticRescanCount);
    }

    [Fact]
    public void NoLamps_AddNoLights()
    {
        LampLightIndex index = new();
        LightCollector lights = new();

        index.Collect(CreateExport(version: 3, [CreateCube(Vector3.One, GeometryMaterialId.Stone)], []), Camera, lights);
        index.Collect(new GeometryRenderExport([], [], [], []), Camera, lights);

        Assert.Equal(0, lights.Count);
        Assert.Equal(0, index.LastLampCount);
    }

    [Fact]
    public void Collect_AllocatesNothingPerFrame_InSteadyState()
    {
        List<Cube> staticCubes = [];
        List<Cube> dynamicCubes = [];
        for (int i = 0; i < 200; i++)
        {
            staticCubes.Add(CreateCube(new Vector3((i * 0.25f) + 0.5f, 0.5f, 0.5f), i % 10 == 0 ? GeometryMaterialId.Lamp : GeometryMaterialId.Grass));
            dynamicCubes.Add(CreateCube(new Vector3((i * 0.25f) + 0.5f, 1.5f, 2.5f), i % 7 == 0 ? GeometryMaterialId.Lamp : GeometryMaterialId.Stone));
        }

        GeometryRenderExport export = CreateExport(version: 9, staticCubes, dynamicCubes);
        LampLightIndex index = new();
        LightCollector lights = new();
        index.Collect(export, Camera, lights);

        long bytes = AllocationAssert.AtMost(0, () =>
        {
            lights.Clear();
            index.Collect(export, Camera, lights);
        });

        Assert.Equal(0, bytes);
        Assert.Equal(1, index.StaticRescanCount);
        Assert.Equal(20 + 29, lights.Count);
    }

    [Fact]
    public void Collect_FromAMapPresentationExport_FindsAPlacedLamp_WithoutRescanningEachFrame()
    {
        Map map = new(width: 8, depth: 8);
        Cube lamp = Assert.IsType<Cube>(map.TryAddCube(new Vector3(3f, 1f, 3f), withPhysics: false, materialId: GeometryMaterialId.Lamp));
        LampLightIndex index = new();
        LightCollector lights = new();

        for (int frame = 0; frame < 3; frame++)
        {
            lights.Clear();
            index.Collect(map.CapturePresentationGeometryRenderExport(lamp.Position, 32f), lamp.Position, lights);
            Assert.Contains(lights.Lights.ToArray(), light => light.Position == lamp.Position);
        }

        Assert.Equal(1, index.StaticRescanCount);
    }

    [Fact]
    public void Collect_SkipsLampsBeyondTheCollectDistance()
    {
        Assert.Equal(WorldVisibility.MaxRenderDistance + LampLightIndex.LampRadius, LampLightIndex.CollectDistance);
        Cube near = CreateCube(Camera + new Vector3(LampLightIndex.CollectDistance - 0.5f, 0f, 0f), GeometryMaterialId.Lamp);
        Cube far = CreateCube(Camera + new Vector3(0f, 0f, LampLightIndex.CollectDistance + 0.5f), GeometryMaterialId.Lamp);
        Cube farDynamic = CreateCube(Camera - new Vector3(LampLightIndex.CollectDistance + 0.5f, 0f, 0f), GeometryMaterialId.Lamp);
        LampLightIndex index = new();
        LightCollector lights = new();

        index.Collect(CreateExport(version: 5, [near, far], [farDynamic]), Camera, lights);

        Assert.Equal(2, index.StaticLampCount);
        Assert.Equal(1, index.LastLampCount);
        Assert.Equal(near.Position, Assert.Single(lights.Lights.ToArray()).Position);
    }

    // The scene's order (PlayScene.CollectFrameLights): held lights first, then lamps. 2,000 lamps
    // around the camera overflow the 1,024-slot collector, and the torch keeps its slot.
    [Fact]
    public void TwoThousandLamps_DoNotDropAHeldTorch()
    {
        List<Cube> lamps = [];
        for (int i = 0; i < 2000; i++)
        {
            lamps.Add(CreateCube(new Vector3((i % 40) + 0.5f, 1.5f, (i / 40) + 0.5f), GeometryMaterialId.Lamp));
        }

        LampLightIndex index = new();
        LightCollector lights = new();
        Vector3 camera = new(20f, 1.6f, 25f);
        Vector3 front = -Vector3.UnitZ;
        PlayScene.LocalHeldLight torch = new(HeldLightKind.Torch, ActorId: 1, FirstPerson: true, camera, front);

        PlayScene.AddHeldLights(lights, in torch, camera, front, Vector3.UnitX, Vector3.UnitY, [], timeSeconds: 0.0);
        index.Collect(CreateExport(version: 1, lamps, []), camera, lights);

        Assert.Equal(LightCollector.Capacity, lights.Count);
        Assert.True(lights.Dropped > 0);

        // HeldLightCatalog emits the torch at anchor + FireFlicker's wander offset, so the only
        // bound the slot-0 light obeys is the corner of the wander box: the lateral constant on X
        // and Z, the vertical one on Y. Derive it; never restate it as a number.
        Vector3 drift = lights.Lights[0].Position - HeldLightAnchor.FirstPerson(camera, front, Vector3.UnitX, Vector3.UnitY);
        Assert.True(MathF.Abs(drift.X) <= FireFlicker.OffsetLateral + WanderEpsilon, $"lateral X drift {drift.X}");
        Assert.True(MathF.Abs(drift.Z) <= FireFlicker.OffsetLateral + WanderEpsilon, $"lateral Z drift {drift.Z}");
        Assert.True(MathF.Abs(drift.Y) <= FireFlicker.OffsetVertical + WanderEpsilon, $"vertical drift {drift.Y}");
        Assert.True(drift.Length <= WanderReach + WanderEpsilon, $"drift {drift.Length} exceeds the wander reach {WanderReach}");
        Assert.InRange(lights.Lights[0].Radius, 7.29f, 8.71f);
    }

    // What the drift bound above is worth, asserted against the thing it bounds: the SAMPLED offset. Eight
    // seconds of clock at 240 Hz, on the seeds the two producers in that test actually carry - a lamp's
    // seed is FireFlicker.SeedFor(its centre), a held torch's is the actor id - never leaves the reach,
    // and gets most of the way out to it, so a reach that were merely enormous fails here too. Comparing
    // the reach against the expression that defines it would assert nothing at all.
    [Fact]
    public void TheWanderReach_BoundsTheSampledOffset_AndIsNearlyReached()
    {
        const int Rate = 240;
        (FireFlickerKind Kind, int Seed)[] fires =
        [
            (FireFlickerKind.Lamp, FireFlicker.SeedFor(new Vector3(20.5f, 1.5f, 25.5f))),
            (FireFlickerKind.Lamp, FireFlicker.SeedFor(new Vector3(3.5f, 1.5f, 3.5f))),
            (FireFlickerKind.Lamp, FireFlicker.SeedFor(new Vector3(0.5f, 1.5f, 39.5f))),
            (FireFlickerKind.HeldTorch, 1)
        ];

        float worst = 0f;
        foreach ((FireFlickerKind kind, int seed) in fires)
        {
            for (int i = 0; i < 8 * Rate; i++)
            {
                Vector3 offset = FireFlicker.Sample(kind, Vector3.Zero, (double)i / Rate, seed).Offset;
                Assert.True(
                    offset.Length <= WanderReach + WanderEpsilon,
                    $"{kind} seed {seed} at t={(double)i / Rate}: offset {offset} is {offset.Length} long, past the wander reach {WanderReach}");
                worst = MathF.Max(worst, offset.Length);
            }
        }

        Assert.True(
            worst >= 0.7f * WanderReach,
            $"the flame never got past {worst / WanderReach:0.00} of the wander reach {WanderReach}, so the bound measures nothing");
    }

    private const float WanderEpsilon = 1e-5f;

    private static readonly float WanderReach = MathF.Sqrt(
        (2f * FireFlicker.OffsetLateral * FireFlicker.OffsetLateral)
        + (FireFlicker.OffsetVertical * FireFlicker.OffsetVertical));

    [Fact]
    public void PartlyMinedLamp_StillLights_FromItsSolidSubcells_AndAFullyMinedOneDoesNot()
    {
        Map map = new(width: 8, depth: 8);
        Cube lamp = Assert.IsType<Cube>(map.TryAddCube(new Vector3(3f, 1f, 3f), withPhysics: false, materialId: GeometryMaterialId.Lamp));
        GeometryCellKey key = new(3, 1, 3);
        LampLightIndex index = new();
        LightCollector lights = new();

        // Mine the subcell at the cell's minimum corner: the lamp is subdivided, so it leaves the
        // coarse cubes, and the light moves to the centroid of what is left (away from that corner).
        map.ApplyGeometrySubcellSolid(key, new GeometrySubcellKey(0, 0, 0), isSolid: false);
        GeometryRenderExport export = map.CapturePresentationGeometryRenderExport(lamp.Position, 32f);
        Assert.DoesNotContain(export.CompatibilityCubes, cube => cube.MaterialId == GeometryMaterialId.Lamp);
        Assert.DoesNotContain(export.DynamicCompatibilityCubes, cube => cube.MaterialId == GeometryMaterialId.Lamp);

        index.Collect(export, lamp.Position, lights);
        PointLight light = Assert.Single(lights.Lights.ToArray());
        Assert.Equal(LampLightIndex.LampRadius, light.Radius);
        Vector3 offset = light.Position - lamp.Position;
        Assert.True(offset.X > 0f && offset.Y > 0f && offset.Z > 0f, $"centroid {light.Position} should move away from the mined corner");
        Assert.True(offset.Length < 0.5f);

        // Mine every remaining subcell: no light.
        GeometryCellRecord record = Assert.IsType<GeometryCellRecord>(map.TryGetGeometryCellAtCell(3, 1, 3));
        int resolution = record.Definition.SubdivisionResolution;
        for (int x = 0; x < resolution; x++)
        {
            for (int y = 0; y < resolution; y++)
            {
                for (int z = 0; z < resolution; z++)
                {
                    map.ApplyGeometrySubcellSolid(key, new GeometrySubcellKey(x, y, z), isSolid: false);
                }
            }
        }

        lights.Clear();
        index.Collect(map.CapturePresentationGeometryRenderExport(lamp.Position, 32f), lamp.Position, lights);
        Assert.Equal(0, lights.Count);
        Assert.Equal(0, index.LastLampCount);
    }

    [Fact]
    public void Collect_UsesStaticCells_FromTheReferenceCaptureToo()
    {
        Map map = new(width: 8, depth: 8);
        Cube lamp = Assert.IsType<Cube>(map.TryAddCube(new Vector3(3f, 1f, 3f), withPhysics: false, materialId: GeometryMaterialId.Lamp));
        map.ApplyGeometrySubcellSolid(new GeometryCellKey(3, 1, 3), new GeometrySubcellKey(1, 1, 1), isSolid: false);
        LampLightIndex index = new();
        LightCollector lights = new();

        GeometryRenderExport export = map.CaptureFullGeometryRenderExport();
        Assert.NotNull(export.StaticCells);
        index.Collect(export, lamp.Position, lights);

        Assert.Equal(1, lights.Count);
    }

    private static GeometryRenderExport CreateExport(long version, IReadOnlyList<Cube> staticCubes, IReadOnlyList<Cube> dynamicCubes)
    {
        return new GeometryRenderExport(
            DirtyCells: [],
            Cells: [],
            Blocks: [],
            CompatibilityCubes: staticCubes,
            dynamicCompatibilityCubes: dynamicCubes)
        {
            StaticContentVersion = version
        };
    }

    private static Cube CreateCube(Vector3 position, GeometryMaterialId material)
    {
        return new Cube(isEmpty: false, isSolid: true)
        {
            Position = position,
            MaterialId = material
        };
    }
}
