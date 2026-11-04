using OpenTK.Mathematics;
using Dream.Simulation.Inventory;

namespace Dream.Rendering.Lighting;

// The point light each HeldLightKind casts (wave T2). Colors are linear RGB already multiplied by
// intensity, as PointLight expects. The torch flickers a little, driven only by client time and a
// per-player seed, so it is deterministic and costs no state; the lantern burns steady.
public static class HeldLightCatalog
{
    // Raised from 1.6 toward the brazier's 3.0, and stopped just short of it: at 1.6 a self-lit
    // figure's internal spread was 0.351 against A3's 0.45 gate, and the actor transfer function is
    // strictly linear (no tonemap, no gamma encode), so the spread scales with the gain - 2.3 gives
    // a DERIVED 0.505. It does not go to 3.0 because the fire halo packs
    // light.Color * HeldHaloTint (0.375 red) into a byte: 3.0 * 0.375 = 1.125 clips the halo's red
    // channel flat, and at 2.3 the halo still has headroom under its +-15% flicker.
    // The RADIUS stays 8: BrazierRadius and LampRadius are both 10, and that radius is what
    // FxSimulation.TryClassify matches for the fire halo, so drifting the torch onto it would give
    // every torch a brazier halo.
    public const float TorchIntensity = 2.3f;
    public const float TorchRadius = 8f;
    public const float LanternIntensity = 1.1f;
    public const float LanternRadius = 6f;

    // Peak deviation of the torch's flicker from 1 (intensity only; the radius stays fixed).
    public const float TorchFlickerAmplitude = 0.08f;

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
