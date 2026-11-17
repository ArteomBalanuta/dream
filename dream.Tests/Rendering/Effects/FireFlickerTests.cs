using OpenTK.Mathematics;
using Xunit;
using Dream.Rendering.Effects;
using Dream.Rendering.Lighting;
using Dream.Rendering.World;

namespace Dream.Tests.Rendering.Effects;

public class FireFlickerTests
{
    private const int Samples = 20000;
    private static readonly Vector3 Position = new(12.5f, 3f, -7.5f);

    private static float[] Series(FireFlickerKind kind, int seed, double step = 1.0 / 60.0)
    {
        float[] values = new float[Samples];
        for (int i = 0; i < Samples; i++)
        {
            values[i] = FireFlicker.Sample(kind, Position, i * step, seed).Intensity;
        }

        return values;
    }

    [Fact]
    public void IsDeterministic()
    {
        foreach (FireFlickerKind kind in Enum.GetValues<FireFlickerKind>())
        {
            Assert.Equal(FireFlicker.Sample(kind, Position, 12.34, 9), FireFlicker.Sample(kind, Position, 12.34, 9));
            Assert.Equal(FireFlicker.Sample(kind, Position, 12.34, 0), FireFlicker.Sample(kind, Position, 12.34, 0));
        }
    }

    [Fact]
    public void OctaveWeights_SumToOne()
    {
        Assert.Equal(1.0, (double)FireFlicker.Weight0 + FireFlicker.Weight1 + FireFlicker.Weight2 + FireFlicker.Weight3, 6);
        Assert.Equal(1f, FireFlicker.OctaveWeightSum, 6);
    }

    [Theory]
    [InlineData(FireFlickerKind.HeldTorch)]
    [InlineData(FireFlickerKind.Brazier)]
    [InlineData(FireFlickerKind.Lamp)]
    public void Intensity_StaysWithinTheAmplitude_AndReallyMoves(FireFlickerKind kind)
    {
        float amplitude = FireFlicker.Amplitude(kind);
        Assert.Equal(0.25f, amplitude);
        float[] values = Series(kind, kind == FireFlickerKind.HeldTorch ? 7 : 0);
        Assert.InRange(values.Min(), 1f - amplitude - 1e-6f, 1f + amplitude);
        Assert.InRange(values.Max(), 1f, 1f + amplitude + 1e-6f);
        Assert.True(values.Max() - values.Min() > 0.30f, $"{kind}: range {values.Max() - values.Min():F3}");
    }

    // Pins "not a bulb" with two metrics that the previous torch signal (1 + 0.08 * (0.6 sin 11.3t +
    // 0.4 sin 17.9t)) genuinely fails and the shipped one passes. Measured over 20000 frames at 60 Hz
    // (seeds 7, 1, 12345; the old signal is seed-independent to three decimals):
    //                                  old two-sine    shipped
    //   CV of peak-to-peak intervals      0.23           0.41 - 0.46     (regular vs irregular beat)
    //   max autocorrelation, lag 1-10 s   0.978          0.80 - 0.83     (quasi-periodic recurrence vs none)
    // (Lag-0.5 s autocorrelation does NOT separate them: 0.284 old, 0.06 - 0.07 new, both under 0.5.)
    [Theory]
    [InlineData(7)]
    [InlineData(1)]
    [InlineData(12345)]
    public void TheFlicker_IsIrregular_AndDoesNotRecur(int seed)
    {
        float[] shipped = Series(FireFlickerKind.HeldTorch, seed);
        Assert.True(Autocorrelation(shipped, 30) < 0.5, $"lag 0.5 s correlation {Autocorrelation(shipped, 30):F3}");

        float[] old = new float[Samples];
        double phase = seed * 0.6180339887;
        for (int i = 0; i < Samples; i++)
        {
            double t = i / 60.0;
            old[i] = (float)(1 + (0.08 * ((0.6 * Math.Sin((t * 11.3) + phase)) + (0.4 * Math.Sin((t * 17.9) + (phase * 2.3))))));
        }

        double shippedCv = PeakIntervalCv(shipped);
        double oldCv = PeakIntervalCv(old);
        Assert.True(oldCv < 0.30, $"old signal CV {oldCv:F3}");
        Assert.True(shippedCv > 0.35, $"shipped CV {shippedCv:F3}");

        double shippedRecurrence = MaxAutocorrelation(shipped, 60, 600);
        double oldRecurrence = MaxAutocorrelation(old, 60, 600);
        Assert.True(oldRecurrence > 0.95, $"old signal recurrence {oldRecurrence:F3}");
        Assert.True(shippedRecurrence < 0.90, $"shipped recurrence {shippedRecurrence:F3}");
    }

