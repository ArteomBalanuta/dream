using System.Runtime.InteropServices;
using OpenTK.Mathematics;

namespace Dream.Rendering.Lighting;

// Mirrors the std140 FrameLighting uniform block in shaders/lighting.glsl member for member.
[StructLayout(LayoutKind.Explicit, Size = SizeInBytes)]
public struct FrameLighting
{
    public const int SizeInBytes = 176;
    public const int BindingPoint = 0;
    public const string BlockName = "FrameLighting";

    [FieldOffset(0)] public Vector4 SunDirection;     // xyz unit toward sun, w intensity
    [FieldOffset(16)] public Vector4 SunColor;        // rgb, w 0
    [FieldOffset(32)] public Vector4 MoonDirection;   // xyz unit toward moon, w intensity
    [FieldOffset(48)] public Vector4 MoonColor;       // rgb, w 0
    [FieldOffset(64)] public Vector4 AmbientSky;      // rgb for up normals
    [FieldOffset(80)] public Vector4 AmbientGround;   // rgb for down normals
    [FieldOffset(96)] public Vector4 FogColor;        // rgb (== sky horizon), w exp2 density per unit
    [FieldOffset(112)] public Vector4 CameraPosition; // xyz eye in fragPos space (set at upload), w fog start
    [FieldOffset(128)] public Vector4 Params;         // x exposure, y actor brightness floor, z actor ambient scale, w 0
    [FieldOffset(144)] public Vector4 CloudParams;    // x coverage 0..1, y softness 0..1, z shadow strength 0..1, w scroll speed (world units/s)
    [FieldOffset(160)] public Vector4 FrameTime;      // x wrapped clock seconds (set at upload), y global gust 0..1, z 0, w 0

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
    };
}
