using System.Runtime.InteropServices;
using OpenTK.Mathematics;

namespace Dream.Rendering.Lighting;

// Mirrors the std140 FrameLighting uniform block in shaders/lighting.glsl member for member.
[StructLayout(LayoutKind.Explicit, Size = SizeInBytes)]
public struct FrameLighting
{
    public const int SizeInBytes = 144;
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
    [FieldOffset(128)] public Vector4 Params;         // x exposure, y actor brightness floor, zw 0

    // Today's look matched at the spawn point (5,0.5,5): direction = normalize(lightPos(4,2,2) - spawn).
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
        Params = new Vector4(1f, 0.35f, 0f, 0f),
    };
}
