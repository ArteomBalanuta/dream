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

    // Pins "not a bulb": half a second later the flicker has decorrelated (four incommensurate
    // octaves plus gusts); a slow steady pulse would still be strongly correlated.
    [Fact]
    public void Autocorrelation_AtHalfASecond_IsLow()
    {
        float[] values = Series(FireFlickerKind.HeldTorch, 7);
        Assert.True(Autocorrelation(values, 30) < 0.5, $"lag 0.5 s correlation {Autocorrelation(values, 30):F3}");
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
            Assert.InRange(MathF.Abs(s.Offset.X), 0f, 0.02f + 1e-6f);
            Assert.InRange(MathF.Abs(s.Offset.Y), 0f, 0.015f + 1e-6f);
            Assert.InRange(MathF.Abs(s.Offset.Z), 0f, 0.02f + 1e-6f);
        }

        FireFlickerSample lantern = FireFlicker.Sample(FireFlickerKind.HeldLantern, Position, 3.3, 4);
        Assert.Equal(new FireFlickerSample(1f, 1f, Vector3.Zero), lantern);
    }

    [Fact]
    public void ThePositionPhase_IsQuantised_SoAFlameWobbleCannotChangeIt()
    {
        FireFlickerSample a = FireFlicker.Sample(FireFlickerKind.Brazier, Position, 5.5, 0);
        FireFlickerSample b = FireFlicker.Sample(FireFlickerKind.Brazier, Position + a.Offset, 5.5, 0);
        Assert.Equal(a.Intensity, b.Intensity);
        // And different braziers are out of step.
        Assert.NotEqual(
            a.Intensity,
            FireFlicker.Sample(FireFlickerKind.Brazier, Position + new Vector3(16f, 0f, 0f), 5.5, 0).Intensity);
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

    private static FxSimulation.FxLightClass Classify(float radius)
    {
        FxSimulation.TryClassify(radius, out FxSimulation.FxLightClass lightClass);
        return lightClass;
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