    [Fact]
    public void Gusts_OnlyReduce_AndNeverExceedThePeak()
    {
        foreach (int seed in new[] { 0, 1, 7, 12345 })
        {
            foreach (float value in Series(FireFlickerKind.HeldTorch, seed, 1.0 / 97.0))
            {
                Assert.True(value <= 1f + FireFlicker.TorchAmplitude + 1e-6f);
                Assert.True(value >= 1f - FireFlicker.TorchAmplitude - 1e-6f);
            }
        }

        // A gust happens: some sample is lower than the same octaves alone would give is hard to
        // observe directly, but over a minute at least one window fires (30 % chance each).
        bool sawGust = false;
        for (int window = 0; window < 60 && !sawGust; window++)
        {
            for (int k = 0; k < 40; k++)
            {
                double t = (window * 1.3) + (k * 0.0325);
                float with = FireFlicker.Sample(FireFlickerKind.HeldTorch, Position, t, 3).Intensity;
                if (with <= 1f - FireFlicker.TorchAmplitude + 1e-6f)
                {
                    sawGust = true;
                    break;
                }
            }
        }

        Assert.True(sawGust, "gusts should reach the floor now and then");
    }

    [Fact]
    public void RadiusScale_Follows_AndTheLanternIsSteady()
    {
        for (int i = 0; i < Samples; i++)
        {
            FireFlickerSample s = FireFlicker.Sample(FireFlickerKind.HeldTorch, Position, i / 60.0, 4);
            Assert.Equal(1f + (0.35f * (s.Intensity - 1f)), s.RadiusScale, 5);
            Assert.InRange(s.RadiusScale, 0.9124f, 1.0876f);
            Assert.InRange(MathF.Abs(s.Offset.X), 0f, FireFlicker.OffsetLateral + 1e-6f);
            Assert.InRange(MathF.Abs(s.Offset.Y), 0f, FireFlicker.OffsetVertical + 1e-6f);
            Assert.InRange(MathF.Abs(s.Offset.Z), 0f, FireFlicker.OffsetLateral + 1e-6f);
        }

        FireFlickerSample lantern = FireFlicker.Sample(FireFlickerKind.HeldLantern, Position, 3.3, 4);
        Assert.Equal(new FireFlickerSample(1f, 1f, Vector3.Zero), lantern);
    }

    // The seed-0 FALLBACK, which is what a light reaching a consumer without a seed still gets: the
    // phase is the position quantised to PhaseGrid, so moving within the cell cannot change it.
    [Fact]
    public void ThePositionPhase_IsQuantised_SoAFlameWobbleCannotChangeIt()
    {
        FireFlickerSample a = FireFlicker.Sample(FireFlickerKind.Brazier, Position, 5.5, 0);
        // Position sits at the centre of its cell on every axis, so the wobble stays inside it.
        FireFlickerSample b = FireFlicker.Sample(FireFlickerKind.Brazier, Position + a.Offset, 5.5, 0);
        Assert.Equal(a.Intensity, b.Intensity);
        // And different braziers are out of step.
        Assert.NotEqual(
            a.Intensity,
            FireFlicker.Sample(FireFlickerKind.Brazier, Position + new Vector3(16f, 0f, 0f), 5.5, 0).Intensity);
    }

    // The seed, not the position, is what keeps the halo in step with the light. The halo only has the
    // light, whose position already carries the offset; the carried seed makes the question of which
    // phase cell that position lands in irrelevant. Cell boundaries sit at 0.5k - 0.25, and the offset
    // now crosses them freely, so this is the mechanism and not a happy accident of a small offset.
    [Theory]
    [InlineData(FireFlickerKind.Lamp)]
    [InlineData(FireFlickerKind.Brazier)]
    public void ThePhase_IsStableUnderTheOffset_AcrossEveryCellBoundary(FireFlickerKind kind)
    {
        float radius = kind == FireFlickerKind.Lamp ? LampLightIndex.LampRadius : PlacedLightIndex.BrazierRadius;
        int checkedSamples = 0;
        int crossedACellBoundary = 0;
        for (int axis = 0; axis < 3; axis++)
        {
            // From 1.80 to 3.30 in 0.001 steps: crosses the boundaries at 2.25 and 2.75 (and 3.25), and
            // lands exactly on them.
            for (int step = 0; step <= 1500; step++)
            {
                Vector3 position = new(7.5f, 3.5f, -4.5f);
                float value = 1.8f + (step * 0.001f);
                if (axis == 0) position.X = value; else if (axis == 1) position.Y = value; else position.Z = value;

                int seed = FireFlicker.SeedFor(position);
                Assert.NotEqual(0, seed);
                for (int k = 0; k < 24; k++)
                {
                    double t = (k * 0.173) + (step * 0.0007);
                    FireFlickerSample s = FireFlicker.Sample(kind, position, t, seed);
                    PointLight light = new(position + s.Offset, radius * s.RadiusScale, Vector3.One, seed);
                    Assert.Equal(s.Intensity, FireFlicker.IntensityOf(in light, t));
                    if (Quantise(position + s.Offset) != Quantise(position))
                    {
                        crossedACellBoundary++;
                    }

                    checkedSamples++;
                }
            }
        }

        Assert.True(checkedSamples > 100000);
        // The offset really does leave the base position's phase cell now, so the agreement above is
        // the seed's doing and not a wobble too small to matter.
        Assert.True(crossedACellBoundary > 1000, $"only {crossedACellBoundary} samples left their cell");
    }

