using Xunit;
using Dream.Rendering.Effects;

namespace Dream.Tests.Rendering.Effects;

public class FireClockPinTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("garbage")]
    [InlineData("inf")]
    [InlineData("Infinity")]
    [InlineData("NaN")]
    public void ParseRejectsUnsetBlankGarbageAndNonFinite(string? text) => Assert.Null(FireClockPin.Parse(text));

    [Fact]
    public void ParseReadsInvariantFloat()
    {
        Assert.Equal(10d, FireClockPin.Parse("10"));
        Assert.Equal(2.5d, FireClockPin.Parse("2.5"));
    }

    [Fact]
    public void ResolveReturnsThePinOrTheLiveClock()
    {
        Assert.Equal(10d, FireClockPin.Resolve(123.4, 10d));
        Assert.Equal(123.4d, FireClockPin.Resolve(123.4, null));
    }

    // Each file's fire-flicker call passes FireClockSeconds; the helper itself is defined in Effects.cs.
    private static readonly string[] CallSiteFiles =
    {
        "PlayScene.PlacedLights.cs", "PlayScene.LampLights.cs", "PlayScene.HeldLights.cs", "PlayScene.Effects.cs",
    };

    private const string HelperBody = "FireClockPin.Resolve(_clientPresentationTimeSeconds)";

    // Call sites = uses of FireClockSeconds other than its own definition.
    private static int CallSites(string code) =>
        code.Replace("double FireClockSeconds =>", "").Split("FireClockSeconds").Length - 1;

    [Fact]
    public void EveryFireFlickerCallSiteUsesThePinnedClock()
    {
        foreach (string file in CallSiteFiles)
        {
            Assert.Equal(1, CallSites(Scene(file)));
        }

        Assert.Contains(HelperBody, Scene("PlayScene.Effects.cs"));
    }

    [Fact]
    public void AttackTimingAndGalleryKeyStayOnTheLiveClock()
    {
        string code = Scene("PlayScene.HeldLights.cs");
        Assert.Contains("_clientPresentationTimeSeconds - _localLastAttackSeconds >= LocalAttackRepeatSeconds", code);
        Assert.Contains("_localLastAttackSeconds = _clientPresentationTimeSeconds;", code);
        Assert.Contains("LoopbackGalleryVisualKey((float)_clientPresentationTimeSeconds", code);
    }

    [Fact]
    public void SyntheticNegativeMutatedSourceIsRejected()
    {
        foreach (string file in CallSiteFiles)
        {
            string mutated = Scene(file).Replace("FireClockSeconds", "_clientPresentationTimeSeconds");
            Assert.NotEqual(1, CallSites(mutated));
        }
    }

    private static string Scene(string file) => File.ReadAllText(Path.Combine(RepoRoot(), "dream", "Client", "Scenes", file));

    private static string RepoRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "dream", "dream.csproj")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
