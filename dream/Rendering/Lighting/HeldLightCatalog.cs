using OpenTK.Mathematics;
using Dream.Rendering.Effects;
using Dream.Simulation.Inventory;

namespace Dream.Rendering.Lighting;

// The point light each HeldLightKind casts (wave T2). Colors are linear RGB already multiplied by
// intensity, as PointLight expects. The torch flickers a little, driven only by client time and a
// per-player seed, so it is deterministic and costs no state; the lantern burns steady.
public static class HeldLightCatalog
{
    // Raised from 1.6 toward the brazier's 3.0 but held under MaxUnclippedTorchIntensity. The actor
    // transfer function is strictly linear up to the output clamp (no tonemap, no gamma encode), so
    // a self-lit figure's internal spread scales with the gain: MEASURED through enginelook.py (the
    // same instrument the M3 gate uses) the shipped figure under a held torch at 0.4 m spreads
    // 0.262-0.338 at 2.1 against 0.286-0.369 at 2.3, a factor of 0.916 - and neither reaches A3's
    // 0.45, which is why M1 is captured at a BRAZIER 1.2-2.0 m away, a light this constant does not
    // touch. What the torch does carry is A1's bright tail, which is thin.
    // It stops there because of the fire halo, NOT because of the brazier: the halo packs
    // light.Color * HeldHaloTint * gain into a byte, and TWO flickers are already in that product -
    // the light's own (TryCreate returns color * Flicker, peak 1.08) and the halo's (peak 1.15).
    // Peak red at 2.1 is 2.1 * 0.375 * 1.08 * 1.15 = 0.979, just under the byte; at 2.3 it was
    // 1.071, so the red channel pinned flat at flicker peaks and the halo stopped responding to its
    // own flicker - the exact failure the ceiling exists to avoid.
    // The RADIUS stays 8: BrazierRadius and LampRadius are both 10, and that radius is what
    // FxSimulation.TryClassify matches for the fire halo, so drifting the torch onto it would give
    // every torch a brazier halo.
    public const float TorchIntensity = 2.1f;
    public const float TorchRadius = 8f;
    public const float LanternIntensity = 1.1f;
    public const float LanternRadius = 6f;

    // Peak deviation of the torch's flicker from 1 (intensity only; the radius stays fixed).
    public const float TorchFlickerAmplitude = 0.08f;

    // The largest TorchIntensity whose fire halo's red channel still fits in a byte at both
    // flickers' peaks. DERIVED here instead of written into the comment above, so raising one of
    // the three constants it depends on cannot silently re-break the clipping it guards against:
    // HeldLightCatalogTests pins TorchIntensity <= this.
    public static readonly float MaxUnclippedTorchIntensity =
        1f / (FxSimulation.HeldHaloTint.X * (1f + TorchFlickerAmplitude) * FxSimulation.TorchHaloFlickerPeak);

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

    // Intensity multiplier at the client time: two incommensurate sines, phase-shifted by the seed
    // so neighbouring torches do not pulse in step. Always within 1 ± TorchFlickerAmplitude.
    public static float Flicker(HeldLightKind kind, double timeSeconds, int seed)
    {
        if (kind != HeldLightKind.Torch)
        {
            return 1f;
        }

        double phase = (seed & 0xFFFF) * 0.6180339887;
        double wave = 0.6 * Math.Sin(timeSeconds * 11.3 + phase) + 0.4 * Math.Sin(timeSeconds * 17.9 + phase * 2.3);
        return 1f + TorchFlickerAmplitude * (float)wave;
    }

    // The light of the kind at the anchor; false for None.
    public static bool TryCreate(HeldLightKind kind, Vector3 position, double timeSeconds, int seed, out PointLight light)
    {
        if (!TryResolve(kind, out Vector3 color, out float radius))
        {
            light = default;
            return false;
        }

        light = new PointLight(position, radius, color * Flicker(kind, timeSeconds, seed));
        return true;
    }
}
