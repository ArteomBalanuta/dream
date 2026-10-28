using OpenTK.Mathematics;

namespace Dream.Rendering.Sky;

// One look of the sky at one time of day. Light terms (ambient, moon, actor floor) of a keyframe with
// NightScaled = 1 are multiplied by NightBrightness / DefaultNightBrightness, so the night floor follows
// SkyModelSettings while the twilight and day keyframes keep their authored values.
internal readonly struct SkyKeyframe
{
    public readonly float Time01;
    public readonly Vector3 Zenith;
    public readonly Vector3 Horizon;        // also the fog color
    public readonly float FogDensity;       // exp2 density per unit
    public readonly float FogStart;         // units
    public readonly Vector3 SunColor;
    public readonly float SunIntensity;     // before the horizon fade
    public readonly Vector3 SunDisc;
    public readonly Vector3 MoonColor;
    public readonly float MoonIntensity;    // before the horizon fade
    public readonly Vector3 AmbientSky;
    public readonly Vector3 AmbientGround;
    public readonly float Exposure;
    public readonly float ActorFloor;
    public readonly float Stars;
    public readonly float NightScaled;      // 0 or 1; blended like everything else

    public SkyKeyframe(
        float hour,
        Vector3 zenith, Vector3 horizon, float fogDensity, float fogStart,
        Vector3 sunColor, float sunIntensity, Vector3 sunDisc,
        Vector3 moonColor, float moonIntensity,
        Vector3 ambientSky, Vector3 ambientGround,
        float exposure, float actorFloor, float stars, float nightScaled)
    {
        Time01 = hour / 24f;
        Zenith = zenith;
        Horizon = horizon;
        FogDensity = fogDensity;
        FogStart = fogStart;
        SunColor = sunColor;
        SunIntensity = sunIntensity;
        SunDisc = sunDisc;
        MoonColor = moonColor;
        MoonIntensity = moonIntensity;
        AmbientSky = ambientSky;
        AmbientGround = ambientGround;
        Exposure = exposure;
        ActorFloor = actorFloor;
        Stars = stars;
        NightScaled = nightScaled;
    }
}

// The authored day. Colors are linear-ish display RGB, chosen for a stylised voxel look:
//
//   00:00 / 04:00 / 20:00  Night      deep navy zenith, slate-blue horizon, cool blue moonlight, full stars.
//                                     Up-facing ambient + moon at midnight is exactly NightBrightness x noon.
//   05:00                  Blue hour  violet zenith, dusky rose horizon, stars fading, moon dimming.
//   06:00                  Sunrise    warm orange horizon, low orange sun on side faces, warm ground bounce.
//   07:30                  Morning    pale peach horizon settling to day, sun slightly warm.
//   10:00 - 14:00          Day        FrameLighting.ReferenceNoon (white sun, 0.2 grey ambient, 0.62/0.74/0.88 fog).
//   16:30                  Afternoon  slightly warmer and hazier.
//   18:00                  Sunset     saturated orange horizon, red-orange sun, warm bounce.
//   19:00                  Dusk       mauve horizon under an indigo zenith, first stars.
//
// Fog: start and exp2 density per keyframe keep 1 - exp2(-k^2), k = density * max(d - start, 0), at
// >= 0.9 at 48 units (the render distance) and <= 0.1 at 16 units. Linear blends of two valid
// (density, start) pairs stay valid at 48 units; the tests sweep the whole day for both bounds.
internal static class SkyModelKeyframes
{
    // Rec. 709 luma: the "brightness" the night floor is measured in.
    public static float Luminance(Vector3 c) => 0.2126f * c.X + 0.7152f * c.Y + 0.0722f * c.Z;

    public static readonly Vector3 NoonZenith = new(0.25f, 0.45f, 0.85f);
    public static readonly Vector3 NoonHorizon = new(0.62f, 0.74f, 0.88f);

    public static readonly Vector3 NightZenith = new(0.010f, 0.016f, 0.042f);
    public static readonly Vector3 NightHorizon = new(0.045f, 0.060f, 0.105f);
    public static readonly Vector3 NightAmbientSky = new(0.040f, 0.055f, 0.095f);
    public static readonly Vector3 NightAmbientGround = new(0.016f, 0.020f, 0.034f);
    public static readonly Vector3 MoonTint = new(0.60f, 0.70f, 1.00f);
    public static readonly Vector3 MoonDisc = new(0.86f, 0.89f, 0.96f);

    // Moon intensity that puts the up-facing light at exactly DefaultNightBrightness x noon's up-facing light
    // when the moon is at its highest (midnight, elevation = noon sun elevation).
    public static readonly float NightMoonIntensity = ComputeNightMoonIntensity();

    public static readonly SkyKeyframe[] Table = BuildTable();

    public static float NoonUpFacingLuminance()
    {
        Vector4 sun = Dream.Rendering.Lighting.FrameLighting.ReferenceNoon.SunDirection;
        Vector3 ambient = Dream.Rendering.Lighting.FrameLighting.ReferenceNoon.AmbientSky.Xyz;
        Vector3 sunColor = Dream.Rendering.Lighting.FrameLighting.ReferenceNoon.SunColor.Xyz;
        return Luminance(ambient + sunColor * (sun.Y * sun.W));
    }

    private static float ComputeNightMoonIntensity()
    {
        float target = SkyModelSettings.DefaultNightBrightness * NoonUpFacingLuminance();
        float moonElevation = Dream.Rendering.Lighting.FrameLighting.ReferenceNoon.SunDirection.Y;
        return (target - Luminance(NightAmbientSky)) / (moonElevation * Luminance(MoonTint));
    }

