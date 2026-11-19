using Dream.Rendering.Effects;
using Dream.Rendering.Lighting;
using Dream.Rendering.World;
using Dream.Simulation.Inventory;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Rendering.Lighting;

// The steady fire ranking trusts three side fields every fire producer must set: Wander (the offset it added to the
// resting position), Flicker (the intensity multiplier) and a Radius breathing with FireFlicker.RadiusScaleOf(Flicker).
// One test per producer, at several clock values, with a non-zero offset; the helper has a synthetic negative.
public class FireProducerSteadyFieldsTests
{
    private static readonly double[] Clocks = { 0.0, 1.37, 5.9, 42.25, 311.6 };

    private static void AssertSteadyFields(PointLight light, Vector3 basePosition, float baseRadius, FireFlickerKind kind, double clock)
    {
        int seed = FireFlicker.SeedFor(basePosition);
        FireFlickerSample sample = FireFlicker.Sample(kind, basePosition, clock, seed);
        Assert.NotEqual(Vector3.Zero, sample.Offset);
        Assert.Equal(sample.Offset, light.Wander);
        Assert.Equal(basePosition.X, light.Position.X - light.Wander.X, 4);
        Assert.Equal(basePosition.Y, light.Position.Y - light.Wander.Y, 4);
        Assert.Equal(basePosition.Z, light.Position.Z - light.Wander.Z, 4);
        Assert.Equal(sample.Intensity, light.Flicker);
        Assert.Equal(baseRadius * FireFlicker.RadiusScaleOf(light.Flicker), light.Radius, 4);
    }

    [Fact]
    public void Helper_Fails_On_A_Light_Built_Without_Wander()
    {
        Vector3 position = new(3.5f, 2f, 4.5f);
        int seed = FireFlicker.SeedFor(position);
        FireFlickerSample sample = FireFlicker.Sample(FireFlickerKind.Lamp, position, 1.37, seed);
        PointLight noWander = new(position + sample.Offset, LampLightIndex.LampRadius * sample.RadiusScale, LampLightIndex.LampColor * sample.Intensity, seed, 0, sample.Intensity);
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertSteadyFields(noWander, position, LampLightIndex.LampRadius, FireFlickerKind.Lamp, 1.37));
    }

    [Fact]
    public void LampLightIndex_Sets_Wander_Flicker_And_Radius()
    {
        Vector3 center = new(6.5f, 2.5f, 9.5f);
        foreach (double clock in Clocks)
        {
            AssertSteadyFields(LampLightIndex.CreateLight(center, clock), center, LampLightIndex.LampRadius, FireFlickerKind.Lamp, clock);
        }
    }

    [Fact]
    public void PlacedLightIndex_Sets_Wander_Flicker_And_Radius()
    {
        List<Cube> cubes = new();
        for (int x = 0; x < 48; x++)
        {
            for (int z = 0; z < 48; z++)
            {
                cubes.Add(new Cube(isEmpty: false, isSolid: true) { Position = new Vector3(x + 0.5f, 0.5f, z + 0.5f), MaterialId = GeometryMaterialId.Grass });
            }
        }

        GeometryRenderExport export = new(DirtyCells: [], Cells: [], Blocks: [], CompatibilityCubes: cubes) { StaticContentVersion = 1 };
        Vector3 camera = new(8.5f, 2.5f, 8.5f);
        foreach (double clock in Clocks)
        {
            PlacedLightIndex index = new();
            LightCollector collector = new();
            index.Collect(export, camera, collector, 0f, clock);
            Assert.True(collector.Count > 0);
            // Match each light to its placement by resting position.
            foreach (PointLight light in collector.Lights)
            {
                PlacedLight placed = index.Placements.Single(p => Vector3.Distance(p.Position, light.Position - light.Wander) < 1e-3f);
                AssertSteadyFields(light, placed.Position, placed.Radius, FireFlickerKind.Brazier, clock);
            }
        }
    }

    [Theory]
    [InlineData(HeldLightKind.Torch, FireFlickerKind.HeldTorch)]
    [InlineData(HeldLightKind.Lantern, FireFlickerKind.HeldLantern)]
    public void HeldLightCatalog_Sets_Wander_Flicker_And_Radius(HeldLightKind kind, FireFlickerKind fire)
    {
        Vector3 position = new(2.5f, 1.4f, -7.5f);
        foreach (double clock in Clocks)
        {
            Assert.True(HeldLightCatalog.TryCreate(kind, position, clock, 7, out PointLight light, 7));
            float radius = kind == HeldLightKind.Torch ? HeldLightCatalog.TorchRadius : HeldLightCatalog.LanternRadius;
            // The held producer seeds the sample with the carrier seed, not the position hash.
            FireFlickerSample sample = FireFlicker.Sample(fire, position, clock, 7);
            if (kind == HeldLightKind.Torch)
            {
                Assert.NotEqual(Vector3.Zero, sample.Offset);
            }

            Assert.Equal(sample.Offset, light.Wander);
            Assert.Equal(position.X, light.Position.X - light.Wander.X, 4);
            Assert.Equal(position.Y, light.Position.Y - light.Wander.Y, 4);
            Assert.Equal(position.Z, light.Position.Z - light.Wander.Z, 4);
            Assert.Equal(sample.Intensity, light.Flicker);
            Assert.Equal(radius * FireFlicker.RadiusScaleOf(light.Flicker), light.Radius, 4);
        }
    }
}
