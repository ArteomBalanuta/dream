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
    public const float Contrast = 2f;
    public const float SoftnessScale = 0.5f;
    public const int Octaves = 4;

    // 0 = clear, 1 = solid. `cloudParams` is FrameLighting.CloudParams, `clockSeconds` FrameTime.x.
    public static float Cover(Vector2 planeXZ, Vector4 cloudParams, float clockSeconds)
    {
        if (cloudParams.X <= 0f)
        {
            return 0f;
        }

        Vector2 p = (planeXZ - Direction * (cloudParams.W * clockSeconds)) * CellsPerUnit;
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
    public static float SunMask(Vector3 worldPos, Vector3 sunDirection, Vector4 cloudParams, float clockSeconds)
    {
        if (cloudParams.X <= 0f || sunDirection.Y <= 0.02f)
        {
            return 1f;
        }

        Vector3 onPlane = worldPos + sunDirection * ((PlaneY - worldPos.Y) / sunDirection.Y);
        return 1f - cloudParams.Z * Cover(new Vector2(onPlane.X, onPlane.Z), cloudParams, clockSeconds);
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
        float a = Hash(new Vector2(cx, cy));
        float b = Hash(new Vector2(cx + 1f, cy));
        float c = Hash(new Vector2(cx, cy + 1f));
        float d = Hash(new Vector2(cx + 1f, cy + 1f));
        return Mix(Mix(a, b, ux), Mix(c, d, ux), uy);
    }

    private static float Mix(float a, float b, float t) => a * (1f - t) + b * t;

    private static float SmoothStep(float edge0, float edge1, float x)
    {
        float s = Math.Clamp((x - edge0) / (edge1 - edge0), 0f, 1f);
        return s * s * (3f - 2f * s);
    }
}
