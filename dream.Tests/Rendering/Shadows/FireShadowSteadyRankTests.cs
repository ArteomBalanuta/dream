using Dream.Rendering.Core;
using Dream.Rendering.Effects;
using Dream.Rendering.Lighting;
using Dream.Rendering.Shadows;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Rendering.Shadows;

// The fire shadow slots are ranked by the STEADY colour (Color / PointLight.Flicker), so two fires of similar steady
// brightness do not swap slots as their flames flicker. The synthetic negative ranks by the flickered colour and must flip.
public class FireShadowSteadyRankTests
{
    private const int Fires = 5;
    private const int Samples = 200;
    private static readonly Vector3 Focus = new(0f, 0f, -10f);

    private static PointLight[] Ring(double clock)
    {
        PointLight[] lights = new PointLight[Fires];
        for (int i = 0; i < Fires; i++)
        {
            // Equal distance from the focus; the steady brightness differs by 2% per fire, far below the flicker swing.
            float angle = i * MathF.Tau / Fires;
            Vector3 position = Focus + new Vector3(MathF.Cos(angle) * 3f, 0f, MathF.Sin(angle) * 3f);
            int seed = 1000 + (i * 7919);
            FireFlickerSample sample = FireFlicker.Sample(FireFlickerKind.Brazier, position, clock, seed);
            Vector3 steady = new Vector3(1f, 0.6f, 0.3f) * (1f + (0.02f * i));
            lights[i] = new PointLight(position, 8f, steady * sample.Intensity, seed, 0, sample.Intensity);
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

    private static int IndexOfLight(PointLight[] lights, in LightList list, int listIndex)
    {
        Vector3 p = list.PositionRadius[listIndex].Xyz;
        for (int i = 0; i < lights.Length; i++)
        {
            if (lights[i].Position == p)
            {
                return i;
            }
        }

        throw new InvalidOperationException("list entry matches no input light");
    }

    private static int SetOf(PointLight[] lights, in LightList list, ReadOnlySpan<float> flicker)
    {
        Span<int> chosen = stackalloc int[ShadowSettings.MaxCastingFireCount];
        int count = FireShadowAtlas.SelectSlots(in list, Focus, 4, chosen, flicker);
        int mask = 0;
        for (int s = 0; s < count; s++)
        {
            mask |= 1 << IndexOfLight(lights, in list, chosen[s]);
        }

        return mask;
    }

    [Fact]
    public void SteadyRanking_PicksTheSameSetAtEveryClockSample()
    {
        int[] carriers = new int[LightList.MaxLights];
        float[] flicker = new float[LightList.MaxLights];
        int expected = -1;
        for (int n = 0; n < Samples; n++)
        {
            PointLight[] lights = Ring(n * 0.05);
            LightList list = Select(lights, carriers, flicker);
            Assert.Equal(Fires, list.Count);
            int mask = SetOf(lights, in list, flicker);
            expected = expected < 0 ? mask : expected;
            Assert.Equal(expected, mask);
        }

        // The four brightest steady fires are 1..4.
        Assert.Equal(0b11110, expected);
    }

    [Fact]
    public void RankingByTheFlickeredColour_ChangesTheSet_SyntheticNegative()
    {
        int[] carriers = new int[LightList.MaxLights];
        float[] flicker = new float[LightList.MaxLights];
        HashSet<int> sets = new();
        for (int n = 0; n < Samples; n++)
        {
            PointLight[] lights = Ring(n * 0.05);
            LightList list = Select(lights, carriers, flicker);
            sets.Add(SetOf(lights, in list, ReadOnlySpan<float>.Empty));
        }

        Assert.True(sets.Count > 1, "ranking by the flickered colour should swap slots; the gate cannot fail otherwise");
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
