using Xunit;
using Dream.Client.Scenes;

namespace Dream.Tests.Client.Scenes;

public class HudCaptureScriptTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NoDirectory_MeansOff(string? directory)
    {
        Assert.Null(HudCaptureScript.Parse(directory, "play"));
    }

    [Fact]
    public void Steps_AreParsedInOrder_CaseInsensitive_AndUnknownOnesDropped()
    {
        HudCaptureScript? script = HudCaptureScript.Parse("out", "Play, inventory,bogus,,admin,settings");

        Assert.NotNull(script);
        Assert.Equal(["play", "inventory", "admin", "settings"], script!.Steps);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("nonsense")]
    public void NoUsableSteps_DefaultsToPlay(string? steps)
    {
        Assert.Equal(["play"], HudCaptureScript.Parse("out", steps)!.Steps);
    }

    [Fact]
    public void Steps_AreCapped()
    {
        string many = string.Join(',', Enumerable.Repeat("play", 100));

        Assert.Equal(HudCaptureScript.MaxSteps, HudCaptureScript.Parse("out", many)!.Steps.Count);
    }

    [Fact]
    public void FilePath_NumbersAndNamesEachStep()
    {
        HudCaptureScript script = HudCaptureScript.Parse("shots", "play,inventory")!;

        Assert.Equal(Path.Combine("shots", "01-play.png"), script.FilePath(0));
        Assert.Equal(Path.Combine("shots", "02-inventory.png"), script.FilePath(1));
    }
}
