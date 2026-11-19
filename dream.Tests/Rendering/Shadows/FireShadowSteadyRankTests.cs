using Dream.Rendering.Core;
using Dream.Rendering.Effects;
using Dream.Rendering.Lighting;
using Dream.Rendering.Shadows;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Rendering.Shadows;

// The fire shadow slots are ranked by the STEADY light (colour / Flicker, radius / RadiusScaleOf(Flicker)), so near-tied fires
// do not swap slots as their flames flicker. The lights are built exactly as the producers build them (position + Offset,
// radius * RadiusScale, colour * Intensity). The synthetic negatives rank by the old, partial rules and must swap.
public class FireShadowSteadyRankTests
{
    private const int Samples = 240;
    private const float BaseRadius = 10f;
    private static readonly Vector3 Focus = new(0f, 0f, -10f);

    // Distance / radius of each fire. The contested pair is 0.75 vs 0.80: windowed falloff is 1.57x apart there, more than the
    // positional wander can close (see Wander_ScoreSwing_IsBounded) but far less than the radius flicker swing (-62% / +76%).
    private static readonly float[] Ratios = { 0.5f, 0.6f, 0.7f, 0.75f, 0.8f };

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
                basePosition + sample.Offset, BaseRadius * sample.RadiusScale, new Vector3(1f, 0.6f, 0.3f) * sample.Intensity, seed, 0, sample.Intensity);
        }

        return lights;
    }

    private static LightList Select(PointLight[] lights, int[] carriers, float[] flicker)
    {
        Matrix4 view = Matrix4.LookAt(Vector3.Zero, -Vector3.UnitZ, Vector3.UnitY);
        Matrix4 projection = Matrix4.CreatePerspectiveFieldOfView(1.6f, 1f, 0.1f, 100f);
        ViewFrustum frustum = ViewFrustum.FromViewProjection(view * projection);
        LightList list = default;
        LightSelection.SelectNearest(lights, ReadOnlySpan<PointLight>.Empty, Vector3.Zero, in frustum, ref list, carriers, flicker);
        return list;
    }

    private static LightList ToList(PointLight[] lights, float[] flicker)
    {
        LightList list = default;
        list.Count = lights.Length;
        for (int i = 0; i < lights.Length; i++)
        {
            list.PositionRadius[i] = new Vector4(lights[i].Position, lights[i].Radius);
            list.Color[i] = new Vector4(lights[i].Color, 0f);
            flicker[i] = lights[i].Flicker;
        }

        return list;
    }

    private static int RealSet(PointLight[] lights)
    {
        float[] flicker = new float[LightList.MaxLights];
        LightList list = ToList(lights, flicker);
        Span<int> chosen = stackalloc int[ShadowSettings.MaxCastingFireCount];
        int count = FireShadowAtlas.SelectSlots(in list, Focus, 4, chosen, flicker);
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
            scored[i] = (FireShadowAtlas.Score(color, l.Position, radius, Focus), i);
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
        HashSet<int> sets = Sets(RealSet);
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

    // The flame's positional wander is accepted, not carried (the list has no offset). Its worst-case effect on a score is
    // bounded here, along the radial direction, at the distance/radius values that matter.
    [Fact]
    public void Wander_ScoreSwing_IsBounded()
    {
        const float bound = 0.25f;
        foreach (float ratio in new[] { 0.6f, 0.7f, 0.8f })
        {
            float d = ratio * BaseRadius;
            float steady = FireShadowAtlas.WindowedFalloff(d, BaseRadius);
            foreach (float delta in new[] { FireFlicker.OffsetLateral, -FireFlicker.OffsetLateral })
            {
                float moved = FireShadowAtlas.WindowedFalloff(d + delta, BaseRadius);
                Assert.True(MathF.Abs(moved - steady) / steady < bound, $"ratio {ratio} delta {delta}: {moved} vs {steady}");
            }
        }

        // And the bound is not vacuous: the 0.75 vs 0.80 pair stays ordered at the worst wander on both.
        float near = FireShadowAtlas.WindowedFalloff((0.75f * BaseRadius) + FireFlicker.OffsetLateral, BaseRadius);
        float far = FireShadowAtlas.WindowedFalloff((0.80f * BaseRadius) - FireFlicker.OffsetLateral, BaseRadius);
        Assert.True(near > far);
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
            lights[i] = new PointLight(new Vector3(0f, 0f, z), 1f, new Vector3(i + 1f, 0f, 0f) * flicker, 0, 100 + i, flicker);
        }

        int[] carriers = new int[LightList.MaxLights];
        float[] flickers = new float[LightList.MaxLights];
        LightList list = Select(lights, carriers, flickers);
        Assert.Equal(LightList.MaxLights, list.Count);
        for (int k = 0; k < list.Count; k++)
        {
            int i = carriers[k] - 100;
            Assert.Equal(0.7f + (i * 0.02f), flickers[k]);
            Assert.Equal((i + 1f) * flickers[k], list.Color[k].X, 3);
        }

        ShadowFrame frame = default;
        FireShadowAtlas.FillSlots(in list, Focus, 4, 1f, ref frame, carriers, flickers);
        Assert.Equal(4, frame.FireCount);
        for (int s = 0; s < frame.FireCount; s++)
        {
            Assert.Equal(carriers[frame.Fires[s].LightIndex], frame.Fires[s].CarrierActorId);
        }
    }
}
