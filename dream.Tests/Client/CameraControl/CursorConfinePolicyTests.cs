using Dream.Client.CameraControl;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Client.CameraControl;

public class CursorConfinePolicyTests
{
    [Theory]
    [InlineData(CameraConfineMode.Off, false, false)]
    [InlineData(CameraConfineMode.FullscreenOnly, false, false)]
    [InlineData(CameraConfineMode.FullscreenOnly, true, true)]
    [InlineData(CameraConfineMode.Always, false, true)]
    [InlineData(CameraConfineMode.Always, true, true)]
    public void Mode_truth_table(CameraConfineMode mode, bool fullscreenLike, bool expected) =>
        Assert.Equal(expected, CursorConfinePolicy.ShouldConfine(mode, true, false, fullscreenLike, false, false, true));

    [Theory]
    [InlineData(CameraConfineMode.FullscreenOnly)]
    [InlineData(CameraConfineMode.Always)]
    public void Each_blocker_releases(CameraConfineMode mode)
    {
        Assert.False(CursorConfinePolicy.ShouldConfine(mode, false, false, true, false, false, true));
        Assert.False(CursorConfinePolicy.ShouldConfine(mode, true, true, true, false, false, true));
        Assert.False(CursorConfinePolicy.ShouldConfine(mode, true, false, true, true, false, true));
        Assert.False(CursorConfinePolicy.ShouldConfine(mode, true, false, true, false, true, true));
        Assert.False(CursorConfinePolicy.ShouldConfine(mode, true, false, true, false, false, false));
    }

    [Fact]
    public void Null_confiner_never_confines_and_records()
    {
        NullCursorConfiner confiner = new();
        confiner.Confine(new Box2i(1, 2, 3, 4));
        Assert.False(confiner.IsConfined);
        Assert.Equal(1, confiner.ConfineCalls);
        confiner.Release();
        confiner.Release();
        Assert.Equal(2, confiner.ReleaseCalls);
    }
}
