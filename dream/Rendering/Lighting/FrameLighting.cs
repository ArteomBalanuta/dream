using System.Runtime.InteropServices;
using OpenTK.Mathematics;

namespace Dream.Rendering.Lighting;

// Mirrors the std140 FrameLighting uniform block in shaders/lighting.glsl member for member (16 x vec4 = 256 bytes).
[StructLayout(LayoutKind.Explicit, Size = SizeInBytes)]
public struct FrameLighting
{
    public const int SizeInBytes = 256;
    public const int BindingPoint = 0;
    public const string BlockName = "FrameLighting";

    [FieldOffset(0)] public Vector4 SunDirection;     // xyz unit toward sun, w intensity
    [FieldOffset(16)] public Vector4 SunColor;        // rgb, w 0
    [FieldOffset(32)] public Vector4 MoonDirection;   // xyz unit toward moon, w intensity
    [FieldOffset(48)] public Vector4 MoonColor;       // rgb, w 0
    [FieldOffset(64)] public Vector4 AmbientSky;      // rgb for up normals
    [FieldOffset(80)] public Vector4 AmbientGround;   // rgb for down normals
    [FieldOffset(96)] public Vector4 FogColor;        // rgb (== sky horizon), w exp2 density per unit
    [FieldOffset(112)] public Vector4 CameraPosition; // xyz the FOG ORIGIN in fragPos space (the isometric focus, NOT the eye; set at upload), w fog start
    [FieldOffset(128)] public Vector4 Params;         // x exposure, y actor brightness floor, z actor ambient scale, w 0
    [FieldOffset(144)] public Vector4 CloudParams;    // x coverage 0..1, y softness 0..1, z shadow strength 0..1, w scroll speed (world units/s)
    // OUTPUT-ONLY: FrameLightingBuffer.ForWorld overwrites this at upload (from the raw clock argument and CloudParams.W),
    // so whatever a caller puts here is discarded. Pinned by FrameLightingContractTests so a feature that sets it upstream fails loudly.
    [FieldOffset(160)] public Vector4 FrameTime;      // x sway clock seconds wrapped at GroundWind.ClockPeriodSeconds, yz cloud drift in noise cells (CloudLayer.ScrollCells), w 0 (all set at upload)

    // The sun/moon shadow map. The matrix maps WORLD to shadow TEXTURE space (xy 0..1, z the stored 0..1 depth;
    // SunShadowVolume.ToTextureSpace). It is carried as four vec4s so every member stays a vec4.
    // CONVENTION: OpenTK uses row vectors (clip = p * M), so these four members are the four ROWS of the C# Matrix4.
    // GLSL mat4(a, b, c, d) takes COLUMNS, and `M * v` computes v.x*a + v.y*b + v.z*c + v.w*d, which is exactly the
    // row-vector product v * M when a..d are the C# rows. So the rows are uploaded as-is, with NO transpose, and the
    // shader builds mat4(uSunShadowMatrix0, uSunShadowMatrix1, uSunShadowMatrix2, uSunShadowMatrix3).
    [FieldOffset(176)] public Vector4 SunShadowMatrix0; // row 0
    [FieldOffset(192)] public Vector4 SunShadowMatrix1; // row 1
    [FieldOffset(208)] public Vector4 SunShadowMatrix2; // row 2
    [FieldOffset(224)] public Vector4 SunShadowMatrix3; // row 3
    [FieldOffset(240)] public Vector4 SunShadowParams;  // x 1 / resolution, y constant bias / depth range, z normal offset in world units, w strength (0 = off)

    // The four members as the C# Matrix4 they were packed from.
    public Matrix4 SunShadowMatrix
    {
        readonly get => new(SunShadowMatrix0, SunShadowMatrix1, SunShadowMatrix2, SunShadowMatrix3);
        set
        {
            SunShadowMatrix0 = value.Row0;
            SunShadowMatrix1 = value.Row1;
            SunShadowMatrix2 = value.Row2;
            SunShadowMatrix3 = value.Row3;
        }
    }

    // The frozen legacy look: the pre-T1 point lights matched at the spawn point (5,0.5,5), direction =
    // normalize(lightPos(4,2,2) - spawn), a sun only about 25 degrees high. It is the renderer's value
    // before the first sky evaluation and the reference of the shader-equivalence tests. The day/night
    // noon is SkyModelKeyframes.Noon (T2d): the same azimuth, a 60 degree sun, retuned ambient.
    public static FrameLighting ReferenceNoon => new()
    {
        SunDirection = new Vector4(-0.28571430f, 0.42857143f, -0.85714287f, 1f),
        SunColor = new Vector4(1f, 1f, 1f, 0f),
        MoonDirection = new Vector4(0.28571430f, -0.42857143f, 0.85714287f, 0f),
        MoonColor = Vector4.Zero,
        AmbientSky = new Vector4(0.2f, 0.2f, 0.2f, 0f),
        AmbientGround = new Vector4(0.2f, 0.2f, 0.2f, 0f),
        FogColor = new Vector4(0.62f, 0.74f, 0.88f, 0f),
        CameraPosition = Vector4.Zero,
        Params = new Vector4(1f, 0.35f, 3.25f, 0f),
        CloudParams = Vector4.Zero,
        FrameTime = Vector4.Zero,
        // Strength 0: no shadow term until a shadow pass fills the members.
        SunShadowMatrix0 = Vector4.Zero,
        SunShadowMatrix1 = Vector4.Zero,
        SunShadowMatrix2 = Vector4.Zero,
        SunShadowMatrix3 = Vector4.Zero,
        SunShadowParams = Vector4.Zero,
    };
}
