using Dream.Rendering.Lighting;
using Dream.Rendering.Shadows;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Rendering.Shadows;

// A CPU mirror of dreamSunVisibility (shaders/lighting.glsl) over a CPU-rendered depth map, the house pattern for
// shaders (CloudLayer, GrimLookHistogramTests). The scene is analytic: the ground plane y = 0 and one box, so the
// depth map is exact ray casting and every expectation can be stated geometrically.
public class SunShadowLookupMirrorTests
{
    private static readonly Vector3 BoxMin = new(-1f, 0f, -1f);
    private static readonly Vector3 BoxMax = new(1f, 2f, 1f);

    // ---- the mirror ----

    private sealed class DepthMap
    {
        public DepthMap(int resolution)
        {
            Resolution = resolution;
            Depth = new float[resolution * resolution];
        }

        public int Resolution { get; }
        public float[] Depth { get; }

        // GL_CLAMP_TO_BORDER with border (1,1,1,1).
        public float Texel(int x, int y) =>
            x < 0 || y < 0 || x >= Resolution || y >= Resolution ? 1f : Depth[y * Resolution + x];

        // Hardware 2x2 PCF: compare each of the four texels (LEQUAL: ref <= stored is lit), then bilinear-weight.
        public float Compare(Vector2 uv, float reference)
        {
            float fx = uv.X * Resolution - 0.5f;
            float fy = uv.Y * Resolution - 0.5f;
            int x0 = (int)MathF.Floor(fx);
            int y0 = (int)MathF.Floor(fy);
            float wx = fx - x0;
            float wy = fy - y0;
            float a = reference <= Texel(x0, y0) ? 1f : 0f;
            float b = reference <= Texel(x0 + 1, y0) ? 1f : 0f;
            float c = reference <= Texel(x0, y0 + 1) ? 1f : 0f;
            float d = reference <= Texel(x0 + 1, y0 + 1) ? 1f : 0f;
            return MathHelper.Lerp(MathHelper.Lerp(a, b, wx), MathHelper.Lerp(c, d, wx), wy);
        }
    }

    private readonly record struct Biases(bool Constant, bool Slope, bool NormalOffset);

    // Renders the analytic scene into a depth map exactly as the GPU would store it: depth01 along the light ray from
    // the near plane, 1.0 where nothing is hit (the clear value).
    private static DepthMap Render(in ShadowFrame frame, Vector3 toLight, int resolution)
    {
        Matrix4.Invert(in frame.SunLightViewProjection, out Matrix4 inverse);
        DepthMap map = new(resolution);
        for (int y = 0; y < resolution; y++)
        {
            for (int x = 0; x < resolution; x++)
            {
                float ndcX = (x + 0.5f) / resolution * 2f - 1f;
                float ndcY = (y + 0.5f) / resolution * 2f - 1f;
                Vector4 world = new Vector4(ndcX, ndcY, -1f, 1f) * inverse;
                Vector3 origin = world.Xyz / world.W;
                Vector3 direction = -toLight;
                float t = Nearest(origin, direction);
                map.Depth[y * resolution + x] = float.IsPositiveInfinity(t) ? 1f : Math.Clamp(t / frame.SunDepthRange, 0f, 1f);
            }
        }

        return map;
    }

    private static float Nearest(Vector3 origin, Vector3 direction)
    {
        float best = float.PositiveInfinity;
        if (direction.Y < 0f)
        {
            float tGround = -origin.Y / direction.Y;
            if (tGround > 0f)
            {
                best = tGround;
            }
        }

        float tBox = RayBox(origin, direction, BoxMin, BoxMax);
        return MathF.Min(best, tBox);
    }

    private static float RayBox(Vector3 origin, Vector3 direction, Vector3 min, Vector3 max)
    {
        float t0 = float.NegativeInfinity;
        float t1 = float.PositiveInfinity;
        for (int axis = 0; axis < 3; axis++)
        {
            float o = axis == 0 ? origin.X : axis == 1 ? origin.Y : origin.Z;
            float d = axis == 0 ? direction.X : axis == 1 ? direction.Y : direction.Z;
            float lo = axis == 0 ? min.X : axis == 1 ? min.Y : min.Z;
            float hi = axis == 0 ? max.X : axis == 1 ? max.Y : max.Z;
            if (MathF.Abs(d) < 1e-9f)
            {
                if (o < lo || o > hi)
                {
                    return float.PositiveInfinity;
                }

                continue;
            }

            float ta = (lo - o) / d;
            float tb = (hi - o) / d;
            t0 = MathF.Max(t0, MathF.Min(ta, tb));
            t1 = MathF.Min(t1, MathF.Max(ta, tb));
        }

        return t0 <= t1 && t1 > 0f ? MathF.Max(t0, 0f) : float.PositiveInfinity;
    }

