using System.IO;
using Dream.Application;
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
    public void Program_CatchesOnlyGlfwExceptions_AndOnlyWhileAnotherAttemptRemains()
    {
        string source = File.ReadAllText(Path.Combine(RepoRoot(), "dream", "Application", "Program.cs"));

        Assert.Contains("catch (GLFWException ex) when (i + 1 < attempts.Count)", source);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(source, @"catch \(GLFWException"));
        Assert.Contains("new Game(attempts[i])", source);
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
