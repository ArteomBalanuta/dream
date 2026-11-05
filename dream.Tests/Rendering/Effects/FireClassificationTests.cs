using Dream.Rendering.Effects;
using Dream.Rendering.Lighting;
using Dream.Rendering.World;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Rendering.Effects;

// Invariants of the radius-band classification that FxSimulation uses to decide which lights get fire
// effects. A radius that falls out of its band makes the halo, embers and flame vanish with no error.
// FireFlickerTests samples one seed at one position; these tests use the analytic extremes, every
// radius the engine can emit, and an exhaustive sweep of the radius axis.
public class FireClassificationTests
{
    private static readonly (FireFlickerKind Kind, float Centre, float Tolerance)[] Bands =
    [
        (FireFlickerKind.HeldTorch, HeldLightCatalog.TorchRadius, FireFlicker.TorchClassTolerance),
        (FireFlickerKind.HeldLantern, HeldLightCatalog.LanternRadius, FireFlicker.LanternClassTolerance),
        (FireFlickerKind.Lamp, LampLightIndex.LampRadius, FireFlicker.LampClassTolerance),
    ];

    private static FxSimulation.FxLightClass ClassOf(FireFlickerKind kind) => kind switch
    {
        FireFlickerKind.HeldTorch => FxSimulation.FxLightClass.Torch,
        FireFlickerKind.HeldLantern => FxSimulation.FxLightClass.Lantern,
        _ => FxSimulation.FxLightClass.Lamp,
    };

    private static FxSimulation.FxLightClass Classify(float radius)
    {
        FxSimulation.TryClassify(radius, out FxSimulation.FxLightClass lightClass);
        return lightClass;
    }

    [Fact]
    public void The_Bands_Are_Disjoint_And_The_Sweep_Matches_The_Declared_Intervals_Exactly()
    {
        // Every radius from 0 to 20 in 0.001 steps lies in at most ONE declared band, and TryClassify
        // answers exactly that band (or None). So no tie-break is ever relied on.
        for (int i = 0; i <= 20000; i++)
        {
            float radius = i * 0.001f;
            int containing = 0;
            FxSimulation.FxLightClass expected = FxSimulation.FxLightClass.None;
            foreach ((FireFlickerKind kind, float centre, float tolerance) in Bands)
            {
                if (MathF.Abs(radius - centre) <= tolerance)
                {
                    containing++;
                    expected = ClassOf(kind);
                }
            }

            Assert.True(containing <= 1, $"radius {radius} lies in {containing} bands");
            Assert.Equal(expected, Classify(radius));
        }
    }

    [Fact]
    public void The_Band_Edges_Are_Inclusive_And_One_Step_Outside_Is_Nothing()
    {
        foreach ((FireFlickerKind kind, float centre, float tolerance) in Bands)
        {
            Assert.True(FireFlicker.TryClassifyRadius(centre, out FireFlickerKind atCentre));
            Assert.Equal(kind, atCentre);
            Assert.True(FireFlicker.TryClassifyRadius(centre + tolerance - 1e-3f, out FireFlickerKind nearUpper));
            Assert.Equal(kind, nearUpper);
            Assert.True(FireFlicker.TryClassifyRadius(centre - tolerance + 1e-3f, out FireFlickerKind nearLower));
            Assert.Equal(kind, nearLower);
            Assert.False(FireFlicker.TryClassifyRadius(centre + tolerance + 1e-2f, out _), $"{kind} upper");
            Assert.False(FireFlicker.TryClassifyRadius(centre - tolerance - 1e-2f, out _), $"{kind} lower");
        }
    }

    [Fact]
    public void Non_Finite_And_Negative_Radii_Are_Never_A_Fire()
    {
        foreach (float radius in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity, -8f, -10f, 0f })
        {
            Assert.False(FireFlicker.TryClassifyRadius(radius, out _), radius.ToString());
            Assert.Equal(FxSimulation.FxLightClass.None, Classify(radius));
        }
    }

    // The extremes of the radius scale are analytic: intensity is clamped to 1 +- A, the radius follows
    // it by RadiusFollow. Any radius the engine emits is centre * a scale between those two.
    [Fact]
    public void The_Analytic_Extremes_Of_Every_Flickering_Radius_Stay_In_Their_Class()
    {
        foreach ((FireFlickerKind kind, float centre, _) in Bands)
        {
            float amplitude = FireFlicker.Amplitude(kind);
            foreach (float intensity in new[] { 1f - amplitude, 1f, 1f + amplitude })
            {
                float scale = 1f + (FireFlicker.RadiusFollow * (intensity - 1f));
                Assert.True(FireFlicker.TryClassifyRadius(centre * scale, out FireFlickerKind found), $"{kind} at {intensity}");
                Assert.Equal(kind, found);
            }
        }

        // Brazier shares the lamp centre and amplitude.
        Assert.Equal(LampLightIndex.LampRadius, PlacedLightIndex.BrazierRadius);
        Assert.Equal(FireFlicker.LampAmplitude, FireFlicker.BrazierAmplitude);
    }

    [Fact]
    public void Every_Light_The_Engine_Emits_Classifies_As_Its_Own_Fire_Over_Many_Seeds_Positions_And_Times()
    {
        Random random = new(20260);
        double[] clocks = [0.0, 0.016, 1.3, 77.7, 1024.5, 4095.99];
        for (int n = 0; n < 4000; n++)
        {
            Vector3 position = new(
                (float)((random.NextDouble() * 400.0) - 200.0),
                (float)(random.NextDouble() * 20.0),
                (float)((random.NextDouble() * 400.0) - 200.0));
            double clock = clocks[n % clocks.Length] + (random.NextDouble() * 5.0);
            int seed = random.Next(1, int.MaxValue);

            Assert.True(HeldLightCatalog.TryCreate(Dream.Simulation.Inventory.HeldLightKind.Torch, position, clock, seed, out PointLight torch));
            Assert.Equal(FxSimulation.FxLightClass.Torch, Classify(torch.Radius));

            Assert.True(HeldLightCatalog.TryCreate(Dream.Simulation.Inventory.HeldLightKind.Lantern, position, clock, seed, out PointLight lantern));
            Assert.Equal(FxSimulation.FxLightClass.Lantern, Classify(lantern.Radius));

            PointLight lamp = LampLightIndex.CreateLight(position, clock);
            Assert.Equal(FxSimulation.FxLightClass.Lamp, Classify(lamp.Radius));

            FireFlickerSample brazier = FireFlicker.Sample(FireFlickerKind.Brazier, position, clock, 0);
            Assert.Equal(FxSimulation.FxLightClass.Lamp, Classify(PlacedLightIndex.BrazierRadius * brazier.RadiusScale));
        }
    }

    [Fact]
    public void No_Flash_Light_Radius_Reads_As_A_Fire()
    {
        // The death flash (5.5) sits 0.5 from the lantern's 6; the others are far from every band.
        foreach (FxFlashKind kind in Enum.GetValues<FxFlashKind>().Append((FxFlashKind)99))
        {
            float radius = FxFlashList.Radius(kind);
            Assert.Equal(FxSimulation.FxLightClass.None, Classify(radius));
        }

        Assert.Equal(5.5f, FxFlashList.Radius(FxFlashKind.Death));
        Assert.False(FireFlicker.TryClassifyRadius(FxFlashList.Radius(FxFlashKind.Death), out _));
    }
}