    // Line for line dreamSunVisibility with DREAM_SUN_SHADOW_LIVE = 1. The matrix is read as GLSL does:
    // mat4(row0, row1, row2, row3) is four COLUMNS, and M * v = sum of v.i * column i.
    private static float Visibility(in FrameLighting l, DepthMap map, Vector3 fragPos, Vector3 n, Biases use)
    {
        if (l.SunShadowParams.W <= 0f)
        {
            return 1f;
        }

        Vector3 toLight = l.SunDirection.W >= l.MoonDirection.W ? l.SunDirection.Xyz : l.MoonDirection.Xyz;
        float ndl = MathF.Max(Vector3.Dot(n, toLight), 0f);
        float tanT = MathF.Min(MathF.Sqrt(MathF.Max(1f - ndl * ndl, 0f)) / MathF.Max(ndl, ShadowSettings.GrazingCosineFloor), ShadowSettings.SlopeTanMax);
        Vector3 lookup = use.NormalOffset ? fragPos + n * (l.SunShadowParams.Z * tanT) : fragPos;
        Vector4 tc = GlslMat4Times(l.SunShadowMatrix0, l.SunShadowMatrix1, l.SunShadowMatrix2, l.SunShadowMatrix3, new Vector4(lookup, 1f));
        float bias = (use.Constant ? l.SunShadowParams.Y : 0f) * (1f + (use.Slope ? ShadowSettings.SlopePerInverseResolution * l.SunShadowParams.X * tanT : 0f));
        // The hardware clamps the comparison reference to 0..1 before comparing.
        float reference = Math.Clamp(tc.Z - bias, 0f, 1f);

        const int perSide = 3;
        const float spacing = 1.5f;
        float sum = 0f;
        for (int j = 0; j < perSide; j++)
        {
            for (int i = 0; i < perSide; i++)
            {
                Vector2 o = (new Vector2(i, j) - new Vector2(0.5f * (perSide - 1))) * (spacing * l.SunShadowParams.X);
                sum += map.Compare(new Vector2(tc.X + o.X, tc.Y + o.Y), reference);
            }
        }

        return MathHelper.Lerp(1f, sum / (perSide * perSide), Math.Clamp(l.SunShadowParams.W, 0f, 1f));
    }

    private static Vector4 GlslMat4Times(Vector4 c0, Vector4 c1, Vector4 c2, Vector4 c3, Vector4 v) =>
        c0 * v.X + c1 * v.Y + c2 * v.Z + c3 * v.W;

    // ---- the scene ----

    private static readonly Vector3 ToLight = Vector3.Normalize(new Vector3(0.4f, 0.8f, 0.3f));

    private static (FrameLighting Lighting, DepthMap Map) Scene(int resolution, float strength = 1f)
    {
        ShadowFrame frame = ShadowFrame.ForSun(Vector3.Zero, ToLight, ToLight.Y, resolution, strength);
        FrameLighting lighting = FrameLighting.ReferenceNoon;
        lighting.SunDirection = new Vector4(ToLight, 1f);
        lighting.MoonDirection = new Vector4(-ToLight, 0f);
        frame.WriteSunTo(ref lighting);
        return (lighting, Render(in frame, ToLight, resolution));
    }

    private const int Default = 2048;
    private static readonly Lazy<(FrameLighting Lighting, DepthMap Map)> Shared = new(() => Scene(Default));
    private static readonly Biases AllBiases = new(true, true, true);

    // Is the ground point shadowed by the box, by exact geometry? margin > 0 shrinks the box (point must be deeper in shadow).
    private static bool ShadowedByBox(Vector3 groundPoint, float margin)
    {
        float t = RayBox(groundPoint, ToLight, BoxMin + new Vector3(margin), BoxMax - new Vector3(margin));
        return !float.IsPositiveInfinity(t);
    }

    private static bool ShadowedByBoxLoosely(Vector3 groundPoint, float margin)
    {
        float t = RayBox(groundPoint, ToLight, BoxMin - new Vector3(margin), BoxMax + new Vector3(margin));
        return !float.IsPositiveInfinity(t);
    }

    // ---- tests ----

    [Fact]
    public void An_Open_Ground_Point_Is_Fully_Lit_And_The_Ground_Never_Shadows_Itself()
    {
        (FrameLighting lighting, DepthMap map) = Shared.Value;
        int checkedPoints = 0;
        for (float x = -7.9f; x <= 7.9f; x += 0.37f)
        {
            for (float z = -7.9f; z <= 7.9f; z += 0.41f)
            {
                Vector3 p = new(x, 0f, z);
                if (ShadowedByBoxLoosely(p, 0.3f))
                {
                    continue;
                }

                Assert.Equal(1f, Visibility(in lighting, map, p, Vector3.UnitY, AllBiases), 4);
                checkedPoints++;
            }
        }

        Assert.True(checkedPoints > 300);
    }

