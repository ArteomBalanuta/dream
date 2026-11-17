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
// The phase is CARRIED, not re-derived. A producer computes the fire's seed once from its base,
// un-offset position (SeedFor) - or, for a held light, from the actor id - and puts it on the
// PointLight. Every consumer that only sees the light reads that same seed, so the producer and the
// consumer stay in step whatever the flame's wander has done to the light's position. A seed of 0
// means "no seed" and falls back to hashing the position quantised to PhaseGrid; that fallback is
// only stable while the light stays inside its own phase cell, which is why production carries a seed.
//
// PUBLIC CONTRACT for a pass that only has a PointLight and the clock (a shadow pass, the FX
// halos):
//   * SeedFor(basePosition) - the stable seed of a fire placed there, never 0.
//   * Sample(kind, position, clockSeconds, seed) - public; with a non-zero seed the result does not
//     depend on position at all. Otherwise position is the fire's base, un-offset position.
//   * TryClassifyRadius(radius, out kind) - the radius bands (nearest class centre within a
//     tolerance; torch 8 +-0.75, lamp/brazier 10 +-0.95, lantern 6 +-0.05) of the radius-tagged
//     lights; lamp and brazier are the same band and the same flicker.
//   * IntensityOf(in PointLight, clockSeconds) - the current intensity multiplier of a light: from
//     its carried seed when it has one; otherwise a held torch is read back from its colour and a
//     placed fire falls back to the hash of the light's own position.
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
    public const float OffsetLateral = 0.12f;
    public const float OffsetVertical = 0.09f;

    // The flame's wander: weights that sum to exactly 1 (so an axis is bounded by its own Offset
    // constant) over three frequencies in Hz that share no short common period, so the path the flame
    // traces does not close on itself the way two sines of one frequency do.
    public const float WanderWeight0 = 0.58f;
    public const float WanderWeight1 = 0.30f;
    public const float WanderWeight2 = 0.12f;
    public const float WanderWeightSum = WanderWeight0 + WanderWeight1 + WanderWeight2;
    public const double WanderFrequency0 = 0.83;
    public const double WanderFrequency1 = 1.97;
    public const double WanderFrequency2 = 3.37;

    // How much of a fire's phase each higher octave carries. Not 1, and not proportional between the
    // octaves, so two fires whose phases differ slightly drift apart octave by octave.
    public const double WanderPhaseSpread1 = 2.3;
    public const double WanderPhaseSpread2 = 3.7;

    /// <summary>
    /// The stable flicker seed of a fire placed at <paramref name="basePosition"/>: the position
    /// quantised to <see cref="PhaseGrid"/> and hashed, never 0. A producer computes it ONCE from the
    /// UN-OFFSET position and carries it on the light, so every consumer reads the same phase whatever
    /// the flame's wander has done to the light's position.
    /// </summary>
    public static int SeedFor(Vector3 basePosition)
    {
        int seed = unchecked((int)PositionHash(basePosition));
        // 0 is reserved for "no seed", so the one cell that would hash to it is folded onto a
        // fixed substitute instead.
        return seed != 0 ? seed : ZeroSeedSubstitute;
    }

    // Any fixed non-zero value: it only has to differ from 0.
    private const int ZeroSeedSubstitute = 0x5F3759DF;

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

        if (light.FlickerSeed != 0)
        {
            // The seed decides the phase, so the light's already-offset position is irrelevant here
            // and this is exactly the sample its producer took.
            return Sample(kind, light.Position, clockSeconds, light.FlickerSeed).Intensity;
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
        // The wander: the same three octaves on every axis, but with per-axis constant phase terms
        // that are not proportional between the axes, so no two axes are a fixed phase shift of each
        // other at a shared frequency and the path the flame traces does not close into an ellipse.
        // Deliberately NOT multiplied by the gust: a pure sum of sines keeps the wander's own shape
        // the thing that moves.
        Vector3 offset = new(
            OffsetLateral * Wander(t, phase, 0.00, 1.27, 2.95),
            OffsetVertical * Wander(t, phase, 1.05, 3.33, 5.50),
            OffsetLateral * Wander(t, phase, 2.09, 4.71, 0.64));

        return new FireFlickerSample(f, radiusScale, offset);
    }

    private static int Cell(float v) => (int)MathF.Floor((v / PhaseGrid) + 0.5f);

    // One axis of the wander, in [-1, 1] because the weights sum to exactly 1, so the axis is bounded
    // by its own Offset constant rather than by a sampled maximum. d0/d1/d2 are this axis's constant
    // phase terms.
    private static float Wander(double t, double phase, double d0, double d1, double d2)
    {
        const double Tau = Math.PI * 2.0;
        return (float)(
            (WanderWeight0 * Math.Sin((Tau * WanderFrequency0 * t) + phase + d0))
            + (WanderWeight1 * Math.Sin((Tau * WanderFrequency1 * t) + (WanderPhaseSpread1 * phase) + d1))
            + (WanderWeight2 * Math.Sin((Tau * WanderFrequency2 * t) + (WanderPhaseSpread2 * phase) + d2)));
    }

    private static uint PositionHash(Vector3 position) =>
        Hash(Cell(position.X), Cell(position.Z), (Cell(position.Y) * 7919) + 0x3C6EF372);

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
            h = PositionHash(position);
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
