using Dream.Rendering.Lighting;
using Dream.Rendering.Sky;
using Dream.Tests.Utilities;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Rendering.Sky;

public class SkyModelTests
{
    private const float Noon = 0.5f;
    private const float Midnight = 0f;
    private const int Samples = 10_000;

    private static float Hour(float h) => h / 24f;

    private static float Luminance(Vector3 c) => 0.2126f * c.X + 0.7152f * c.Y + 0.0722f * c.Z;

    // What lighting.glsl gives an up-facing normal: ambient sky plus sun and moon direct light, then exposure.
    private static float UpFacingLuminance(in FrameLighting l)
    {
        Vector3 light = l.AmbientSky.Xyz
            + MathF.Max(l.SunDirection.Y, 0f) * l.SunDirection.W * l.SunColor.Xyz
            + MathF.Max(l.MoonDirection.Y, 0f) * l.MoonDirection.W * l.MoonColor.Xyz;
        return Luminance(light) * l.Params.X;
    }

    // Fog amount from lighting.glsl: 1 - exp2(-k^2), k = density * max(d - start, 0).
    private static float FogAmount(in FrameLighting l, float distance)
    {
        float k = l.FogColor.W * MathF.Max(distance - l.CameraPosition.W, 0f);
        return 1f - Math.Clamp(MathF.Pow(2f, -k * k), 0f, 1f);
    }

    [Fact]
    public void Noon_EqualsReferenceNoonForSunAmbientAndParams()
    {
        FrameLighting reference = FrameLighting.ReferenceNoon;
        FrameLighting noon = SkyModel.Evaluate(Noon).Lighting;

        Assert.Equal(reference.SunDirection, noon.SunDirection);
        Assert.Equal(reference.SunColor, noon.SunColor);
        Assert.Equal(reference.AmbientSky, noon.AmbientSky);
        Assert.Equal(reference.AmbientGround, noon.AmbientGround);
        Assert.Equal(reference.Params, noon.Params);
        Assert.Equal(reference.FogColor.Xyz, noon.FogColor.Xyz);
        Assert.Equal(reference.MoonDirection.Xyz, noon.MoonDirection.Xyz);
        Assert.Equal(0f, noon.MoonDirection.W);
        Assert.Equal(Vector3.Zero, noon.CameraPosition.Xyz);
        Assert.True(noon.FogColor.W > 0f);
    }

    [Fact]
    public void Night_SunIsOffMoonIsCoolAndUpFacingLightIsTheNightFloor()
    {
        float noonUp = UpFacingLuminance(FrameLighting.ReferenceNoon);
        Assert.Equal(0.2f + 0.42857143f, noonUp, 4);

        FrameLighting night = SkyModel.Evaluate(Midnight).Lighting;
        Assert.Equal(0f, night.SunDirection.W);
        Assert.True(night.MoonDirection.W > 0f);
        Assert.True(night.MoonColor.Z > night.MoonColor.X, "moonlight must be cool (b > r)");
        Assert.True(night.AmbientSky.Z > night.AmbientSky.X, "night ambient must be cool (b > r)");
        Assert.True(night.FogColor.Z > night.FogColor.X, "night horizon must be cool (b > r)");
        Assert.Equal(SkyModelSettings.DefaultNightBrightness * noonUp, UpFacingLuminance(night), 4);
        Assert.True(night.Params.Y < FrameLighting.ReferenceNoon.Params.Y * 0.5f, "actor floor must drop at night");

        // Every night sample, not just midnight, has the sun off and stays at or below the floor.
        for (float h = 20.5f; h < 27.5f; h += 0.25f)
        {
            FrameLighting l = SkyModel.Evaluate(Hour(h % 24f)).Lighting;
            Assert.Equal(0f, l.SunDirection.W);
            Assert.True(UpFacingLuminance(l) <= SkyModelSettings.DefaultNightBrightness * noonUp + 1e-4f);
            Assert.True(UpFacingLuminance(l) >= 0.5f * SkyModelSettings.DefaultNightBrightness * noonUp);
        }
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(0.05f)]
    [InlineData(0.4f)]
    [InlineData(1f)]
    public void NightFloor_FollowsTheSetting(float nightBrightness)
    {
        SkyModelSettings settings = new(nightBrightness);
        float noonUp = UpFacingLuminance(FrameLighting.ReferenceNoon);

        FrameLighting night = SkyModel.Evaluate(Midnight, settings).Lighting;

        Assert.Equal(nightBrightness * noonUp, UpFacingLuminance(night), 4);
        Assert.Equal(0.35f * nightBrightness, night.Params.Y, 5);
        // The setting does not touch the day.
        Assert.Equal(FrameLighting.ReferenceNoon.AmbientSky, SkyModel.Evaluate(Noon, settings).Lighting.AmbientSky);
    }