    [Fact]
    public void A_Ground_Point_Deep_In_The_Boxs_Shadow_Is_Dark()
    {
        (FrameLighting lighting, DepthMap map) = Shared.Value;
        int checkedPoints = 0;
        for (float x = -7.9f; x <= 7.9f; x += 0.13f)
        {
            for (float z = -7.9f; z <= 7.9f; z += 0.13f)
            {
                Vector3 p = new(x, 0f, z);
                // The box itself covers (-1..1)^2 at y 0; its shadow falls away from the light, to -X, -Z.
                if (ShadowedByBox(p, 0.25f) && (x < -1f || z < -1f))
                {
                    Assert.Equal(0f, Visibility(in lighting, map, p, Vector3.UnitY, AllBiases), 4);
                    checkedPoints++;
                }
            }
        }

        Assert.True(checkedPoints > 100);
    }

    [Fact]
    public void The_Contact_Point_Beside_The_Wall_Stays_Dark_The_Bias_Does_Not_Peter_Pan()
    {
        (FrameLighting lighting, DepthMap map) = Shared.Value;
        // 0.15 units (about 1.6 texels here) from the base of the wall facing away from the light.
        Vector3 p = new(-1.15f, 0f, 0f);
        Assert.True(ShadowedByBox(p, 0f));
        Assert.Equal(0f, Visibility(in lighting, map, p, Vector3.UnitY, AllBiases), 4);
    }

    [Fact]
    public void Outside_The_Volume_Reads_Fully_Lit()
    {
        (FrameLighting lighting, DepthMap map) = Shared.Value;
        foreach (Vector3 p in new[] { new Vector3(500f, 0f, 0f), new Vector3(0f, 0f, -900f), new Vector3(-300f, 0f, 300f) })
        {
            Assert.Equal(1f, Visibility(in lighting, map, p, Vector3.UnitY, AllBiases), 6);
        }
    }

    [Fact]
    public void Strength_Zero_Is_A_Literal_One_Even_In_Shadow()
    {
        (FrameLighting lighting, DepthMap map) = Shared.Value;
        lighting.SunShadowParams.W = 0f;
        Assert.Equal(1f, Visibility(in lighting, map, new Vector3(-1.6f, 0f, -1.2f), Vector3.UnitY, AllBiases));
    }

    [Fact]
    public void Strength_Scales_The_Shadow_Toward_Lit()
    {
        (FrameLighting lighting, DepthMap map) = Shared.Value;
        Vector3 inShadow = new(-1.6f, 0f, -1.2f);
        Assert.True(ShadowedByBox(inShadow, 0.25f));

        lighting.SunShadowParams.W = 0.5f;

        Assert.Equal(0.5f, Visibility(in lighting, map, inShadow, Vector3.UnitY, AllBiases), 4);
    }

    // The bias terms are not decoration: with none of them the ground shadows itself (acne). Together they remove it.
    [Theory]
    [InlineData(512)]
    [InlineData(1024)]
    [InlineData(2048)]
    [InlineData(4096)]
    public void Without_Any_Bias_The_Ground_Shows_Acne_And_With_All_Three_It_Does_Not(int resolution)
    {
        (FrameLighting lighting, DepthMap map) = Scene(resolution);
        Biases none = new(false, false, false);
        int acne = 0;
        int acneWithBias = 0;
        int total = 0;
        for (float x = 2.5f; x <= 7.9f; x += 0.211f)
        {
            for (float z = 2.5f; z <= 7.9f; z += 0.197f)
            {
                Vector3 p = new(x, 0f, z);
                total++;
                if (Visibility(in lighting, map, p, Vector3.UnitY, none) < 0.99f)
                {
                    acne++;
                }

                if (Visibility(in lighting, map, p, Vector3.UnitY, AllBiases) < 0.99f)
                {
                    acneWithBias++;
                }
            }
        }

        Assert.True(acne > total / 10, $"Expected visible acne without bias, got {acne} of {total}.");
        Assert.Equal(0, acneWithBias);
    }