    private static (int X, int Y, int Z) Quantise(Vector3 v) => (
        (int)MathF.Floor((v.X / FireFlicker.PhaseGrid) + 0.5f),
        (int)MathF.Floor((v.Y / FireFlicker.PhaseGrid) + 0.5f),
        (int)MathF.Floor((v.Z / FireFlicker.PhaseGrid) + 0.5f));

    [Fact]
    public void AHaloOnACellBoundary_DoesNotJitterFrameToFrame()
    {
        // Exactly on a boundary on every axis (2.25 = 0.5*5 - 0.25), 4 s at 60 fps: the light's own
        // flicker and the halo's read-back must be the same series, i.e. no frame flips to another phase.
        Vector3 position = new(2.25f, 2.25f, 2.25f);
        int seed = FireFlicker.SeedFor(position);
        for (int frame = 0; frame < 240; frame++)
        {
            double t = frame / 60.0;
            FireFlickerSample s = FireFlicker.Sample(FireFlickerKind.Lamp, position, t, seed);
            PointLight light = new(position + s.Offset, LampLightIndex.LampRadius * s.RadiusScale, Vector3.One, seed);
            Assert.Equal(s.Intensity, FireFlicker.IntensityOf(in light, t));
        }
    }

    // The producers all carry the seed; a light that reaches a consumer without one is the exception.
    [Fact]
    public void EveryProducer_CarriesTheSeedItSampledWith()
    {
        Vector3 centre = new(6.5f, 2.5f, -3.5f);
        PointLight lamp = LampLightIndex.CreateLight(centre, 2.75);
        Assert.Equal(FireFlicker.SeedFor(centre), lamp.FlickerSeed);
        Assert.Equal(FireFlicker.Sample(FireFlickerKind.Lamp, centre, 2.75, lamp.FlickerSeed).Intensity, FireFlicker.IntensityOf(in lamp, 2.75));

        // No clock, no flicker, and nothing to stay in step with.
        Assert.Equal(0, LampLightIndex.CreateLight(centre).FlickerSeed);

        Assert.True(HeldLightCatalog.TryCreate(Dream.Simulation.Inventory.HeldLightKind.Torch, centre, 2.75, 21, out PointLight torch));
        Assert.Equal(21, torch.FlickerSeed);
    }

    // FxSimulation classifies a light by its radius, so a flickering radius must stay in its class.
    [Fact]
    public void TryClassify_KeepsEveryFlickeringRadiusInItsClass_AndTheBandsDoNotOverlap()
    {
        float torchMin = float.MaxValue, torchMax = float.MinValue, lampMin = float.MaxValue, lampMax = float.MinValue;
        for (int i = 0; i < Samples; i++)
        {
            double t = i / 60.0;
            float torch = HeldLightCatalog.TorchRadius * FireFlicker.Sample(FireFlickerKind.HeldTorch, Position, t, 3).RadiusScale;
            float brazier = PlacedLightIndex.BrazierRadius * FireFlicker.Sample(FireFlickerKind.Brazier, Position, t, 0).RadiusScale;
            float lamp = LampLightIndex.LampRadius * FireFlicker.Sample(FireFlickerKind.Lamp, Position, t, 0).RadiusScale;
            Assert.Equal(FxSimulation.FxLightClass.Torch, Classify(torch));
            Assert.Equal(FxSimulation.FxLightClass.Lamp, Classify(brazier));
            Assert.Equal(FxSimulation.FxLightClass.Lamp, Classify(lamp));
            torchMin = MathF.Min(torchMin, torch);
            torchMax = MathF.Max(torchMax, torch);
            lampMin = MathF.Min(lampMin, brazier);
            lampMax = MathF.Max(lampMax, brazier);
        }

        Assert.True(torchMax < lampMin, "the torch and brazier radius bands overlap");
        Assert.Equal(FxSimulation.FxLightClass.Lantern, Classify(HeldLightCatalog.LanternRadius));
        Assert.Equal(FxSimulation.FxLightClass.None, Classify(5.5f));
        Assert.Equal(FxSimulation.FxLightClass.None, Classify(3f));
        Assert.Equal(FxSimulation.FxLightClass.None, Classify(12f));
        Assert.InRange(torchMin, 7.29f, 7.5f);
        Assert.InRange(torchMax, 8.5f, 8.71f);
    }

