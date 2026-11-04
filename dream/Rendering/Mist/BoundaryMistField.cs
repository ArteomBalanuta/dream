using Dream.Simulation.Players;

namespace Dream.Rendering.Mist;

// The boundary mist wall's opacity curve on the CPU. shaders/boundaryMist.glsl evaluates the same
// curve per fragment (dreamBoundaryOpacity), so the constants here and the GLSL constants must
// agree; a contract test compares them. The wall is opaque at and past the map edge and thins
// out over RampStart metres inside it.
public static class BoundaryMistField
{
    // Inset (metres inside the map edge) beyond which the mist is gone, before the noise term.
    public const float RampStart = 3.5f;

    // Exponent of the ramp: 3 keeps the wall thick near the edge and thin quickly after.
    public const float Gamma = 3f;

    // How far the noise moves the inset (metres, peak) so the wall's inner border churns.
    public const float NoiseAmp = 0.35f;

    // The noise term fades in over this much inset, so the opacity is exactly 1 on the edge.
    public const float NoiseFadeDistance = 0.5f;

    // Seconds after which the shader time wraps. Every lattice shift below is a whole multiple of
    // HashPeriod cells over this period and the hash repeats every HashPeriod cells, so the wrap
    // is invisible.
    public const double TimeWrapSeconds = 6400d;

    // The noise hash repeats every this many lattice cells (the shader takes mod(cell, 64.0)).
    public const int HashPeriod = 64;

    // Lattice cells per second each noise octave drifts along x and z (the shader repeats these).
    // rate x TimeWrapSeconds must be a multiple of HashPeriod on both axes.
    public static readonly (double X, double Z) EdgeRate = (0.20, 0.15);
    public static readonly (double X, double Z) Octave1Rate = (0.12, 0.09);
    public static readonly (double X, double Z) Octave2Rate = (-0.21, 0.28);
    public const double Octave3Rate = 0.4; // along the second axis only

    // How far inside the edge a player is stopped (the shared movement clamp).
    public static float StopInset => Player.EdgeMargin;

    // The signed distance from (x, z) to the nearest edge of the [0, width] x [0, depth] map, positive
    // inside, zero on the edge, negative outside.
    public static float Inset(float x, float z, float width, float depth) =>
        MathF.Min(MathF.Min(x, width - x), MathF.Min(z, depth - z));

    // Opacity 0..1 for an inset `d` and a noise sample n in 0..1. At or past the edge it is 1 for
    // any noise.
    public static float Opacity(float d, float n)
    {
        if (d <= 0f)
        {
            return 1f;
        }

        float fade = Math.Clamp(d / NoiseFadeDistance, 0f, 1f);
        float shifted = d + NoiseAmp * (2f * n - 1f) * fade;
        float ramp = Math.Clamp(1f - shifted / RampStart, 0f, 1f);
        return MathF.Pow(ramp, Gamma);
    }

    // The wrapped shader time for an elapsed time.
    public static float WrapTime(double seconds)
    {
        double wrapped = seconds % TimeWrapSeconds;
        return (float)(wrapped < 0d ? wrapped + TimeWrapSeconds : wrapped);
    }
}
