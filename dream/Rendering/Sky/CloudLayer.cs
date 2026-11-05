using OpenTK.Mathematics;

namespace Dream.Rendering.Sky;

// CPU mirror of dreamCloudCover (shaders/clouds.glsl), so the cloud field is testable without GL.
// CloudLayerTests parse the constants below out of the GLSL and compare the two.
public static class CloudLayer
{
    public const float PlaneY = 60f;
    public static readonly Vector2 Direction = new(0.8f, 0.6f);
    public const float CellsPerUnit = 0.008f;
    public const float Lacunarity = 2f;
    public const float Gain = 0.5f;
    public const float AmplitudeSum = 1.875f;
    // Coverage is a threshold on contrast-stretched noise, NOT the literal fraction of sky covered.
    public const float Contrast = 2f;
    public const float SoftnessScale = 0.5f;
    public const int Octaves = 4;

    // The lattice hash wraps at this many cells, so the whole field repeats every PeriodCells noise cells on
    // both axes at every octave (a power of two that every octave's stretch divides). That is what lets the
    // drift offset be reduced modulo PeriodCells in double precision on the CPU with no visible jump, however
    // long the session runs: the clock itself is never wrapped for clouds.
    public const float PeriodCells = 64f;

    // The drift of the cloud field in noise cells, reduced into [0, PeriodCells) on each axis. This is what
    // the renderer uploads in FrameTime.yz (the shader subtracts it from the plane position in cells).
    // `rawClockSeconds` is the UNWRAPPED clock.
    public static Vector2 ScrollCells(float scrollSpeed, double rawClockSeconds)
    {
        double distanceCells = scrollSpeed * rawClockSeconds * CellsPerUnit;
        return new Vector2(
            (float)PositiveMod(distanceCells * Direction.X, PeriodCells),
            (float)PositiveMod(distanceCells * Direction.Y, PeriodCells));
    }

    // 0 = clear, 1 = solid. `cloudParams` is FrameLighting.CloudParams, `rawClockSeconds` the unwrapped clock.
    public static float Cover(Vector2 planeXZ, Vector4 cloudParams, double rawClockSeconds) =>
        CoverAtOffset(planeXZ, cloudParams, ScrollCells(cloudParams.W, rawClockSeconds));

    // The same with the drift already reduced, i.e. what the shader evaluates from FrameTime.yz.
    public static float CoverAtOffset(Vector2 planeXZ, Vector4 cloudParams, Vector2 scrollCells)
    {
        if (cloudParams.X <= 0f)
        {
            return 0f;
        }

        Vector2 p = (planeXZ * CellsPerUnit) - scrollCells;
        float sum = 0f;
        float amplitude = 1f;
        for (int i = 0; i < Octaves; i++)
        {
            sum += amplitude * ValueNoise(p);
            p = p * Lacunarity + new Vector2(17f, 31f);
            amplitude *= Gain;
        }

        float n = Math.Clamp((sum / AmplitudeSum - 0.5f) * Contrast + 0.5f, 0f, 1f);
        float w = Math.Clamp(cloudParams.Y, 0f, 1f) * SoftnessScale + 0.0005f;
        float center = Mix(1f + w, -w, Math.Clamp(cloudParams.X, 0f, 1f));
        return SmoothStep(center - w, center + w, n);
    }

    // The sun term multiplier at a world position: 1 - strength * cover where the ray toward the sun
    // meets the cloud plane (1 when the sun is too low or there is no cover).
    public static float SunMask(Vector3 worldPos, Vector3 sunDirection, Vector4 cloudParams, double rawClockSeconds)
    {
        if (cloudParams.X <= 0f || sunDirection.Y <= 0.02f)
        {
            return 1f;
        }

        Vector3 onPlane = worldPos + sunDirection * ((PlaneY - worldPos.Y) / sunDirection.Y);
        return 1f - cloudParams.Z * Cover(new Vector2(onPlane.X, onPlane.Z), cloudParams, rawClockSeconds);
    }

    private static double PositiveMod(double x, double m)
    {
        double r = x % m;
        return r < 0d ? r + m : r;
    }

    private static float Fract(float x) => x - MathF.Floor(x);

    private static float Hash(Vector2 p)
    {
        float x = Fract(p.X * 0.1031f);
        float y = Fract(p.Y * 0.1031f);
        float z = Fract(p.X * 0.1031f);
        // p3 += dot(p3, p3.yzx + 33.33)
        float d = x * (y + 33.33f) + y * (z + 33.33f) + z * (x + 33.33f);
        x += d;
        y += d;
        z += d;
        return Fract((x + y) * z);
    }

    private static float ValueNoise(Vector2 p)
    {
        float cx = MathF.Floor(p.X);
        float cy = MathF.Floor(p.Y);
        float fx = p.X - cx;
        float fy = p.Y - cy;
        float ux = fx * fx * (3f - 2f * fx);
        float uy = fy * fy * (3f - 2f * fy);
        float a = Hash(Lattice(cx, cy));
        float b = Hash(Lattice(cx + 1f, cy));
        float c = Hash(Lattice(cx, cy + 1f));
        float d = Hash(Lattice(cx + 1f, cy + 1f));
        return Mix(Mix(a, b, ux), Mix(c, d, ux), uy);
    }

    // mod(cell, PeriodCells) as GLSL's mod: floor-based, so negative cells wrap upward.
    private static Vector2 Lattice(float x, float y) =>
        new(x - (PeriodCells * MathF.Floor(x / PeriodCells)), y - (PeriodCells * MathF.Floor(y / PeriodCells)));

    private static float Mix(float a, float b, float t) => a * (1f - t) + b * t;

    private static float SmoothStep(float edge0, float edge1, float x)
    {
        float s = Math.Clamp((x - edge0) / (edge1 - edge0), 0f, 1f);
        return s * s * (3f - 2f * s);
    }
}
