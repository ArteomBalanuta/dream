using Dream.Rendering.Lighting;
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
    public readonly float ActorAmbientScale; // not night-scaled: the ambient it multiplies already is
    public readonly float Stars;
    public readonly float NightScaled;      // 0 or 1; blended like everything else

    public SkyKeyframe(
        float hour,
        Vector3 zenith, Vector3 horizon, float fogDensity, float fogStart,
        Vector3 sunColor, float sunIntensity, Vector3 sunDisc,
        Vector3 moonColor, float moonIntensity,
        Vector3 ambientSky, Vector3 ambientGround,
        float exposure, float actorFloor, float actorAmbientScale, float stars, float nightScaled)
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
        ActorAmbientScale = actorAmbientScale;
        Stars = stars;
        NightScaled = nightScaled;
    }
}

// The authored day. Colors are linear-ish display RGB, chosen for a stylised voxel look:
//
//   00:00 / 04:00 / 20:00  Night      near-black neutral sky and horizon, cool blue moonlight, full stars.
//                                     Up-facing ambient + moon at midnight is exactly NightBrightness x noon;
//                                     most of it is the moon, so side faces fall off into silhouettes.
//   05:00                  Blue hour  dark grey-violet, stars fading, moon dimming. 05:00 and 19:00 are the grim
//                                     look's dusk band: low ambient, a neutral horizon (the isometric void is
//                                     a quarter of it, so a tinted one reads as saturated black), and the
//                                     brazier pools (PlacedLightIndex) carry the bright tail.
//   06:00                  Sunrise    warm orange horizon, low orange sun on side faces, warm ground bounce.
//   07:30                  Morning    pale peach horizon settling to day, sun slightly warm.
//   10:00 - 14:00          Day        Noon: a high (60 degree) near-white sun, cool sky / warm bounce ambient,
//                                     0.62/0.74/0.88 fog. Up-facing light about 0.92; a -Z wall gets less than
//                                     the ground. (T1 used FrameLighting.ReferenceNoon, a 25 degree sun that made
//                                     every day look like late afternoon; ReferenceNoon is now the frozen legacy look.)
//   16:30                  Afternoon  slightly warmer and hazier.
//   18:00                  Sunset     saturated orange horizon, red-orange sun, warm bounce.
//   19:00                  Dusk       the blue hour's look, mirrored: first stars.
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

    // The noon sun: NoonSunElevationDegrees above the horizon, on ReferenceNoon's azimuth (the old light
    // seen from spawn), so the sun still rises in the same east and sets in the same west.
    public const float NoonSunElevationDegrees = 60f;
    public static readonly Vector3 NoonSunDirection = ComputeNoonSunDirection();

    public static readonly Vector3 DaySunColor = new(1.00f, 0.98f, 0.94f);
    public const float DaySunIntensity = 0.82f;
    public static readonly Vector3 DayAmbientSky = new(0.20f, 0.22f, 0.26f);
    public static readonly Vector3 DayAmbientGround = new(0.17f, 0.16f, 0.14f);
    public const float DayFogDensity = 0.066f;
    public const float DayFogStart = 18f;
    public const float DayActorFloor = 0.35f;

    // Near-black and neutral: the horizon (and fog) sits just above lit night terrain, so distant ground
    // fades into the sky instead of glowing. The isometric pass clears the void to a quarter of the horizon,
    // and an 8-bit value of (1, 1, 2) measures as 50% saturated, so the horizon is neutral (b only a hair above
    // r, which SkyModelTests wants cool) and the void rounds to (2, 2, 2).
    public static readonly Vector3 NightZenith = new(0.010f, 0.010f, 0.011f);
    public static readonly Vector3 NightHorizon = new(0.026f, 0.026f, 0.028f);
    public static readonly Vector3 NightAmbientSky = new(0.016f, 0.024f, 0.048f);
    public static readonly Vector3 NightAmbientGround = new(0.006f, 0.007f, 0.013f);
    public static readonly Vector3 MoonTint = new(0.60f, 0.70f, 1.00f);
    public static readonly Vector3 MoonDisc = new(0.86f, 0.89f, 0.96f);

    // FrameLighting.Params.z: how much enemyFragment.glsl scales the frame ambient for actors (wolves,
    // players). Day is today's look (0.2 ambient x 3.25 = 0.65). At night the ambient is a cool blue
    // and most of the light, so the day scale would make actors glow (about 2.2x terrain on up faces);
    // 1.5 keeps them about 1.2x terrain at midnight. Twilight keyframes sit in between.
    public const float DayActorAmbientScale = 3.25f;
    public const float SunHorizonActorAmbientScale = 2.75f;
    public const float TwilightActorAmbientScale = 2.0f;
    public const float NightActorAmbientScale = 1.5f;

    // Moon intensity that puts the up-facing light at exactly DefaultNightBrightness x noon's up-facing light
    // when the moon is at its highest (midnight, elevation = noon sun elevation).
    public static readonly float NightMoonIntensity = ComputeNightMoonIntensity();

    public static readonly SkyKeyframe[] Table = BuildTable();

    // SkyModel.Evaluate(0.5).Lighting: the Day keyframe with the noon sun. Built from the constants
    // above, not by evaluating, so it can be read during SkyModel's own static initialisation.
    public static FrameLighting Noon => new()
    {
        SunDirection = new Vector4(NoonSunDirection, DaySunIntensity),
        SunColor = new Vector4(DaySunColor, 0f),
        MoonDirection = new Vector4(-NoonSunDirection, 0f),
        MoonColor = new Vector4(MoonTint, 0f),
        AmbientSky = new Vector4(DayAmbientSky, 0f),
        AmbientGround = new Vector4(DayAmbientGround, 0f),
        FogColor = new Vector4(NoonHorizon, DayFogDensity),
        CameraPosition = new Vector4(0f, 0f, 0f, DayFogStart),
        Params = new Vector4(1f, DayActorFloor, DayActorAmbientScale, 0f),
    };

    public static float NoonUpFacingLuminance() =>
        Luminance(DayAmbientSky + DaySunColor * (NoonSunDirection.Y * DaySunIntensity));

    private static Vector3 ComputeNoonSunDirection()
    {
        Vector3 legacy = FrameLighting.ReferenceNoon.SunDirection.Xyz;
        Vector3 azimuth = Vector3.Normalize(new Vector3(legacy.X, 0f, legacy.Z));
        float elevation = MathHelper.DegreesToRadians(NoonSunElevationDegrees);
        return Vector3.Normalize(azimuth * MathF.Cos(elevation) + Vector3.UnitY * MathF.Sin(elevation));
    }

    private static float ComputeNightMoonIntensity()
    {
        float target = SkyModelSettings.DefaultNightBrightness * NoonUpFacingLuminance();
        return (target - Luminance(NightAmbientSky)) / (NoonSunDirection.Y * Luminance(MoonTint));
    }

    private static SkyKeyframe Night(float hour) => new(
        hour,
        zenith: NightZenith, horizon: NightHorizon, fogDensity: 0.058f, fogStart: 12f,
        sunColor: new Vector3(1.00f, 0.45f, 0.20f), sunIntensity: 0f, sunDisc: new Vector3(1.00f, 0.50f, 0.25f),
        moonColor: MoonTint, moonIntensity: NightMoonIntensity,
        ambientSky: NightAmbientSky, ambientGround: NightAmbientGround,
        exposure: 1f, actorFloor: 0.35f * SkyModelSettings.DefaultNightBrightness,
        actorAmbientScale: NightActorAmbientScale, stars: 1f, nightScaled: 1f);

    private static SkyKeyframe Day(float hour) => new(
        hour,
        zenith: NoonZenith, horizon: NoonHorizon, fogDensity: DayFogDensity, fogStart: DayFogStart,
        sunColor: DaySunColor, sunIntensity: DaySunIntensity, sunDisc: new Vector3(1.00f, 0.95f, 0.85f),
        moonColor: MoonTint, moonIntensity: 0f,
        ambientSky: DayAmbientSky, ambientGround: DayAmbientGround,
        exposure: 1f, actorFloor: DayActorFloor, actorAmbientScale: DayActorAmbientScale, stars: 0f, nightScaled: 0f);

    private static SkyKeyframe[] BuildTable() =>
    [
        Night(0f),
        Night(4f),
        new(5f, // blue hour
            zenith: new Vector3(0.030f, 0.032f, 0.040f), horizon: new Vector3(0.090f, 0.090f, 0.090f),
            fogDensity: 0.060f, fogStart: 14f,
            sunColor: new Vector3(1.00f, 0.45f, 0.20f), sunIntensity: 0f, sunDisc: new Vector3(1.00f, 0.50f, 0.25f),
            moonColor: MoonTint, moonIntensity: NightMoonIntensity * 0.6f,
            ambientSky: new Vector3(0.048f, 0.048f, 0.068f), ambientGround: new Vector3(0.021f, 0.020f, 0.032f),
            exposure: 1f, actorFloor: 0.10f, actorAmbientScale: TwilightActorAmbientScale, stars: 0.4f, nightScaled: 0f),
        new(6f, // sunrise
            zenith: new Vector3(0.220f, 0.320f, 0.560f), horizon: new Vector3(0.950f, 0.540f, 0.270f),
            fogDensity: 0.062f, fogStart: 16f,
            sunColor: new Vector3(1.00f, 0.58f, 0.30f), sunIntensity: 0.85f, sunDisc: new Vector3(1.00f, 0.62f, 0.32f),
            moonColor: MoonTint, moonIntensity: 0f,
            ambientSky: new Vector3(0.170f, 0.145f, 0.140f), ambientGround: new Vector3(0.105f, 0.080f, 0.065f),
            exposure: 1f, actorFloor: 0.22f, actorAmbientScale: SunHorizonActorAmbientScale, stars: 0f, nightScaled: 0f),
        new(7.5f, // morning
            zenith: new Vector3(0.250f, 0.430f, 0.780f), horizon: new Vector3(0.800f, 0.760f, 0.760f),
            fogDensity: 0.064f, fogStart: 17f,
            sunColor: new Vector3(1.00f, 0.90f, 0.78f), sunIntensity: 0.80f, sunDisc: new Vector3(1.00f, 0.88f, 0.72f),
            moonColor: MoonTint, moonIntensity: 0f,
            ambientSky: new Vector3(0.195f, 0.190f, 0.190f), ambientGround: new Vector3(0.180f, 0.170f, 0.160f),
            exposure: 1f, actorFloor: 0.32f, actorAmbientScale: DayActorAmbientScale, stars: 0f, nightScaled: 0f),
        Day(10f),
        Day(14f),
        new(16.5f, // afternoon
            zenith: new Vector3(0.260f, 0.430f, 0.780f), horizon: new Vector3(0.740f, 0.740f, 0.760f),
            fogDensity: 0.065f, fogStart: 17f,
            sunColor: new Vector3(1.00f, 0.88f, 0.72f), sunIntensity: 0.80f, sunDisc: new Vector3(1.00f, 0.86f, 0.68f),
            moonColor: MoonTint, moonIntensity: 0f,
            ambientSky: new Vector3(0.200f, 0.190f, 0.180f), ambientGround: new Vector3(0.190f, 0.175f, 0.160f),
            exposure: 1f, actorFloor: 0.32f, actorAmbientScale: DayActorAmbientScale, stars: 0f, nightScaled: 0f),
        new(18f, // sunset
            zenith: new Vector3(0.200f, 0.250f, 0.480f), horizon: new Vector3(0.980f, 0.470f, 0.200f),
            fogDensity: 0.062f, fogStart: 16f,
            sunColor: new Vector3(1.00f, 0.50f, 0.24f), sunIntensity: 0.85f, sunDisc: new Vector3(1.00f, 0.55f, 0.25f),
            moonColor: MoonTint, moonIntensity: 0f,
            ambientSky: new Vector3(0.170f, 0.130f, 0.130f), ambientGround: new Vector3(0.110f, 0.075f, 0.060f),
            exposure: 1f, actorFloor: 0.22f, actorAmbientScale: SunHorizonActorAmbientScale, stars: 0f, nightScaled: 0f),
        new(19f, // dusk
            zenith: new Vector3(0.030f, 0.032f, 0.040f), horizon: new Vector3(0.090f, 0.090f, 0.090f),
            fogDensity: 0.060f, fogStart: 14f,
            sunColor: new Vector3(1.00f, 0.45f, 0.20f), sunIntensity: 0f, sunDisc: new Vector3(1.00f, 0.50f, 0.25f),
            moonColor: MoonTint, moonIntensity: NightMoonIntensity * 0.6f,
            ambientSky: new Vector3(0.048f, 0.047f, 0.068f), ambientGround: new Vector3(0.021f, 0.019f, 0.032f),
            exposure: 1f, actorFloor: 0.10f, actorAmbientScale: TwilightActorAmbientScale, stars: 0.4f, nightScaled: 0f),
        Night(20f),
    ];
}
