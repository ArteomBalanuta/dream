using Dream.Rendering.Lighting;
using Dream.Rendering.World;
using OpenTK.Mathematics;

namespace Dream.Rendering.Effects;

public enum FireFlickerKind
{
    HeldTorch,
    HeldLantern,
    Brazier,
    Lamp
}

// One flicker sample: the multiplier of the light's intensity (and of everything that must flicker
// with it), the multiplier of its radius, and a small positional wobble of the flame.
public readonly record struct FireFlickerSample(float Intensity, float RadiusScale, Vector3 Offset);

// The one flicker of every fire: the light, its halo, its flame and the shadows it casts all read
// this, so they are in phase and the halo's one-byte colour carries ONE flicker factor, not two.
// Pure, static, allocation-free, no GL.
//
// Intensity is 1 + A * (four octaves whose weights sum to exactly 1), then a gust may pull it down
// toward the floor 1 - A. It therefore always lies in [1 - A, 1 + A], and 1 + A is the peak the
// halo's byte ceiling (HeldLightCatalog.MaxUnclipped*Intensity) is derived from. Gusts only reduce;
// that single rule keeps the ceiling valid.
//
// A light that is only seen as a PointLight (FxSimulation, FxShadowList) re-derives its phase from
// its position, quantised to PhaseGrid so a wobble of the offset cannot change it. Held lights pass
// their actor id as the seed instead.
//
// PUBLIC CONTRACT for a pass that only has a PointLight and the clock (a shadow pass, the FX
// halos):
//   * Sample(kind, position, clockSeconds, seed) - public; seed 0 derives the phase from the
//     position quantised to PhaseGrid (0.5), so a light moved by its own Offset (<= 0.02) keeps its phase.
//   * TryClassifyRadius(radius, out kind) - the radius bands (nearest class centre within a
//     tolerance; torch 8 +-0.75, lamp/brazier 10 +-0.95, lantern 6 +-0.05) of the radius-tagged
//     lights; lamp and brazier are the same band and the same flicker.
//   * IntensityOf(in PointLight, clockSeconds) - the current intensity multiplier of a light:
//     placed fires from Sample at the light's position; a held torch (whose seed a PointLight does
//     not carry) read back from its colour, so it is exactly in phase with the light itself.
public static class FireFlicker
{
    public const float TorchClassTolerance = 0.75f;
    public const float LampClassTolerance = 0.95f;
    public const float LanternClassTolerance = 0.05f;

    public const float TorchAmplitude = 0.25f;
    public const float BrazierAmplitude = 0.25f;
    public const float LampAmplitude = 0.25f;
    public const float LanternAmplitude = 0f;

    // Octave weights (sum exactly 1) and frequencies in Hz.
    public const float Weight0 = 0.42f;
    public const float Weight1 = 0.27f;
    public const float Weight2 = 0.19f;
    public const float Weight3 = 0.12f;
    public const float OctaveWeightSum = Weight0 + Weight1 + Weight2 + Weight3;
    public const double Frequency0 = 1.7;
    public const double Frequency1 = 3.1;
    public const double Frequency2 = 7.3;
    public const double Frequency3 = 13.9;

    public const float PhaseGrid = 0.5f;
    public const float GustDepth = 0.35f;
    public const double GustSeconds = 0.25;
    public const double GustWindowSeconds = 1.3;
    public const float GustChance = 0.30f;
    public const float RadiusFollow = 0.35f;
    public const float OffsetLateral = 0.02f;
    public const float OffsetVertical = 0.015f;

    // Which fire a light of this radius is, by the nearest class radius within that class's
    // tolerance. False for any other radius (flash lights, debug rings).
    public static bool TryClassifyRadius(float radius, out FireFlickerKind kind)
    {
        kind = default;
        bool found = false;
        float best = float.MaxValue;
        Consider(radius, LampLightIndex.LampRadius, LampClassTolerance, FireFlickerKind.Lamp, ref best, ref kind, ref found);
        Consider(radius, HeldLightCatalog.TorchRadius, TorchClassTolerance, FireFlickerKind.HeldTorch, ref best, ref kind, ref found);
        Consider(radius, HeldLightCatalog.LanternRadius, LanternClassTolerance, FireFlickerKind.HeldLantern, ref best, ref kind, ref found);
        return found;
    }

    private static void Consider(
        float radius, float centre, float tolerance, FireFlickerKind candidate, ref float best, ref FireFlickerKind kind, ref bool found)
    {
        float distance = MathF.Abs(radius - centre);
        if (distance <= tolerance && distance < best)
        {
            best = distance;
            kind = candidate;
            found = true;
        }
    }

    // The current intensity multiplier of a classified light (1 when it is not a fire).
    public static float IntensityOf(in PointLight light, double clockSeconds)
    {
        if (!TryClassifyRadius(light.Radius, out FireFlickerKind kind))
        {
            return 1f;
        }

        switch (kind)
        {
            case FireFlickerKind.Lamp:
            case FireFlickerKind.Brazier:
                return Sample(kind, light.Position, clockSeconds, 0).Intensity;
            case FireFlickerKind.HeldTorch:
                float ratio = light.Color.X / HeldLightCatalog.TorchColor.X;
                return float.IsFinite(ratio) ? Math.Clamp(ratio, 1f - TorchAmplitude, 1f + TorchAmplitude) : 1f;
            default:
                return 1f;
        }
    }

