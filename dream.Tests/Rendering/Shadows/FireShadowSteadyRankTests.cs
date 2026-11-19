using Dream.Rendering.Core;
using Dream.Rendering.Effects;
using Dream.Rendering.Lighting;
using Dream.Rendering.Shadows;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Rendering.Shadows;

// The fire shadow slots are ranked by the STEADY light (colour / Flicker, radius / RadiusScaleOf(Flicker), resting position
// = Position - Wander), so near-tied fires
// do not swap slots as their flames flicker. The lights are built exactly as the producers build them (position + Offset,
// radius * RadiusScale, colour * Intensity). The synthetic negatives rank by the old, partial rules and must swap.
public class FireShadowSteadyRankTests
{
    private const int Samples = 240;
    private const float BaseRadius = 10f;
    private static readonly Vector3 Focus = new(0f, 0f, -10f);

    // Distance / radius of each fire. The contested pair is 0.75 vs 0.755: their resting scores are within 5% of each other,
    // far inside the swing the positional wander (<= 0.12 m) and the radius flicker (-62% / +76%) put on an unsteady score.
    private static readonly float[] Ratios = { 0.5f, 0.6f, 0.7f, 0.75f, 0.755f };

    private static PointLight[] Fires(double clock)
    {
        PointLight[] lights = new PointLight[Ratios.Length];
        for (int i = 0; i < Ratios.Length; i++)
        {
            float angle = i * MathF.Tau / Ratios.Length;
            float d = Ratios[i] * BaseRadius;
            Vector3 basePosition = Focus + new Vector3(MathF.Cos(angle) * d, 0f, MathF.Sin(angle) * d);
            int seed = FireFlicker.SeedFor(basePosition);
            FireFlickerSample sample = FireFlicker.Sample(FireFlickerKind.Brazier, basePosition, clock, seed);
            lights[i] = new PointLight(
                basePosition + sample.Offset, BaseRadius * sample.RadiusScale, new Vector3(1f, 0.6f, 0.3f) * sample.Intensity, seed, 0, sample.Intensity, sample.Offset);
        }

        return lights;
    }

    private static LightList Select(PointLight[] lights, int[] carriers, float[] flicker, Vector3[] wander)
    {
        Matrix4 view = Matrix4.LookAt(Vector3.Zero, -Vector3.UnitZ, Vector3.UnitY);
        Matrix4 projection = Matrix4.CreatePerspectiveFieldOfView(1.6f, 1f, 0.1f, 100f);
        ViewFrustum frustum = ViewFrustum.FromViewProjection(view * projection);
        LightList list = default;
        LightSelection.SelectNearest(lights, ReadOnlySpan<PointLight>.Empty, Vector3.Zero, in frustum, ref list, carriers, flicker, wander);
        return list;
    }

    private static LightList ToList(PointLight[] lights, float[] flicker, Vector3[] wander)
    {
        LightList list = default;
        list.Count = lights.Length;
        for (int i = 0; i < lights.Length; i++)
        {
            list.PositionRadius[i] = new Vector4(lights[i].Position, lights[i].Radius);
            list.Color[i] = new Vector4(lights[i].Color, 0f);
            flicker[i] = lights[i].Flicker;
            wander[i] = lights[i].Wander;
        }

        return list;
    }

    private static int RealSet(PointLight[] lights, bool withWander = true)
    {
        float[] flicker = new float[LightList.MaxLights];
        Vector3[] wander = new Vector3[LightList.MaxLights];
        LightList list = ToList(lights, flicker, wander);
        Span<int> chosen = stackalloc int[ShadowSettings.MaxCastingFireCount];
        int count = FireShadowAtlas.SelectSlots(in list, Focus, 4, chosen, flicker, withWander ? wander : default);
        int mask = 0;
        for (int s = 0; s < count; s++)
        {
            mask |= 1 << chosen[s];
        }

        return mask;
    }

