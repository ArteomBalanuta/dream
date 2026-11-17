using OpenTK.Mathematics;
using Dream.Rendering.Effects;
using Dream.Simulation.Inventory;

namespace Dream.Rendering.Lighting;

// The point light each HeldLightKind casts (wave T2). Colors are linear RGB already multiplied by
// intensity, as PointLight expects. The torch flickers (FireFlicker: four octaves plus gusts, in
// intensity, radius and a small positional wobble), driven only by client time and a per-player
// seed, so it is deterministic and costs no state; the lantern burns steady.
public static class HeldLightCatalog
{
    // Raised from 1.6 toward the brazier's 3.0 but held under MaxUnclippedTorchIntensity. The actor
    // transfer function is strictly linear up to the output clamp (no tonemap, no gamma encode), so
    // a self-lit figure's internal spread scales with the gain: MEASURED through enginelook.py (the
    // same instrument the M3 gate uses) the shipped figure under a held torch at 0.4 m spreads
    // 0.262-0.338 at 2.1 against 0.286-0.369 at 2.3, a factor of 0.916 - and neither reaches A3's
    // 0.45, which is why M1 is captured at a BRAZIER 1.2-2.0 m away, a light this constant does not
    // touch. What the torch does carry is A1's bright tail, which is thin.
    //
    // The ceiling. The fire halo packs light.Color * HeldHaloTint * fade into ONE BYTE, so its red
    // channel is TorchIntensity * HeldHaloTint.X * flicker. There is exactly one flicker factor in
    // that product: the light's colour already carries FireFlicker's intensity (peak 1 + A), and the
    // halo does NOT multiply a second, independent flicker on top (it used to, which is why the
    // amplitude had to stay at 0.08). With A = 0.25 the peak red is 2.1 * 0.375 * 1.25 = 0.984, under
    // the byte; the largest intensity that still fits is MaxUnclippedTorchIntensity (2.133), and
    // the largest amplitude at 2.1 is 0.269. Gusts only reduce the flicker, so 1 + A really is the peak.
    //
    // The RADIUS now flickers by +-8.75 % around 8 (7.30 .. 8.70), so FxSimulation.TryClassify can no
    // longer match it exactly: it takes the NEAREST class centre (lantern 6, torch 8, lamp and
    // brazier 10) within ClassifyTolerance. The torch's band and the brazier's (9.125 .. 10.875) are
    // 0.425 apart, so a torch never reads as a brazier.
    public const float TorchIntensity = 2.1f;
    public const float TorchRadius = 8f;
    public const float LanternIntensity = 1.1f;
    public const float LanternRadius = 6f;

    // Peak deviation of the torch's flicker from 1.
    public const float TorchFlickerAmplitude = FireFlicker.TorchAmplitude;

    // The largest TorchIntensity whose fire halo's red channel still fits in a byte at the flicker's
    // peak. DERIVED, so raising one of the constants it depends on cannot silently re-break the
    // clipping: HeldLightCatalogTests pins TorchIntensity <= this.
    public static readonly float MaxUnclippedTorchIntensity =
        1f / (FxSimulation.HeldHaloTint.X * (1f + TorchFlickerAmplitude));

    // The same for the authored brazier (PlacedLightIndex.BrazierColor.X is its intensity), whose
    // halo uses the lamp tint.
    public static readonly float MaxUnclippedBrazierIntensity =
        1f / (FxSimulation.LampHaloTint.X * (1f + FireFlicker.BrazierAmplitude));

    // The largest brightness the torch FLAME sprite can carry before a channel exceeds a byte: its
    // channels are cull * flameNight * brightness * tint, all factors <= 1 at night, so it is the
    // reciprocal of the largest per-channel gain (1.0 today).
    public static readonly float MaxUnclippedFlameBrightness = 1f / FxSimulation.FlameMaxChannelGain;

    // The flame's brightness at the flicker peak: a margin UNDER that ceiling, never AT it. Touching
    // the ceiling pinned the byte for every flicker >= 1 (half the cycle); with the margin, the byte at
    // the peak is 247 and each of the 2A-wide flicker steps is a distinct byte
    // (FxSimulation.FlameBrightnessAt maps [1 - A, 1 + A] linearly onto [0.75, this]).
    public const float FlameSaturationMargin = 0.03f;
    public static readonly float FlameBrightnessCeiling = MaxUnclippedFlameBrightness * (1f - FlameSaturationMargin);

    public static readonly Vector3 TorchColor = new Vector3(1.0f, 0.62f, 0.32f) * TorchIntensity;
    public static readonly Vector3 LanternColor = new Vector3(1.0f, 0.80f, 0.55f) * LanternIntensity;

    // The base color and radius of the kind; false for None (or an unknown value).
    public static bool TryResolve(HeldLightKind kind, out Vector3 color, out float radius)
    {
        switch (kind)
        {
            case HeldLightKind.Torch:
                color = TorchColor;
                radius = TorchRadius;
                return true;
            case HeldLightKind.Lantern:
                color = LanternColor;
                radius = LanternRadius;
                return true;
            default:
                color = default;
                radius = 0f;
                return false;
        }
    }

    // Intensity multiplier at the client time, within 1 +- TorchFlickerAmplitude; the seed keeps
    // neighbouring torches out of step.
    public static float Flicker(HeldLightKind kind, double timeSeconds, int seed)
    {
        return Sample(kind, Vector3.Zero, timeSeconds, seed).Intensity;
    }

    // The light of the kind at the anchor; false for None. The light's radius and position carry the
    // flicker (radius scale and the flame's wobble), its colour the intensity.
    public static bool TryCreate(HeldLightKind kind, Vector3 position, double timeSeconds, int seed, out PointLight light)
    {
        if (!TryResolve(kind, out Vector3 color, out float radius))
        {
            light = default;
            return false;
        }

        FireFlickerSample sample = Sample(kind, position, timeSeconds, seed);
        // A held light's colour must NEVER be scaled after this point (no daylight factor, no fade):
        // FireFlicker.IntensityOf recovers the torch's flicker by reading Color.X / TorchColor.X back,
        // clamped to [1 - A, 1 + A]. A scaled colour would not fail, it would silently pin the halo's
        // flicker at the floor. Scale a placed fire's colour (the brazier does) but never a held one.
        // The actor id rides along as the light's flicker seed, so a consumer that only has the light
        // reads the very phase this sample was taken at instead of re-deriving one.
        light = new PointLight(position + sample.Offset, radius * sample.RadiusScale, color * sample.Intensity, seed);
        return true;
    }

    private static FireFlickerSample Sample(HeldLightKind kind, Vector3 position, double timeSeconds, int seed)
    {
        FireFlickerKind fire = kind == HeldLightKind.Torch ? FireFlickerKind.HeldTorch : FireFlickerKind.HeldLantern;
        // A zero seed would fall back to the position hash; a player id is never 0 in play, and the
        // fallback is still deterministic.
        return FireFlicker.Sample(fire, position, timeSeconds, seed);
    }
}
