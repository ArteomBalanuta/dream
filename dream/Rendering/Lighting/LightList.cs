using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using OpenTK.Mathematics;

namespace Dream.Rendering.Lighting;

// Fixed-size std140 vec4[LightList.MaxLights] array.
[InlineArray(LightList.MaxLights)]
public struct LightVector4Array
{
    private Vector4 _element0;
}

// Mirrors the std140 LightList uniform block in shaders/pointLights.glsl member for member.
[StructLayout(LayoutKind.Sequential)]
public struct LightList
{
    public const int MaxLights = 16;
    public const int SizeInBytes = 16 + 2 * MaxLights * 16; // 528
    public const int BindingPoint = 1;                     // FrameLighting is 0
    public const string BlockName = "LightList";

    public int Count; private int _pad0, _pad1, _pad2;     // ivec4 uPointLightCount (x)
    public LightVector4Array PositionRadius;               // offset 16: xyz in fragPos space, w radius
    public LightVector4Array Color;                        // offset 272: rgb*intensity, w 0
}
