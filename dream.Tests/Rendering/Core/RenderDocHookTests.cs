using Dream.Rendering.Core;
using Xunit;

namespace Dream.Tests.Rendering.Core;

public sealed class RenderDocHookTests
{
    [Theory]
    [InlineData(null, true, "renderdoc.dll")]
    [InlineData("1", true, "renderdoc.dll")]
    [InlineData(null, false, null)]
    [InlineData("", false, null)]
    [InlineData("0", false, null)]
    [InlineData("D:\\tools\\renderdoc.dll", false, "D:\\tools\\renderdoc.dll")]
    public void ResolveLibraryPath_FollowsTheTable(string? env, bool loaded, string? expected)
    {
        Assert.Equal(expected, RenderDocHook.ResolveLibraryPath(env, loaded, "C:\\Program Files"));
    }

    [Fact]
    public void ResolveLibraryPath_OneMeansTheDefaultInstall()
    {
        Assert.Equal(Path.Combine("C:\\Program Files", "RenderDoc", "renderdoc.dll"),
            RenderDocHook.ResolveLibraryPath("1", false, "C:\\Program Files"));
    }

    [Fact]
    public void TryAttach_WithNothingConfigured_ReturnsFalse_AndOtherCallsDoNothing()
    {
        Assert.False(RenderDocHook.TryAttach(_ => null));
        Assert.False(RenderDocHook.IsAttached);
        RenderDocHook.TriggerCapture();
        RenderDocHook.OnFrameEnd(0);
    }

    [Fact]
    public void TryAttach_WithAMissingLibrary_ReturnsFalse_WithoutThrowing()
    {
        Assert.False(RenderDocHook.TryAttach(name =>
            name == RenderDocHook.EnvironmentVariable ? "Z:\\nope\\renderdoc.dll" : null));
        Assert.False(RenderDocHook.IsAttached);
    }
}