    [Fact]
    public void PublicContract_MapsAPointLightToItsKindAndIntensity()
    {
        Assert.True(FireFlicker.TryClassifyRadius(7.4f, out FireFlickerKind torch));
        Assert.Equal(FireFlickerKind.HeldTorch, torch);
        Assert.True(FireFlicker.TryClassifyRadius(9.3f, out FireFlickerKind lamp));
        Assert.Equal(FireFlickerKind.Lamp, lamp);
        Assert.False(FireFlicker.TryClassifyRadius(5.5f, out _));

        // A held torch's intensity is read back from its colour: exactly the light's own.
        Assert.True(HeldLightCatalog.TryCreate(Dream.Simulation.Inventory.HeldLightKind.Torch, Position, 4.2, 9, out PointLight held));
        Assert.Equal(FireFlicker.Sample(FireFlickerKind.HeldTorch, Position, 4.2, 9).Intensity, FireFlicker.IntensityOf(in held, 4.2), 4);

        // A placed brazier re-derives it from its position and the clock.
        PointLight brazier = new(Position, PlacedLightIndex.BrazierRadius, PlacedLightIndex.BrazierColor);
        Assert.Equal(FireFlicker.Sample(FireFlickerKind.Brazier, Position, 4.2, 0).Intensity, FireFlicker.IntensityOf(in brazier, 4.2));
    }

    // The halo's size and the light's intensity are both driven by the one clock value the frame
    // carries (FxFrameInputs.ClockSeconds = the presentation clock the lights flicker on).
    [Fact]
    public void ABrazierHalo_AndItsLight_AreDrivenByTheSameClock()
    {
        Vector3 centre = new(10.5f, 3.5f, 5.5f);
        Vector3 camera = new(10f, 3f, 12f);
        FxViewBasis basis = new(Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ);

        (float HalfSize, float LightIntensity) Frame(double clock)
        {
            PointLight light = LampLightIndex.CreateLight(centre, clock);
            FxSimulation simulation = new(1u);
            FxFrameInputs inputs = new(1f / 60f, camera, -Vector3.UnitZ, camera, 1f, basis, clock);
            simulation.Advance(in inputs, [light]);
            FxInstance[] instances = new FxInstance[FxLimits.MaxInstances];
            int count = simulation.WriteInstances(instances);
            FxInstance halo = instances.Take(count).Single(i => i.Frame == (float)FxFrame.Halo);
            return (halo.HalfSize, light.Color.X / LampLightIndex.LampColor.X);
        }

        (float halfA, float lightA) = Frame(3.1);
        (float halfB, float lightB) = Frame(3.1);
        Assert.Equal(halfA, halfB);
        Assert.Equal(lightA, lightB);

        // The halo size is a function of the light's own intensity: same clock, same flicker.
        Assert.Equal(1.25f * (1f + (0.35f * (lightA - 1f))), halfA, 4);

        bool moved = false;
        for (double clock = 3.2; clock < 6.0; clock += 0.13)
        {
            (float half, float light) = Frame(clock);
            Assert.Equal(1.25f * (1f + (0.35f * (light - 1f))), half, 4);
            moved |= MathF.Abs(half - halfA) > 1e-3f && MathF.Abs(light - lightA) > 1e-3f;
        }

        Assert.True(moved, "advancing the clock must change halo size and light intensity together");
    }

    private static FxSimulation.FxLightClass Classify(float radius)
    {
        FxSimulation.TryClassify(radius, out FxSimulation.FxLightClass lightClass);
        return lightClass;
    }

    private static double MaxAutocorrelation(float[] values, int fromLag, int toLag)
    {
        double best = double.MinValue;
        for (int lag = fromLag; lag <= toLag; lag++)
        {
            best = Math.Max(best, Autocorrelation(values, lag));
        }

        return best;
    }

    // Coefficient of variation of the gaps between successive local maxima.
    private static double PeakIntervalCv(float[] values)
    {
        List<double> gaps = [];
        int previous = -1;
        for (int i = 1; i < values.Length - 1; i++)
        {
            if (values[i] > values[i - 1] && values[i] >= values[i + 1])
            {
                if (previous >= 0)
                {
                    gaps.Add((i - previous) / 60.0);
                }

                previous = i;
            }
        }

        double mean = gaps.Average();
        return Math.Sqrt(gaps.Sum(g => (g - mean) * (g - mean)) / gaps.Count) / mean;
    }

    private static double Autocorrelation(float[] values, int lag)
    {
        double mean = values.Average();
        double variance = 0;
        double covariance = 0;
        for (int i = 0; i < values.Length; i++)
        {
            double d = values[i] - mean;
            variance += d * d;
            if (i + lag < values.Length)
            {
                covariance += d * (values[i + lag] - mean);
            }
        }

        return covariance / variance;
    }
}
