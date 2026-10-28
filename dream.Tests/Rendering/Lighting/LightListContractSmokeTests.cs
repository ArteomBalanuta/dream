using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Dream.Rendering.Lighting;
using Xunit;

namespace Dream.Tests.Rendering.Lighting;

// Wave T2 C0: the CPU mirror of the std140 LightList block in shaders/pointLights.glsl is laid out
// as ivec4 count (offset 0), vec4[16] position/radius (offset 16) and vec4[16] color (offset 272).
public class LightListContractSmokeTests
{
    [Fact]
    public void The_Struct_Is_528_Bytes()
    {
        Assert.Equal(528, LightList.SizeInBytes);
        Assert.Equal(LightList.SizeInBytes, Unsafe.SizeOf<LightList>());
        Assert.Equal(LightList.SizeInBytes, Marshal.SizeOf<LightList>());
    }

    [Fact]
    public void The_Fields_Sit_At_The_Std140_Offsets()
    {
        Assert.Equal(0, (int)Marshal.OffsetOf<LightList>(nameof(LightList.Count)));
        Assert.Equal(16, (int)Marshal.OffsetOf<LightList>(nameof(LightList.PositionRadius)));
        Assert.Equal(272, (int)Marshal.OffsetOf<LightList>(nameof(LightList.Color)));
    }

    [Fact]
    public void Each_Inline_Array_Holds_MaxLights_Vec4s()
    {
        Assert.Equal(LightList.MaxLights * 16, Unsafe.SizeOf<LightVector4Array>());

        LightList list = default;
        list.PositionRadius[LightList.MaxLights - 1] = new OpenTK.Mathematics.Vector4(1f, 2f, 3f, 4f);
        Assert.Equal(4f, list.PositionRadius[LightList.MaxLights - 1].W);
    }
}