    // Each term earns its keep: the normal offset and the slope term each remove acne the constant bias alone leaves.
    [Theory]
    [InlineData(512)]
    [InlineData(1024)]
    [InlineData(2048)]
    [InlineData(4096)]
    public void The_Constant_Bias_Alone_Is_Not_Enough_At_A_Grazing_Sun(int resolution)
    {
        Vector3 grazing = Vector3.Normalize(new Vector3(0.9f, 0.18f, 0.4f));
        ShadowFrame frame = ShadowFrame.ForSun(Vector3.Zero, grazing, grazing.Y, resolution, 1f);
        FrameLighting lighting = FrameLighting.ReferenceNoon;
        lighting.SunDirection = new Vector4(grazing, 1f);
        lighting.MoonDirection = new Vector4(-grazing, 0f);
        frame.WriteSunTo(ref lighting);
        DepthMap map = RenderGroundOnly(in frame, grazing, resolution);

        int constantOnly = 0;
        int all = 0;
        for (float x = -6.1f; x <= 6.1f; x += 0.173f)
        {
            for (float z = -6.1f; z <= 6.1f; z += 0.167f)
            {
                Vector3 p = new(x, 0f, z);
                if (Visibility(in lighting, map, p, Vector3.UnitY, new Biases(true, false, false)) < 0.99f)
                {
                    constantOnly++;
                }

                if (Visibility(in lighting, map, p, Vector3.UnitY, AllBiases) < 0.99f)
                {
                    all++;
                }
            }
        }

        Assert.True(constantOnly > 0, "A grazing sun should defeat the constant bias alone.");
        Assert.Equal(0, all);
    }

    private static DepthMap RenderGroundOnly(in ShadowFrame frame, Vector3 toLight, int resolution)
    {
        Matrix4.Invert(in frame.SunLightViewProjection, out Matrix4 inverse);
        DepthMap map = new(resolution);
        for (int y = 0; y < resolution; y++)
        {
            for (int x = 0; x < resolution; x++)
            {
                Vector4 world = new Vector4((x + 0.5f) / resolution * 2f - 1f, (y + 0.5f) / resolution * 2f - 1f, -1f, 1f) * inverse;
                Vector3 origin = world.Xyz / world.W;
                float t = -origin.Y / -toLight.Y;
                map.Depth[y * resolution + x] = t > 0f ? Math.Clamp(t / frame.SunDepthRange, 0f, 1f) : 1f;
            }
        }

        return map;
    }

    // ---- the row/column convention ----

    [Fact]
    public void The_Four_Csharp_Rows_Used_As_Glsl_Columns_Reproduce_The_Row_Vector_Product_With_No_Transpose()
    {
        // A deliberately non-symmetric matrix, so a transpose bug cannot hide.
        Matrix4 m = Matrix4.CreateRotationX(0.4f) * Matrix4.CreateScale(1f, 2f, 3f) * Matrix4.CreateTranslation(5f, -6f, 7f);
        Vector4 v = new(1.5f, -2.5f, 3.5f, 1f);
        FrameLighting l = default;
        l.SunShadowMatrix = m;

        Vector4 csharp = v * m;
        Vector4 glsl = GlslMat4Times(l.SunShadowMatrix0, l.SunShadowMatrix1, l.SunShadowMatrix2, l.SunShadowMatrix3, v);
        Assert.Equal(csharp.X, glsl.X, 4);
        Assert.Equal(csharp.Y, glsl.Y, 4);
        Assert.Equal(csharp.Z, glsl.Z, 4);
        Assert.Equal(csharp.W, glsl.W, 4);

        // Sensitivity: uploading the TRANSPOSED rows would give a different answer, so this test would catch it.
        Matrix4 t = Matrix4.Transpose(m);
        Vector4 wrong = GlslMat4Times(t.Row0, t.Row1, t.Row2, t.Row3, v);
        Assert.True((wrong - csharp).Length > 1f);

        // The struct round trip is lossless and row-ordered.
        Assert.Equal(m, l.SunShadowMatrix);
        Assert.Equal(m.Row2, l.SunShadowMatrix2);
    }

    [Fact]
    public void The_Packed_Texture_Matrix_Takes_A_World_Point_To_The_Same_Place_As_The_Clip_Matrix()
    {
        ShadowFrame frame = ShadowFrame.ForSun(new Vector3(4f, 0f, 9f), ToLight, ToLight.Y, 2048, 1f);
        FrameLighting l = default;
        frame.WriteSunTo(ref l);
        Vector3 p = new(5f, 1.2f, 7f);

        Vector4 clip = new Vector4(p, 1f) * frame.SunLightViewProjection;
        Vector4 tex = GlslMat4Times(l.SunShadowMatrix0, l.SunShadowMatrix1, l.SunShadowMatrix2, l.SunShadowMatrix3, new Vector4(p, 1f));

        Assert.Equal(clip.X * 0.5f + 0.5f, tex.X, 5);
        Assert.Equal(clip.Y * 0.5f + 0.5f, tex.Y, 5);
        Assert.Equal(clip.Z * 0.5f + 0.5f, tex.Z, 5);
        Assert.Equal(1f, tex.W, 5);
    }
}
