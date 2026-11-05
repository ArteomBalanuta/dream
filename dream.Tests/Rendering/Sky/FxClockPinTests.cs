using Dream.Rendering.Core;
using Dream.Rendering.Mist;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Rendering.Sky;

public class FxClockPinTests
{
    private static BoundaryMistInput At(double t) => new(48f, 48f, t, true);

    [Fact]
    public void With_A_Pin_Two_Consecutive_Frames_Give_The_Same_Mist_Time()
    {
        BoundaryMistFrame a = BoundaryMistFrame.ForWorld(FxClockPin.Apply(At(10.0), 12.0), Vector3.Zero);
        BoundaryMistFrame b = BoundaryMistFrame.ForWorld(FxClockPin.Apply(At(10.016), 12.0), Vector3.Zero);

        Assert.Equal(12f, a.Params.Y);
        Assert.Equal(a.Params.Y, b.Params.Y);
        Assert.Equal(a.Bounds, b.Bounds);
    }

    [Fact]
    public void Without_A_Pin_The_Mist_Time_Still_Advances_And_The_Input_Is_Untouched()
    {
        BoundaryMistInput first = At(10.0);
        BoundaryMistInput second = At(10.016);

        Assert.Equal(first, FxClockPin.Apply(first, null));
        BoundaryMistFrame a = BoundaryMistFrame.ForWorld(FxClockPin.Apply(first, null), Vector3.Zero);
        BoundaryMistFrame b = BoundaryMistFrame.ForWorld(FxClockPin.Apply(second, null), Vector3.Zero);
        Assert.True(b.Params.Y > a.Params.Y);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("abc", null)]
    [InlineData("NaN", null)]
    [InlineData("12.0", 12.0)]
    [InlineData("0", 0.0)]
    public void The_Variable_Parses_To_A_Finite_Number_Or_No_Pin(string? text, double? expected)
    {
        Assert.Equal(expected, FxClockPin.Parse(text));
    }
}
