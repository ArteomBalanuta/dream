using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Dream.Rendering.Mist;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Rendering.Mist;

// GL is unavailable in tests, so the BoundaryMist uniform block is checked through its CPU mirror.
public class BoundaryMistFrameTests
{
    [Fact]
    public void The_Frame_Is_Three_Vec4_At_Std140_Offsets()
    {
        Assert.Equal(48, BoundaryMistFrame.SizeInBytes);
        Assert.Equal(BoundaryMistFrame.SizeInBytes, Unsafe.SizeOf<BoundaryMistFrame>());
        Assert.Equal(BoundaryMistFrame.SizeInBytes, Marshal.SizeOf<BoundaryMistFrame>());
        Assert.Equal(0, (int)Marshal.OffsetOf<BoundaryMistFrame>(nameof(BoundaryMistFrame.Bounds)));
        Assert.Equal(16, (int)Marshal.OffsetOf<BoundaryMistFrame>(nameof(BoundaryMistFrame.Params)));
        Assert.Equal(32, (int)Marshal.OffsetOf<BoundaryMistFrame>(nameof(BoundaryMistFrame.View)));
        Assert.Equal(2, BoundaryMistFrame.BindingPoint);
        Assert.Equal("BoundaryMist", BoundaryMistFrame.BlockName);
    }

    [Fact]
    public void The_World_Range_Carries_The_Map_The_Clock_And_The_View()
    {
        BoundaryMistInput input = new(56f, 40f, 605f, Enabled: true);

        BoundaryMistFrame frame = BoundaryMistFrame.ForWorld(in input, new Vector3(0f, -2f, 0f));

        Assert.Equal(new Vector4(0f, 0f, 56f, 40f), frame.Bounds);
        Assert.Equal(1f, frame.Params.X);
        Assert.Equal(5f, frame.Params.Y);
        Assert.Equal(BoundaryMistFrame.FloorTopY, frame.Params.Z);
        Assert.Equal(BoundaryMistField.RampStart, frame.Params.W);
        Assert.Equal(new Vector4(0f, -1f, 0f, 0f), frame.View);
    }

    [Fact]
    public void Disabled_Input_Or_No_Map_Uploads_Strength_Zero()
    {
        BoundaryMistInput off = new(56f, 40f, 1f, Enabled: false);
        Assert.Equal(0f, BoundaryMistFrame.ForWorld(in off, -Vector3.UnitZ).Params.X);

        BoundaryMistInput noMap = default;
        Assert.Equal(0f, BoundaryMistFrame.ForWorld(in noMap, -Vector3.UnitZ).Params.X);
    }

    [Fact]
    public void The_View_Model_Range_Has_Strength_Zero_Whatever_The_Input()
    {
        BoundaryMistInput on = new(56f, 40f, 1f, Enabled: true);

        BoundaryMistFrame world = BoundaryMistFrame.ForWorld(in on, -Vector3.UnitZ);
        BoundaryMistFrame viewModel = BoundaryMistFrame.ForViewModel(in on, -Vector3.UnitZ);

        Assert.Equal(1f, world.Params.X);
        Assert.Equal(0f, viewModel.Params.X);
        Assert.Equal(world.Bounds, viewModel.Bounds);
    }

    [Fact]
    public void A_Zero_Front_Falls_Back_To_A_Unit_Vector()
    {
        BoundaryMistInput on = new(56f, 40f, 1f, Enabled: true);
        Vector4 view = BoundaryMistFrame.ForWorld(in on, Vector3.Zero).View;
        Assert.Equal(1f, view.Xyz.Length, 4);
    }
}
