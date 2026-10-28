using OpenTK.Mathematics;
using Xunit;
using Dream.Rendering.Lighting;
using Dream.Rendering.World;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using Dream.Tests.Utilities;

namespace Dream.Tests.Rendering.World;

// Wave T2c: LampLightIndex turns the lamp blocks of a render export into point lights. Static
// lamps are rescanned only when the static content version changes; dynamic lamps every frame.
public class LampLightIndexTests
{
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

        index.Collect(export, lights);

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
        index.Collect(CreateExport(version: 1, staticCubes, [movingLamp]), lights);
        Assert.Equal(1, index.StaticRescanCount);

        // Same version: a static lamp added behind the index's back is not seen (no rescan), and the
        // moved dynamic lamp is.
        staticCubes.Add(CreateCube(new Vector3(8.5f, 1.5f, 8.5f), GeometryMaterialId.Lamp));
        movingLamp.Position = new Vector3(5f, 1.5f, 4.5f);
        lights.Clear();
        index.Collect(CreateExport(version: 1, staticCubes, [movingLamp]), lights);
        Assert.Equal(1, index.StaticRescanCount);
        Assert.Equal(2, lights.Count);
        Assert.Equal(movingLamp.Position, lights.Lights[1].Position);

        // New version: rescanned, both static lamps now light.
        lights.Clear();
        index.Collect(CreateExport(version: 2, staticCubes, []), lights);
        Assert.Equal(2, index.StaticRescanCount);
        Assert.Equal(2, lights.Count);

        // Version 0 is unknown content: rescanned every frame.
        index.Collect(CreateExport(version: 0, staticCubes, []), lights);
        index.Collect(CreateExport(version: 0, staticCubes, []), lights);
        Assert.Equal(4, index.StaticRescanCount);
    }

    [Fact]
    public void NoLamps_AddNoLights()
    {
        LampLightIndex index = new();
        LightCollector lights = new();

        index.Collect(CreateExport(version: 3, [CreateCube(Vector3.One, GeometryMaterialId.Stone)], []), lights);
        index.Collect(new GeometryRenderExport([], [], [], []), lights);

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
            staticCubes.Add(CreateCube(new Vector3(i + 0.5f, 0.5f, 0.5f), i % 10 == 0 ? GeometryMaterialId.Lamp : GeometryMaterialId.Grass));
            dynamicCubes.Add(CreateCube(new Vector3(i + 0.5f, 1.5f, 2.5f), i % 7 == 0 ? GeometryMaterialId.Lamp : GeometryMaterialId.Stone));
        }

        GeometryRenderExport export = CreateExport(version: 9, staticCubes, dynamicCubes);
        LampLightIndex index = new();
        LightCollector lights = new();
        index.Collect(export, lights);

        long bytes = AllocationAssert.MeasureBytesPerIteration(() =>
        {
            lights.Clear();
            index.Collect(export, lights);
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
            index.Collect(map.CapturePresentationGeometryRenderExport(lamp.Position, 32f), lights);
            Assert.Contains(lights.Lights.ToArray(), light => light.Position == lamp.Position);
        }

        Assert.Equal(1, index.StaticRescanCount);
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
