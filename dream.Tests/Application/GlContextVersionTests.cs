using System.IO;
using Dream.Application;
using OpenTK.Windowing.GraphicsLibraryFramework;
using Xunit;

namespace Dream.Tests.Application;

public class GlContextVersionTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("4.5")]
    [InlineData(" 4.5 ")]
    [InlineData("2.1")]
    public void Attempts_PreferFourFiveThenFallBack(string? value)
    {
        Assert.Equal([new Version(4, 5), new Version(3, 3)], GlContextVersion.Attempts(value));
    }

    [Theory]
    [InlineData("3.3")]
    [InlineData(" 3.3 ")]
    public void Attempts_ThreeThreeSkipsTheNewerContext(string value)
    {
        Assert.Equal([new Version(3, 3)], GlContextVersion.Attempts(value));
    }

    [Fact]
    public void Constants_AreTheDocumentedVersions()
    {
        Assert.Equal("DREAM_GL_VERSION", GlContextVersion.EnvironmentVariable);
        Assert.Equal(new Version(4, 5), GlContextVersion.Preferred);
        Assert.Equal(new Version(3, 3), GlContextVersion.Fallback);
        Assert.Equal("4.5", GlContextVersion.Describe(GlContextVersion.Preferred));
    }

    [Fact]
    public void Program_RetriesOnlyTheContextProbe_NeverTheGameConstructor()
    {
        string source = File.ReadAllText(Path.Combine(RepoRoot(), "dream", "Application", "Program.cs"));

        Assert.Single(System.Text.RegularExpressions.Regex.Matches(source, @"catch \(GLFWException"));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(source, @"new Game\("));
        Assert.Matches(@"using var game = new Game\(version\);", source);
        Assert.DoesNotMatch(@"try\s*\{\s*(created = )?new Game", source);
    }

    [Fact]
    public void SelectContextVersion_FallsBackOnlyOnGlfwException_AndLogsTheRealMessage()
    {
        List<string> logs = [];
        Version chosen = Program.SelectContextVersion(
            GlContextVersion.Attempts(null),
            v => throw new GLFWException("no 4.5 for you"),
            logs.Add);

        Assert.Equal(new Version(3, 3), chosen);
        Assert.Single(logs);
        Assert.Contains("no 4.5 for you", logs[0]);
        Assert.Contains("falling back to 3.3", logs[0]);
    }

    [Fact]
    public void SelectContextVersion_PicksTheFirstSuccess_ProbesNoLastAttempt_AndLetsOtherFailuresThrough()
    {
        List<Version> probed = [];
        Assert.Equal(
            new Version(4, 5),
            Program.SelectContextVersion(GlContextVersion.Attempts(null), probed.Add, _ => { }));
        Assert.Equal([new Version(4, 5)], probed);

        probed.Clear();
        Assert.Equal(
            new Version(3, 3),
            Program.SelectContextVersion(GlContextVersion.Attempts("3.3"), probed.Add, _ => { }));
        Assert.Empty(probed);

        Assert.Throws<InvalidOperationException>(() => Program.SelectContextVersion(
            GlContextVersion.Attempts(null), _ => throw new InvalidOperationException("boom"), _ => { }));
    }

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