    // The same ranking written out, with each steady-ing step switchable: the old fix divided the colour only.
    private static int SetBy(PointLight[] lights, bool steadyColour, bool steadyRadius)
    {
        (float Score, int Index)[] scored = new (float, int)[lights.Length];
        for (int i = 0; i < lights.Length; i++)
        {
            PointLight l = lights[i];
            Vector3 color = steadyColour ? l.Color / l.Flicker : l.Color;
            float radius = steadyRadius ? l.Radius / FireFlicker.RadiusScaleOf(l.Flicker) : l.Radius;
            scored[i] = (FireShadowAtlas.Score(color, l.Position - l.Wander, radius, Focus), i);
        }

        Array.Sort(scored, (x, y) => y.Score.CompareTo(x.Score));
        int mask = 0;
        for (int k = 0; k < 4; k++)
        {
            mask |= 1 << scored[k].Index;
        }

        return mask;
    }

    private static HashSet<int> Sets(Func<PointLight[], int> pick)
    {
        HashSet<int> sets = new();
        for (int n = 0; n < Samples; n++)
        {
            sets.Add(pick(Fires(n * 0.05)));
        }

        return sets;
    }

    [Fact]
    public void RadiusScaleOf_IsTheFormulaSampleUses()
    {
        for (int n = 0; n < Samples; n++)
        {
            FireFlickerSample sample = FireFlicker.Sample(FireFlickerKind.Brazier, Focus, n * 0.05, 4242);
            Assert.Equal(sample.RadiusScale, FireFlicker.RadiusScaleOf(sample.Intensity));
        }

        Assert.Equal(1f, FireFlicker.RadiusScaleOf(0f));
        Assert.Equal(1f, FireFlicker.RadiusScaleOf(float.NaN));
        Assert.Equal(1f, FireFlicker.RadiusScaleOf(1f));
    }

    [Fact]
    public void SteadyRanking_PicksTheSameSetAtEveryClockSample()
    {
        HashSet<int> sets = Sets(l => RealSet(l));
        Assert.Single(sets);
        // The four fires nearest their own centre: ratios 0.5, 0.6, 0.7, 0.75.
        Assert.Equal(0b01111, sets.Single());
    }

    [Fact]
    public void RankingByTheSteadyColourOnly_ChangesTheSet_SyntheticNegative()
    {
        Assert.True(Sets(l => SetBy(l, steadyColour: true, steadyRadius: false)).Count > 1,
            "the old fix (colour only) must still swap near-tied far fires; the gate cannot fail otherwise");
    }

    [Fact]
    public void RankingByTheFlickeredLight_ChangesTheSet_SyntheticNegative()
    {
        Assert.True(Sets(l => SetBy(l, steadyColour: false, steadyRadius: false)).Count > 1,
            "no division at all must swap slots; the gate cannot fail otherwise");
    }

    [Fact]
    public void SteadyHarness_AgreesWithSelectSlots()
    {
        for (int n = 0; n < Samples; n++)
        {
            PointLight[] lights = Fires(n * 0.05);
            Assert.Equal(SetBy(lights, true, true), RealSet(lights));
        }
    }

    [Fact]
    public void RankingWithoutTheWanderSpan_ChangesTheSet_SyntheticNegative()
    {
        Assert.True(Sets(l => RealSet(l, withWander: false)).Count > 1,
            "ranking from the wandering position must swap a near tie; the gate cannot fail otherwise");
    }

    [Fact]
    public void NearTiedFires_AreWithinFivePercentAtRest()
    {
        float a = FireShadowAtlas.WindowedFalloff(0.75f * BaseRadius, BaseRadius);
        float b = FireShadowAtlas.WindowedFalloff(0.755f * BaseRadius, BaseRadius);
        Assert.True(MathF.Abs(a - b) / a < 0.05f, $"{a} vs {b}");
    }

