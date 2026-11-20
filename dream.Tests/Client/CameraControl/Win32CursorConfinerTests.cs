using Dream.Client.CameraControl;
using Xunit;

namespace Dream.Tests.Client.CameraControl;

public class Win32CursorConfinerTests
{
    private static Win32CursorConfiner.Rect R(int l, int t, int r, int b) => new() { Left = l, Top = t, Right = r, Bottom = b };

    [Fact]
    public void OurClip_IsRecognisedOnlyWhenRequestedAndIdentical()
    {
        Win32CursorConfiner.Rect ours = R(10, 20, 810, 620);
        Assert.True(Win32CursorConfiner.IsOurClip(true, ours, R(10, 20, 810, 620)));
        Assert.False(Win32CursorConfiner.IsOurClip(false, ours, R(10, 20, 810, 620)));
    }

    [Theory]
    [InlineData(0, 20, 810, 620)]
    [InlineData(10, 0, 810, 620)]
    [InlineData(10, 20, 0, 620)]
    [InlineData(10, 20, 810, 0)]
    public void AnotherClip_IsNeverOurs(int l, int t, int r, int b)
    {
        Assert.False(Win32CursorConfiner.IsOurClip(true, R(10, 20, 810, 620), R(l, t, r, b)));
    }

    [Fact]
    public void ANewConfiner_IsNotConfinedAndReleaseIsANoOp()
    {
        Win32CursorConfiner confiner = new();
        Assert.False(confiner.IsConfined);
        confiner.Release();
        Assert.False(confiner.IsConfined);
    }
}