    private static SkyKeyframe Night(float hour) => new(
        hour,
        zenith: NightZenith, horizon: NightHorizon, fogDensity: 0.058f, fogStart: 12f,
        sunColor: new Vector3(1.00f, 0.45f, 0.20f), sunIntensity: 0f, sunDisc: new Vector3(1.00f, 0.50f, 0.25f),
        moonColor: MoonTint, moonIntensity: NightMoonIntensity,
        ambientSky: NightAmbientSky, ambientGround: NightAmbientGround,
        exposure: 1f, actorFloor: 0.35f * SkyModelSettings.DefaultNightBrightness, stars: 1f, nightScaled: 1f);

    private static SkyKeyframe Day(float hour) => new(
        hour,
        zenith: NoonZenith, horizon: NoonHorizon, fogDensity: 0.066f, fogStart: 18f,
        sunColor: Vector3.One, sunIntensity: 1f, sunDisc: new Vector3(1.00f, 0.95f, 0.85f),
        moonColor: MoonTint, moonIntensity: 0f,
        ambientSky: new Vector3(0.2f), ambientGround: new Vector3(0.2f),
        exposure: 1f, actorFloor: 0.35f, stars: 0f, nightScaled: 0f);

    private static SkyKeyframe[] BuildTable() =>
    [
        Night(0f),
        Night(4f),
        new(5f, // blue hour
            zenith: new Vector3(0.050f, 0.070f, 0.165f), horizon: new Vector3(0.230f, 0.180f, 0.270f),
            fogDensity: 0.060f, fogStart: 14f,
            sunColor: new Vector3(1.00f, 0.45f, 0.20f), sunIntensity: 0f, sunDisc: new Vector3(1.00f, 0.50f, 0.25f),
            moonColor: MoonTint, moonIntensity: NightMoonIntensity * 0.6f,
            ambientSky: new Vector3(0.085f, 0.085f, 0.130f), ambientGround: new Vector3(0.036f, 0.034f, 0.052f),
            exposure: 1f, actorFloor: 0.10f, stars: 0.4f, nightScaled: 0f),
        new(6f, // sunrise
            zenith: new Vector3(0.220f, 0.320f, 0.560f), horizon: new Vector3(0.950f, 0.540f, 0.270f),
            fogDensity: 0.062f, fogStart: 16f,
            sunColor: new Vector3(1.00f, 0.58f, 0.30f), sunIntensity: 0.85f, sunDisc: new Vector3(1.00f, 0.62f, 0.32f),
            moonColor: MoonTint, moonIntensity: 0f,
            ambientSky: new Vector3(0.170f, 0.145f, 0.140f), ambientGround: new Vector3(0.105f, 0.080f, 0.065f),
            exposure: 1f, actorFloor: 0.22f, stars: 0f, nightScaled: 0f),
        new(7.5f, // morning
            zenith: new Vector3(0.250f, 0.430f, 0.780f), horizon: new Vector3(0.800f, 0.760f, 0.760f),
            fogDensity: 0.064f, fogStart: 17f,
            sunColor: new Vector3(1.00f, 0.90f, 0.78f), sunIntensity: 0.95f, sunDisc: new Vector3(1.00f, 0.88f, 0.72f),
            moonColor: MoonTint, moonIntensity: 0f,
            ambientSky: new Vector3(0.195f, 0.190f, 0.190f), ambientGround: new Vector3(0.180f, 0.170f, 0.160f),
            exposure: 1f, actorFloor: 0.32f, stars: 0f, nightScaled: 0f),
        Day(10f),
        Day(14f),
        new(16.5f, // afternoon
            zenith: new Vector3(0.260f, 0.430f, 0.780f), horizon: new Vector3(0.740f, 0.740f, 0.760f),
            fogDensity: 0.065f, fogStart: 17f,
            sunColor: new Vector3(1.00f, 0.88f, 0.72f), sunIntensity: 0.95f, sunDisc: new Vector3(1.00f, 0.86f, 0.68f),
            moonColor: MoonTint, moonIntensity: 0f,
            ambientSky: new Vector3(0.200f, 0.190f, 0.180f), ambientGround: new Vector3(0.190f, 0.175f, 0.160f),
            exposure: 1f, actorFloor: 0.32f, stars: 0f, nightScaled: 0f),
        new(18f, // sunset
            zenith: new Vector3(0.200f, 0.250f, 0.480f), horizon: new Vector3(0.980f, 0.470f, 0.200f),
            fogDensity: 0.062f, fogStart: 16f,
            sunColor: new Vector3(1.00f, 0.50f, 0.24f), sunIntensity: 0.85f, sunDisc: new Vector3(1.00f, 0.55f, 0.25f),
            moonColor: MoonTint, moonIntensity: 0f,
            ambientSky: new Vector3(0.170f, 0.130f, 0.130f), ambientGround: new Vector3(0.110f, 0.075f, 0.060f),
            exposure: 1f, actorFloor: 0.22f, stars: 0f, nightScaled: 0f),
        new(19f, // dusk
            zenith: new Vector3(0.060f, 0.070f, 0.180f), horizon: new Vector3(0.360f, 0.200f, 0.300f),
            fogDensity: 0.060f, fogStart: 14f,
            sunColor: new Vector3(1.00f, 0.45f, 0.20f), sunIntensity: 0f, sunDisc: new Vector3(1.00f, 0.50f, 0.25f),
            moonColor: MoonTint, moonIntensity: NightMoonIntensity * 0.6f,
            ambientSky: new Vector3(0.080f, 0.070f, 0.120f), ambientGround: new Vector3(0.034f, 0.030f, 0.048f),
            exposure: 1f, actorFloor: 0.10f, stars: 0.4f, nightScaled: 0f),
        Night(20f),
    ];
}