    [Fact]
    public void FlickerOfOne_RanksExactlyAsBefore()
    {
        float[] ones = new float[LightList.MaxLights];
        Array.Fill(ones, 1f);
        int[] a = new int[ShadowSettings.MaxCastingFireCount];
        int[] b = new int[ShadowSettings.MaxCastingFireCount];
        LightList list = default;
        list.Count = 6;
        for (int i = 0; i < 6; i++)
        {
            list.PositionRadius[i] = new Vector4(Focus + new Vector3(i * 0.5f, 0f, 0f), 8f);
            list.Color[i] = new Vector4(1f + ((i * 37 % 6) * 0.1f), 0.5f, 0.2f, 0f);
        }

        int na = FireShadowAtlas.SelectSlots(in list, Focus, 4, a);
        int nb = FireShadowAtlas.SelectSlots(in list, Focus, 4, b, ones);
        Assert.Equal(na, nb);
        Assert.Equal(a, b);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(-1f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void BadFlicker_RanksAsSteady(float bad)
    {
        float[] flicker = new float[LightList.MaxLights];
        Array.Fill(flicker, bad);
        int[] a = new int[ShadowSettings.MaxCastingFireCount];
        int[] b = new int[ShadowSettings.MaxCastingFireCount];
        LightList list = default;
        list.Count = 3;
        for (int i = 0; i < 3; i++)
        {
            list.PositionRadius[i] = new Vector4(Focus, 8f);
            list.Color[i] = new Vector4(1f + i, 1f, 1f, 0f);
        }

        Assert.Equal(FireShadowAtlas.SelectSlots(in list, Focus, 4, a), FireShadowAtlas.SelectSlots(in list, Focus, 4, b, flicker));
        // Infinity is "not finite" too: it must not zero every score into an index-order tie.
        Assert.Equal(a, b);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void BadWander_RanksFromTheLivePosition(float bad)
    {
        Vector3[] wander = new Vector3[LightList.MaxLights];
        Array.Fill(wander, new Vector3(0f, bad, 0f));
        int[] a = new int[ShadowSettings.MaxCastingFireCount];
        int[] b = new int[ShadowSettings.MaxCastingFireCount];
        LightList list = default;
        list.Count = 3;
        for (int i = 0; i < 3; i++)
        {
            // Nearer lights last, so a ranking that works (rather than skipping every NaN score) reverses the index order.
            list.PositionRadius[i] = new Vector4(Focus + new Vector3(0f, 0f, 1f + (2f * (2 - i))), 8f);
            list.Color[i] = new Vector4(1f, 1f, 1f, 0f);
        }

        int expected = FireShadowAtlas.SelectSlots(in list, Focus, 4, a);
        Assert.Equal(3, expected);
        Assert.Equal(expected, FireShadowAtlas.SelectSlots(in list, Focus, 4, b, default, wander));
        Assert.Equal(a, b);
    }

    [Fact]
    public void ParallelArrays_StayAlignedThroughInsertions()
    {
        const int candidates = 24;
        PointLight[] lights = new PointLight[candidates];
        for (int i = 0; i < candidates; i++)
        {
            // Farther lights first, so every insertion shifts the kept ones.
            float z = -(2f + ((candidates - i) * 0.5f));
            float flicker = 0.7f + (i * 0.02f);
            lights[i] = new PointLight(new Vector3(0f, 0f, z), 1f, new Vector3(i + 1f, 0f, 0f) * flicker, 0, 100 + i, flicker, new Vector3(i, 0f, 0f));
        }

        int[] carriers = new int[LightList.MaxLights];
        float[] flickers = new float[LightList.MaxLights];
        Vector3[] wanders = new Vector3[LightList.MaxLights];
        LightList list = Select(lights, carriers, flickers, wanders);
        Assert.Equal(LightList.MaxLights, list.Count);
        for (int k = 0; k < list.Count; k++)
        {
            int i = carriers[k] - 100;
            Assert.Equal(0.7f + (i * 0.02f), flickers[k]);
            Assert.Equal((i + 1f) * flickers[k], list.Color[k].X, 3);
            Assert.Equal(new Vector3(i, 0f, 0f), wanders[k]);
        }

        ShadowFrame frame = default;
        FireShadowAtlas.FillSlots(in list, Focus, 4, 1f, ref frame, carriers, flickers, wanders);
        Assert.Equal(4, frame.FireCount);
        for (int s = 0; s < frame.FireCount; s++)
        {
            Assert.Equal(carriers[frame.Fires[s].LightIndex], frame.Fires[s].CarrierActorId);
            // The slot keeps the LIVE (wandering) position and far plane, not the resting one.
            Vector4 live = list.PositionRadius[frame.Fires[s].LightIndex];
            Assert.Equal(live.Xyz, frame.Fires[s].Position);
            Assert.Equal(FireShadowAtlas.FarPlane(live.W), frame.Fires[s].Far, 5);
        }
    }
}
