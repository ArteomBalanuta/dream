using Dream.Rendering.Core;
using Dream.Rendering.Lighting;
using Dream.Tests.Utilities;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Rendering.Lighting;

// SelectNearestFaded: the nearest-16 selection with a stateless fade toward the cut-off distance.
public class LightCutoffFadeTests
{
    private const float Step = 0.05f;

    // `count` lights on the X axis, 3 apart, each with a unique colour so it can be tracked by identity.
    private static PointLight[] Row(int count)
    {
        var lights = new PointLight[count];
        for (int i = 0; i < count; i++)
        {
            lights[i] = new PointLight(new Vector3(i * 3f, 0f, 0f), 10f, new Vector3(i + 1f, 0.5f, 0.25f));
        }

        return lights;
    }

    // Each light's contributed colour by identity (index into `lights`), zero when not selected.
    private static Vector3[] Contributions(PointLight[] lights, in LightList list)
    {
        var result = new Vector3[lights.Length];
        for (int slot = 0; slot < list.Count; slot++)
        {
            Vector3 position = list.PositionRadius[slot].Xyz;
            for (int i = 0; i < lights.Length; i++)
            {
                if (lights[i].Position == position)
                {
                    result[i] = list.Color[slot].Xyz;
                }
            }
        }

        return result;
    }

    // The largest amount by which any light's colour change between steps exceeds the allowed bound.
    private static float WorstExcess(PointLight[] lights, bool faded)
    {
        float worstExcess = float.MinValue;
        Vector3[]? previous = null;
        for (float x = 4f; x < 50f; x += Step)
        {
            LightList list = default;
            var camera = new Vector3(x, 0f, 0f);
            if (faded)
            {
                LightSelection.SelectNearestFaded(lights, default, camera, default(ViewFrustum), ref list);
            }
            else
            {
                LightSelection.SelectNearest(lights, default, camera, default(ViewFrustum), ref list);
            }

            Vector3[] now = Contributions(lights, in list);
            if (previous != null)
            {
                for (int i = 0; i < lights.Length; i++)
                {
                    // The light's distance moves by <= Step and so does the cut-off distance: the weight moves by <= 2 Step / band.
                    float allowed = 2f * Step / LightSelection.CutoffFadeBand * lights[i].Color.Length + 1e-3f;
                    worstExcess = MathF.Max(worstExcess, (now[i] - previous[i]).Length - allowed);
                }
            }

            previous = now;
        }

        return worstExcess;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(16)]
    public void With_Sixteen_Or_Fewer_Candidates_The_Output_Equals_SelectNearest(int count)
    {
        PointLight[] lights = Row(count);
        var camera = new Vector3(7f, 1f, 2f);
        LightList plain = default;
        LightList faded = default;
        LightSelection.SelectNearest(lights, default, camera, default(ViewFrustum), ref plain);
        LightSelection.SelectNearestFaded(lights, default, camera, default(ViewFrustum), ref faded);

        Assert.Equal(plain.Count, faded.Count);
        for (int i = 0; i < plain.Count; i++)
        {
            Assert.Equal(plain.Color[i], faded.Color[i]);
            Assert.Equal(plain.PositionRadius[i], faded.PositionRadius[i]);
        }
    }

    [Fact]
    public void Moving_Through_A_Rank_Swap_Never_Pops_A_Light()
    {
        Assert.True(WorstExcess(Row(20), faded: true) <= 0f);
    }

    [Fact]
    public void Synthetic_Negative_Plain_SelectNearest_Breaks_The_Same_Bound()
    {
        Assert.True(WorstExcess(Row(20), faded: false) > 0.5f);
    }

    [Fact]
    public void Weights_Are_One_Below_The_Band_And_Zero_At_The_Cutoff()
    {
        float cutoff = 10f;
        Assert.Equal(1f, LightSelection.CutoffWeight(cutoff - LightSelection.CutoffFadeBand, cutoff));
        Assert.Equal(1f, LightSelection.CutoffWeight(0f, cutoff));
        Assert.Equal(0f, LightSelection.CutoffWeight(cutoff, cutoff));
        Assert.Equal(0f, LightSelection.CutoffWeight(cutoff + 1f, cutoff));
        Assert.Equal(0.5f, LightSelection.CutoffWeight(cutoff - LightSelection.CutoffFadeBand / 2f, cutoff), 5);
        Assert.Equal(1f, LightSelection.CutoffWeight(123f, float.PositiveInfinity));
    }

    [Fact]
    public void Lights_Well_Inside_The_Cutoff_Keep_Full_Colour()
    {
        PointLight[] lights = Row(20);
        LightList list = default;
        LightSelection.SelectNearestFaded(lights, default, Vector3.Zero, default(ViewFrustum), ref list);

        Assert.Equal(LightList.MaxLights, list.Count);
        Assert.Equal(lights[0].Color, list.Color[0].Xyz);
        // The cut-off is light 16 (x = 48); light 15 (x = 45) is 3 inside it, more than the band.
        Assert.Equal(lights[15].Color, list.Color[15].Xyz);
    }

    [Fact]
    public void A_Light_Within_The_Band_Of_The_Cutoff_Is_Dimmed()
    {
        var lights = new PointLight[17];
        for (int i = 0; i < 17; i++)
        {
            lights[i] = new PointLight(new Vector3(i * 1f, 0f, 0f), 5f, Vector3.One);
        }

        LightList list = default;
        LightSelection.SelectNearestFaded(lights, default, Vector3.Zero, default(ViewFrustum), ref list);

        // Cut-off at x = 16: the light at 15 is 1 inside it (weight 0.5), the one at 14 a full band inside (weight 1).
        Assert.Equal(0.5f, list.Color[15].X, 5);
        Assert.Equal(1f, list.Color[14].X, 5);
        Assert.Equal(0f, list.Color[15].W);
    }

    [Fact]
    public void Running_It_Twice_Gives_The_Same_List()
    {
        PointLight[] lights = Row(20);
        var camera = new Vector3(21.3f, 0f, 0f);
        LightList a = default;
        LightList b = default;
        LightSelection.SelectNearestFaded(lights, default, camera, default(ViewFrustum), ref a);
        LightSelection.SelectNearestFaded(lights, default, camera, default(ViewFrustum), ref a);
        LightSelection.SelectNearestFaded(lights, default, camera, default(ViewFrustum), ref b);

        Assert.Equal(b.Count, a.Count);
        for (int i = 0; i < a.Count; i++)
        {
            Assert.Equal(b.Color[i], a.Color[i]);
            Assert.Equal(b.PositionRadius[i], a.PositionRadius[i]);
        }
    }

    [Fact]
    public void Selecting_Allocates_Nothing()
    {
        PointLight[] lights = Row(24);
        PointLight[] extra = Row(3);
        LightList list = default;
        int[] carriers = new int[LightList.MaxLights];
        ViewFrustum frustum = default;
        var camera = new Vector3(20f, 0f, 0f);

        long bytes = AllocationAssert.AtMost(0, () =>
        {
            LightSelection.SelectNearestFaded(lights, extra, camera, in frustum, ref list, carriers);
        });

        Assert.Equal(0, bytes);
    }
}
