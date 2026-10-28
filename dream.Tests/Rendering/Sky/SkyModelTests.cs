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

    // The same for any face normal: hemisphere ambient plus sun and moon.
    private static float FaceLuminance(in FrameLighting l, Vector3 n)
    {
        Vector3 ambient = Vector3.Lerp(l.AmbientGround.Xyz, l.AmbientSky.Xyz, n.Y * 0.5f + 0.5f);
        Vector3 light = ambient
            + MathF.Max(Vector3.Dot(n, l.SunDirection.Xyz), 0f) * l.SunDirection.W * l.SunColor.Xyz
            + MathF.Max(Vector3.Dot(n, l.MoonDirection.Xyz), 0f) * l.MoonDirection.W * l.MoonColor.Xyz;
        return Luminance(light) * l.Params.X;
    }

    // Fog amount from lighting.glsl: 1 - exp2(-k^2), k = density * max(d - start, 0).
    private static float FogAmount(in FrameLighting l, float distance)
    {
        float k = l.FogColor.W * MathF.Max(distance - l.CameraPosition.W, 0f);
        return 1f - Math.Clamp(MathF.Pow(2f, -k * k), 0f, 1f);
    }

    // Until T2d this test said "noon equals FrameLighting.ReferenceNoon". That kept the pre-T1 look: a
    // sun only about 25 degrees high, so every day looked like late afternoon and walls facing -Z were
    // lit more than the ground. T2d deliberately moves the noon sun to 60 degrees and retunes the light,
    // so noon is now pinned to SkyModelKeyframes.Noon. ReferenceNoon stays the frozen legacy look, and
    // FrameLightingContractTests still feeds it to the shader mirrors directly.
    [Fact]
    public void Noon_EqualsTheNoonKeyframe_NotTheLegacyReferenceNoon()
    {
        FrameLighting expected = SkyModelKeyframes.Noon;
        FrameLighting noon = SkyModel.Evaluate(Noon).Lighting;

        Assert.Equal(expected.SunDirection, noon.SunDirection);
        Assert.Equal(expected.SunColor, noon.SunColor);
        Assert.Equal(expected.MoonDirection, noon.MoonDirection);
        Assert.Equal(expected.AmbientSky, noon.AmbientSky);
        Assert.Equal(expected.AmbientGround, noon.AmbientGround);
        Assert.Equal(expected.FogColor, noon.FogColor);
        Assert.Equal(expected.CameraPosition, noon.CameraPosition);
        Assert.Equal(expected.Params, noon.Params);
        Assert.Equal(0f, noon.MoonDirection.W);
        Assert.True(noon.FogColor.W > 0f);

        // What did not change: the fog colour, the actor params and the sun's azimuth.
        FrameLighting legacy = FrameLighting.ReferenceNoon;
        Assert.Equal(legacy.FogColor.Xyz, noon.FogColor.Xyz);
        Assert.Equal(legacy.Params, noon.Params);
        Vector2 legacyAzimuth = Vector2.Normalize(new Vector2(legacy.SunDirection.X, legacy.SunDirection.Z));
        Vector2 noonAzimuth = Vector2.Normalize(new Vector2(noon.SunDirection.X, noon.SunDirection.Z));
        Assert.Equal(legacyAzimuth.X, noonAzimuth.X, 5);
        Assert.Equal(legacyAzimuth.Y, noonAzimuth.Y, 5);

        // What did: the sun is about 60 degrees high instead of about 25.
        Assert.InRange(MathHelper.RadiansToDegrees(MathF.Asin(legacy.SunDirection.Y)), 25f, 26f);
        Assert.Equal(60f, MathHelper.RadiansToDegrees(MathF.Asin(noon.SunDirection.Y)), 2);
    }

    // A bright midday: an up-facing surface gets 0.85-0.95 (luma, after exposure), and a wall facing
    // -Z (toward the noon sun's azimuth) is lit less than the ground, unlike the legacy low sun.
    [Fact]
    public void Noon_UpFacingLightIsABrightMidday_AndAMinusZWallIsNoBrighterThanTheGround()
    {
        FrameLighting noon = SkyModel.Evaluate(Noon).Lighting;
        float up = UpFacingLuminance(noon);
        float wall = FaceLuminance(noon, -Vector3.UnitZ);

        Assert.InRange(up, 0.85f, 0.95f);
        Assert.Equal(SkyModelKeyframes.NoonUpFacingLuminance(), up, 4);
        Assert.True(wall <= up, $"-Z wall {wall} is brighter than the ground {up}");
        Assert.True(
            FaceLuminance(FrameLighting.ReferenceNoon, -Vector3.UnitZ) > UpFacingLuminance(FrameLighting.ReferenceNoon),
            "the legacy noon is the case this guards against");
        Assert.True(noon.SunColor.X >= noon.SunColor.Z && noon.SunColor.Z > 0.9f, "the noon sun is near white");
        Assert.True(noon.AmbientSky.Z > noon.AmbientSky.X, "the sky ambient is cool");
        Assert.True(noon.AmbientGround.X > noon.AmbientGround.Z, "the ground bounce is warm");
    }

    [Fact]
    public void Night_SunIsOffMoonIsCoolAndUpFacingLightIsTheNightFloor()
    {
        float noonUp = UpFacingLuminance(SkyModelKeyframes.Noon);
        Assert.Equal(0.07f, SkyModelSettings.DefaultNightBrightness);

        FrameLighting night = SkyModel.Evaluate(Midnight).Lighting;
        Assert.Equal(0f, night.SunDirection.W);
        Assert.True(night.MoonDirection.W > 0f);
        Assert.True(night.MoonColor.Z > night.MoonColor.X, "moonlight must be cool (b > r)");
        Assert.True(night.AmbientSky.Z > night.AmbientSky.X, "night ambient must be cool (b > r)");
        Assert.True(night.FogColor.Z > night.FogColor.X, "night horizon must be cool (b > r)");
        Assert.Equal(SkyModelSettings.DefaultNightBrightness * noonUp, UpFacingLuminance(night), 4);
        Assert.True(night.Params.Y < SkyModelKeyframes.Noon.Params.Y * 0.5f, "actor floor must drop at night");
        Assert.Equal(SkyModelKeyframes.NightActorAmbientScale, night.Params.Z);
        Assert.True(night.Params.Z < SkyModelKeyframes.Noon.Params.Z * 0.5f, "actor ambient scale must drop at night");
        // Most of the night light is the moon, not ambient, so side faces fall off into silhouettes.
        Assert.True(Luminance(night.AmbientSky.Xyz) < 0.5f * UpFacingLuminance(night), "night light is mostly ambient");

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
        float noonUp = UpFacingLuminance(SkyModelKeyframes.Noon);

        FrameLighting night = SkyModel.Evaluate(Midnight, settings).Lighting;

        Assert.Equal(nightBrightness * noonUp, UpFacingLuminance(night), 4);
        Assert.Equal(0.35f * nightBrightness, night.Params.Y, 5);
        // The actor ambient scale is a ratio on an ambient that already follows the setting.
        Assert.Equal(SkyModelKeyframes.NightActorAmbientScale, night.Params.Z);
        // The setting does not touch the day.
        Assert.Equal(SkyModelKeyframes.Noon.AmbientSky, SkyModel.Evaluate(Noon, settings).Lighting.AmbientSky);
    }

    [Fact]
    public void ActorAmbientScale_IsTheDayValueByDay_TheNightValueAtNight_AndInBetweenAtTwilight()
    {
        for (int i = 0; i < Samples; i++)
        {
            float z = SkyModel.Evaluate(i / (float)Samples).Lighting.Params.Z;
            Assert.InRange(z, SkyModelKeyframes.NightActorAmbientScale, SkyModelKeyframes.DayActorAmbientScale);
        }

        for (float h = 10f; h <= 14f; h += 0.5f)
        {
            Assert.Equal(SkyModelKeyframes.DayActorAmbientScale, SkyModel.Evaluate(Hour(h)).Lighting.Params.Z);
        }

        for (float h = 20f; h <= 28f; h += 0.5f)
        {
            Assert.Equal(SkyModelKeyframes.NightActorAmbientScale, SkyModel.Evaluate(Hour(h % 24f)).Lighting.Params.Z);
        }

        foreach (float h in new[] { 5f, 6f, 18f, 19f })
        {
            float z = SkyModel.Evaluate(Hour(h)).Lighting.Params.Z;
            Assert.True(z > SkyModelKeyframes.NightActorAmbientScale && z < SkyModelKeyframes.DayActorAmbientScale, $"{h}:00 scale {z}");
        }
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
        Assert.Equal(SkyModelKeyframes.NoonSunDirection, SkyModel.SunDirectionAt(Noon));
        Assert.Equal(1f, SkyModelKeyframes.NoonSunDirection.Length, 5);
        Assert.True(SkyModel.SunDirectionAt(Hour(9f)).Y > 0f);
        Assert.True(SkyModel.SunDirectionAt(Hour(21f)).Y < 0f);

        // A great circle: every point is perpendicular to the plane normal rise x noon.
        Vector3 normal = Vector3.Normalize(Vector3.Cross(rise, SkyModelKeyframes.NoonSunDirection));
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
