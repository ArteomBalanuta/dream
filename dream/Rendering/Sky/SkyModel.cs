using Dream.Rendering.Lighting;
using OpenTK.Mathematics;

namespace Dream.Rendering.Sky;

// Pure time-of-day -> sky and frame lighting. No GL, no allocation per call (the keyframe table is static).
// The keyframes and the night floor are documented in SkyModelKeyframes.
public static class SkyModel
{
    public const float SunDiscAngularRadius = 0.03f;
    public const float MoonDiscAngularRadius = 0.025f;

    // Direct light fades in over this band of elevation (sin of altitude) so side faces get a low,
    // warm sun at sunrise but nothing from below the horizon.
    private const float LightFadeBelow = -0.04f;
    private const float LightFadeAbove = 0.06f;

    // A disc fades in from fully below the horizon over this much elevation (sin of altitude). With the
    // 60 degree noon sun the bodies cross the horizon about twice as fast as in T1, so the band is wider
    // than the discs themselves to keep the fade smooth (no step above 0.01 per 1/10000 of a day).
    private const float DiscFadeBand = 0.1f;

    // The sun at noon: SkyModelKeyframes.NoonSunDirection (about 60 degrees high on ReferenceNoon's azimuth).
    // East is the horizontal unit vector perpendicular to it, so the great circle through east, noon and
    // west rises at 06:00 and sets at 18:00.
    private static readonly Vector3 NoonSun = SkyModelKeyframes.NoonSunDirection;
    private static readonly Vector3 East = Vector3.Normalize(new Vector3(-NoonSun.Z, 0f, NoonSun.X));

    public static SkyState Evaluate(float timeOfDay01) => Evaluate(timeOfDay01, SkyModelSettings.Default);

    public static SkyState Evaluate(float timeOfDay01, in SkyModelSettings settings)
    {
        float t = Wrap01(timeOfDay01);
        Vector3 sun = SunDirectionAt(t);
        Vector3 moon = -sun;

        SampleKeyframes(t, out SkyKeyframe k);

        float nightBrightness = float.IsFinite(settings.NightBrightness)
            ? Math.Clamp(settings.NightBrightness, 0f, 1f)
            : SkyModelSettings.DefaultNightBrightness;
        float lightScale = 1f + k.NightScaled * (nightBrightness / SkyModelSettings.DefaultNightBrightness - 1f);

        float sunIntensity = k.SunIntensity * SmoothStep(LightFadeBelow, LightFadeAbove, sun.Y);
        float moonIntensity = k.MoonIntensity * lightScale * SmoothStep(LightFadeBelow, LightFadeAbove, moon.Y);

        FrameLighting lighting = new()
        {
            SunDirection = new Vector4(sun, sunIntensity),
            SunColor = new Vector4(k.SunColor, 0f),
            MoonDirection = new Vector4(moon, moonIntensity),
            MoonColor = new Vector4(k.MoonColor, 0f),
            AmbientSky = new Vector4(k.AmbientSky * lightScale, 0f),
            AmbientGround = new Vector4(k.AmbientGround * lightScale, 0f),
            FogColor = new Vector4(k.Horizon, k.FogDensity),
            CameraPosition = new Vector4(0f, 0f, 0f, k.FogStart),
            Params = new Vector4(k.Exposure, k.ActorFloor * lightScale, k.ActorAmbientScale, 0f),
        };

        return new SkyState
        {
            Lighting = lighting,
            ZenithColor = k.Zenith,
            HorizonColor = k.Horizon,
            SunDirection = sun,
            SunDiscColor = k.SunDisc * SmoothStep(-SunDiscAngularRadius, -SunDiscAngularRadius + DiscFadeBand, sun.Y),
            SunDiscAngularRadius = SunDiscAngularRadius,
            MoonDirection = moon,
            MoonDiscColor = SkyModelKeyframes.MoonDisc * SmoothStep(-MoonDiscAngularRadius, -MoonDiscAngularRadius + DiscFadeBand, moon.Y),
            MoonDiscAngularRadius = MoonDiscAngularRadius,
            StarVisibility = k.Stars,
            TimeOfDay01 = t,
        };
    }

    // Unit vector toward the sun. 06:00 = east horizon, 12:00 = SkyModelKeyframes.NoonSunDirection exactly,
    // 18:00 = west horizon, 00:00 = -noon (the moon is then exactly where the noon sun is).
    public static Vector3 SunDirectionAt(float timeOfDay01)
    {
        float phi = MathHelper.TwoPi * (Wrap01(timeOfDay01) - 0.5f);
        return NoonSun * MathF.Cos(phi) - East * MathF.Sin(phi);
    }

    // Non-finite input reads as noon, matching the C0 stub.
    internal static float Wrap01(float value)
    {
        if (!float.IsFinite(value))
        {
            return 0.5f;
        }

        float wrapped = value - MathF.Floor(value);
        return wrapped >= 1f ? 0f : wrapped;
    }

    private static void SampleKeyframes(float t, out SkyKeyframe result)
    {
        SkyKeyframe[] table = SkyModelKeyframes.Table;
        int last = table.Length - 1;
        int i = last;
        for (int j = 0; j < last; j++)
        {
            if (t < table[j + 1].Time01)
            {
                i = j;
                break;
            }
        }

        ref readonly SkyKeyframe a = ref table[i];
        ref readonly SkyKeyframe b = ref table[i == last ? 0 : i + 1];
        float end = i == last ? 1f + b.Time01 : b.Time01;
        float s = SmoothStep(a.Time01, end, t);
        result = Blend(in a, in b, s);
    }

    private static SkyKeyframe Blend(in SkyKeyframe a, in SkyKeyframe b, float s) => new(
        hour: (a.Time01 + (b.Time01 - a.Time01) * s) * 24f,
        zenith: Lerp(a.Zenith, b.Zenith, s),
        horizon: Lerp(a.Horizon, b.Horizon, s),
        fogDensity: Lerp(a.FogDensity, b.FogDensity, s),
        fogStart: Lerp(a.FogStart, b.FogStart, s),
        sunColor: Lerp(a.SunColor, b.SunColor, s),
        sunIntensity: Lerp(a.SunIntensity, b.SunIntensity, s),
        sunDisc: Lerp(a.SunDisc, b.SunDisc, s),
        moonColor: Lerp(a.MoonColor, b.MoonColor, s),
        moonIntensity: Lerp(a.MoonIntensity, b.MoonIntensity, s),
        ambientSky: Lerp(a.AmbientSky, b.AmbientSky, s),
        ambientGround: Lerp(a.AmbientGround, b.AmbientGround, s),
        exposure: Lerp(a.Exposure, b.Exposure, s),
        actorFloor: Lerp(a.ActorFloor, b.ActorFloor, s),
        actorAmbientScale: Lerp(a.ActorAmbientScale, b.ActorAmbientScale, s),
        stars: Lerp(a.Stars, b.Stars, s),
        nightScaled: Lerp(a.NightScaled, b.NightScaled, s));

    // a + (b - a) * s returns a exactly when a == b, which keeps the noon plateau bit-exact.
    private static float Lerp(float a, float b, float s) => a + (b - a) * s;

    private static Vector3 Lerp(Vector3 a, Vector3 b, float s) => a + (b - a) * s;

    private static float SmoothStep(float edge0, float edge1, float x)
    {
        float s = Math.Clamp((x - edge0) / (edge1 - edge0), 0f, 1f);
        return s * s * (3f - 2f * s);
    }
}