    [Theory]
    [InlineData(6f)]
    [InlineData(18f)]
    public void DawnAndDusk_HaveWarmOrangeHorizons(float hour)
    {
        SkyState sky = SkyModel.Evaluate(Hour(hour));
        Vector3 h = sky.HorizonColor;
        Assert.True(h.X > 0.85f && h.X > h.Y * 1.5f && h.Y > h.Z, $"horizon {h} is not warm orange");
        Assert.True(sky.Lighting.SunColor.X > sky.Lighting.SunColor.Z * 2f, "low sun must be warm");
    }

    [Fact]
    public void SunPath_RisesEastSetsWestAndPassesThroughNoon()
    {
        Vector3 rise = SkyModel.SunDirectionAt(Hour(6f));
        Vector3 set = SkyModel.SunDirectionAt(Hour(18f));
        Assert.Equal(0f, rise.Y, 5);
        Assert.Equal(0f, set.Y, 5);
        Assert.Equal(-rise.X, set.X, 5);
        Assert.Equal(-rise.Z, set.Z, 5);
        Assert.Equal(FrameLighting.ReferenceNoon.SunDirection.Xyz, SkyModel.SunDirectionAt(Noon));
        Assert.True(SkyModel.SunDirectionAt(Hour(9f)).Y > 0f);
        Assert.True(SkyModel.SunDirectionAt(Hour(21f)).Y < 0f);

        // A great circle: every point is perpendicular to the plane normal rise x noon.
        Vector3 normal = Vector3.Normalize(Vector3.Cross(rise, FrameLighting.ReferenceNoon.SunDirection.Xyz));
        for (int i = 0; i < 96; i++)
        {
            Assert.Equal(0f, Vector3.Dot(normal, SkyModel.SunDirectionAt(i / 96f)), 5);
        }
    }

    [Fact]
    public void EveryTime_HorizonIsFogDirectionsAreUnitAndMoonIsOppositeSun()
    {
        for (int i = 0; i < Samples; i++)
        {
            SkyState sky = SkyModel.Evaluate(i / (float)Samples);
            Assert.Equal(sky.HorizonColor, sky.Lighting.FogColor.Xyz);
            Assert.Equal(1f, sky.SunDirection.Length, 4);
            Assert.Equal(1f, sky.MoonDirection.Length, 4);
            Assert.Equal(1f, sky.Lighting.SunDirection.Xyz.Length, 4);
            Assert.Equal(1f, sky.Lighting.MoonDirection.Xyz.Length, 4);
            Assert.Equal(-sky.SunDirection, sky.MoonDirection);
            Assert.Equal(sky.SunDirection, sky.Lighting.SunDirection.Xyz);
            if (sky.SunDirection.Y < -sky.SunDiscAngularRadius)
            {
                Assert.Equal(Vector3.Zero, sky.SunDiscColor);
            }
            Assert.InRange(sky.StarVisibility, 0f, 1f);
        }
    }

    [Fact]
    public void EveryTime_FogIsWithinTheRenderDistanceBounds()
    {
        for (int i = 0; i < Samples; i++)
        {
            FrameLighting l = SkyModel.Evaluate(i / (float)Samples).Lighting;
            Assert.True(FogAmount(l, 48f) >= 0.9f, $"fog at 48 is {FogAmount(l, 48f)} at t={i / (float)Samples}");
            Assert.True(FogAmount(l, 16f) <= 0.1f, $"fog at 16 is {FogAmount(l, 16f)} at t={i / (float)Samples}");
        }
    }

    [Fact]
    public void Evaluate_IsContinuousAcrossTheWholeDayIncludingTheWrap()
    {
        // 10k samples plus the wrap step from the last sample back to 0.
        SkyState previous = SkyModel.Evaluate((Samples - 1) / (float)Samples);
        for (int i = 0; i <= Samples; i++)
        {
            SkyState current = SkyModel.Evaluate(i / (float)Samples);
            float delta = MaxDelta(previous, current);
            Assert.True(delta < 0.01f, $"jump of {delta} at sample {i}");
            previous = current;
        }
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(0.125f)]
    [InlineData(0.25f)]
    [InlineData(0.5f)]
    [InlineData(0.8125f)]
    [InlineData(0.99609375f)]
    public void Evaluate_IsPeriodic(float t)
    {
        SkyState sky = SkyModel.Evaluate(t);
        Assert.Equal(0f, MaxDelta(sky, SkyModel.Evaluate(t + 1f)));
        Assert.Equal(0f, MaxDelta(sky, SkyModel.Evaluate(t - 1f)));
        Assert.Equal(t, sky.TimeOfDay01);
    }

