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
        Assert.Equal(32, (int)Marshal.OffsetOf<BoundaryMistFrame>(nameof(BoundaryMistFrame.Eye)));
        Assert.Equal(2, BoundaryMistFrame.BindingPoint);
        Assert.Equal("BoundaryMist", BoundaryMistFrame.BlockName);
    }

    [Fact]
    public void The_World_Range_Carries_The_Map_The_Clock_And_The_True_Eye()
    {
        BoundaryMistInput input = new(56f, 40f, 6405d, Enabled: true);

        BoundaryMistFrame frame = BoundaryMistFrame.ForWorld(in input, new Vector3(13.2f, 14.8f, 13.2f));

        Assert.Equal(new Vector4(0f, 0f, 56f, 40f), frame.Bounds);
        Assert.Equal(1f, frame.Params.X);
        Assert.Equal(5f, frame.Params.Y);
        Assert.Equal(BoundaryMistFrame.FloorTopY, frame.Params.Z);
        Assert.Equal(BoundaryMistField.RampStart, frame.Params.W);
        Assert.Equal(new Vector4(13.2f, 14.8f, 13.2f, 0f), frame.Eye);
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
    public void The_View_Model_Range_Keeps_The_Eye_So_Both_Ranges_Differ_Only_In_Strength()
    {
        BoundaryMistInput on = new(56f, 40f, 1f, Enabled: true);
        Vector3 eye = new(1f, 2f, 3f);

        BoundaryMistFrame viewModel = BoundaryMistFrame.ForViewModel(in on, eye);

        Assert.Equal(new Vector4(eye, 0f), viewModel.Eye);
        Assert.Equal(BoundaryMistFrame.ForWorld(in on, eye).Params.Y, viewModel.Params.Y);
    }
}