    public static float Amplitude(FireFlickerKind kind) => kind switch
    {
        FireFlickerKind.HeldTorch => TorchAmplitude,
        FireFlickerKind.Brazier => BrazierAmplitude,
        FireFlickerKind.Lamp => LampAmplitude,
        _ => LanternAmplitude
    };

    public static FireFlickerSample Sample(FireFlickerKind kind, Vector3 position, double clockSeconds, int seed)
    {
        float amplitude = Amplitude(kind);
        if (amplitude <= 0f || !double.IsFinite(clockSeconds))
        {
            return new FireFlickerSample(1f, 1f, Vector3.Zero);
        }

        double phase = Phase(position, seed);
        double t = clockSeconds;
        const double Tau = Math.PI * 2.0;
        double wave =
            Weight0 * Math.Sin(Tau * Frequency0 * t + phase)
            + Weight1 * Math.Sin(Tau * Frequency1 * t + 1.7 * phase)
            + Weight2 * Math.Sin(Tau * Frequency2 * t + 2.9 * phase)
            + Weight3 * Math.Sin(Tau * Frequency3 * t + 4.3 * phase);
        float f = 1f + amplitude * (float)wave;

        // Gusts only reduce, never below the floor, so the result stays in [1 - A, 1 + A].
        f *= 1f - GustDepth * Gust(t, seed);
        f = Math.Clamp(f, 1f - amplitude, 1f + amplitude);

        float radiusScale = 1f + RadiusFollow * (f - 1f);
        float lateral = OffsetLateral * (float)Math.Sin(Tau * 2.9 * t + 3.1 * phase);
        float lateral2 = OffsetLateral * (float)Math.Sin(Tau * 2.9 * t + 3.1 * phase + 1.9);
        float vertical = OffsetVertical * (float)Math.Sin(Tau * 4.7 * t + 0.7 * phase);
        Vector3 offset = new(lateral, vertical, lateral2);
        if (seed == 0)
        {
            // The light is placed at position + Offset, and its halo (FxSimulation) re-derives the
            // phase from THAT position. Keep the offset inside the phase cell of the un-offset
            // position, so both quantise to the same cell and agree whatever the position is, even
            // one a hair from a cell boundary (a damaged lamp's centroid can land exactly on one).
            offset = KeepInPhaseCell(position, offset);
        }

        return new FireFlickerSample(f, radiusScale, offset);
    }

    private static int Cell(float v) => (int)MathF.Floor((v / PhaseGrid) + 0.5f);

    private static Vector3 KeepInPhaseCell(Vector3 position, Vector3 offset)
    {
        // Checked on the float sum the callers actually compute, so rounding cannot disagree.
        if (Cell(position.X + offset.X) != Cell(position.X)) offset.X = 0f;
        if (Cell(position.Y + offset.Y) != Cell(position.Y)) offset.Y = 0f;
        if (Cell(position.Z + offset.Z) != Cell(position.Z)) offset.Z = 0f;
        return offset;
    }

    // 0..1 raised-cosine pulse of a gust, 0 outside it.
    private static float Gust(double t, int seed)
    {
        double window = Math.Floor(t / GustWindowSeconds);
        int index = unchecked((int)(long)window);
        if (Unit(Hash(index, seed, 0x51ED270B)) >= GustChance)
        {
            return 0f;
        }

        double start = window * GustWindowSeconds + Unit(Hash(index, seed, 0x2F6A1C95)) * (GustWindowSeconds - GustSeconds);
        double u = (t - start) / GustSeconds;
        if (u < 0.0 || u >= 1.0)
        {
            return 0f;
        }

        return (float)(0.5 * (1.0 - Math.Cos(Math.PI * 2.0 * u)));
    }

    private static double Phase(Vector3 position, int seed)
    {
        uint h;
        if (seed != 0)
        {
            h = Hash(seed, 0x68E31DA4, 0x1B56C4E9);
        }
        else
        {
            int qx = Cell(position.X);
            int qy = Cell(position.Y);
            int qz = Cell(position.Z);
            h = Hash(qx, qz, qy * 7919 + 0x3C6EF372);
        }

        return Unit(h) * Math.PI * 2.0;
    }

    private static double Unit(uint h) => h / 4294967296.0;

    private static uint Hash(int a, int b, int c)
    {
        unchecked
        {
            uint h = (uint)a * 0x9E3779B1u;
            h ^= (uint)b * 0x85EBCA77u;
            h ^= (uint)c * 0xC2B2AE3Du;
            h ^= h >> 15;
            h *= 0x2C1B3C6Du;
            h ^= h >> 12;
            h *= 0x297A2D39u;
            h ^= h >> 15;
            return h;
        }
    }
}