    [Fact]
    public void Evaluate_IsPeriodicWithinFloatPrecisionForArbitraryTimes()
    {
        Random random = new(7);
        for (int i = 0; i < 1000; i++)
        {
            float t = (float)random.NextDouble();
            SkyState sky = SkyModel.Evaluate(t);
            Assert.True(MaxDelta(sky, SkyModel.Evaluate(t + 1f)) < 1e-4f);
            Assert.True(MaxDelta(sky, SkyModel.Evaluate(t - 1f)) < 1e-4f);
        }
    }

    [Fact]
    public void Evaluate_TreatsNonFiniteTimeAsNoon()
    {
        Assert.Equal(0f, MaxDelta(SkyModel.Evaluate(Noon), SkyModel.Evaluate(float.NaN)));
        Assert.Equal(0f, MaxDelta(SkyModel.Evaluate(Noon), SkyModel.Evaluate(float.PositiveInfinity)));
    }

    [Fact]
    public void Stars_AreFullAtMidnightAndOffAtNoon()
    {
        Assert.Equal(1f, SkyModel.Evaluate(Midnight).StarVisibility);
        Assert.Equal(0f, SkyModel.Evaluate(Noon).StarVisibility);
    }

    [Theory]
    [InlineData(null, SkyModelSettings.DefaultNightBrightness)]
    [InlineData("", SkyModelSettings.DefaultNightBrightness)]
    [InlineData("abc", SkyModelSettings.DefaultNightBrightness)]
    [InlineData("NaN", SkyModelSettings.DefaultNightBrightness)]
    [InlineData("Infinity", SkyModelSettings.DefaultNightBrightness)]
    [InlineData("0.3", 0.3f)]
    [InlineData("0", 0f)]
    [InlineData("1", 1f)]
    [InlineData("-2", 0f)]
    [InlineData("7", 1f)]
    [InlineData("1e-1", 0.1f)]
    public void Parse_ReadsAClampedInvariantFloat(string? value, float expected)
    {
        Assert.Equal(expected, SkyModelSettings.Parse(value).NightBrightness);
    }

    private static float s_sink;

    [Fact]
    public void Evaluate_AllocatesNothing()
    {
        SkyModelSettings settings = new(0.2f);
        float t = 0f;
        AllocationAssert.AtMost(0, () =>
        {
            t += 0.0137f;
            s_sink = SkyModel.Evaluate(t, settings).StarVisibility;
        });
    }

    private static float MaxDelta(in SkyState a, in SkyState b)
    {
        float d = 0f;
        d = MathF.Max(d, MaxDelta(a.Lighting.SunDirection, b.Lighting.SunDirection));
        d = MathF.Max(d, MaxDelta(a.Lighting.SunColor, b.Lighting.SunColor));
        d = MathF.Max(d, MaxDelta(a.Lighting.MoonDirection, b.Lighting.MoonDirection));
        d = MathF.Max(d, MaxDelta(a.Lighting.MoonColor, b.Lighting.MoonColor));
        d = MathF.Max(d, MaxDelta(a.Lighting.AmbientSky, b.Lighting.AmbientSky));
        d = MathF.Max(d, MaxDelta(a.Lighting.AmbientGround, b.Lighting.AmbientGround));
        d = MathF.Max(d, MaxDelta(a.Lighting.FogColor, b.Lighting.FogColor));
        // Fog start is in units; compare it at a tenth so a 0.1-unit step counts like a 0.01 color step.
        d = MathF.Max(d, MathF.Abs(a.Lighting.CameraPosition.W - b.Lighting.CameraPosition.W) * 0.1f);
        d = MathF.Max(d, MaxDelta(a.Lighting.Params, b.Lighting.Params));
        d = MathF.Max(d, MaxDelta(new Vector4(a.ZenithColor, a.StarVisibility), new Vector4(b.ZenithColor, b.StarVisibility)));
        d = MathF.Max(d, MaxDelta(new Vector4(a.SunDiscColor, 0f), new Vector4(b.SunDiscColor, 0f)));
        d = MathF.Max(d, MaxDelta(new Vector4(a.MoonDiscColor, 0f), new Vector4(b.MoonDiscColor, 0f)));
        return d;
    }

    private static float MaxDelta(Vector4 a, Vector4 b)
    {
        Vector4 d = a - b;
        return MathF.Max(MathF.Max(MathF.Abs(d.X), MathF.Abs(d.Y)), MathF.Max(MathF.Abs(d.Z), MathF.Abs(d.W)));
    }
}
