using System.IO;
using Dream.Tests.Integration;
using Xunit;

namespace Dream.Tests.Utilities;

// Guards the union reader the structural tests use, so a split class cannot make their source
// checks vacuous by reading only the main file.
public class PartialClassSourceTests
{
    [Theory]
    [InlineData("Rendering/Core/Render.cs", "Render")]
    [InlineData("Client/Scenes/PlayScene.cs", "PlayScene")]
    public void Split_Class_Reads_The_Main_File_And_Every_Partial(string relative, string className)
    {
        string path = Path.Combine(RepoRoot.Find(), "dream", relative.Replace('/', Path.DirectorySeparatorChar));
        IReadOnlyList<string> files = PartialClassSource.Files(path);
        string union = PartialClassSource.Read(path);

        Assert.Equal(Path.GetFullPath(path), files[0]);
        Assert.True(files.Count > 1, $"{className} is expected to be split into partial files.");
        foreach (string file in files)
        {
            string source = File.ReadAllText(file);
            Assert.Contains($"public partial class {className}", source);
            Assert.Contains(source.Trim(), union);
        }
    }

    [Fact]
    public void Class_Without_Partials_Reads_As_Itself()
    {
        string path = Path.Combine(RepoRoot.Find(), "dream", "Rendering", "Core", "RenderCounters.cs");

        Assert.Single(PartialClassSource.Files(path));
        Assert.Equal(File.ReadAllText(path), PartialClassSource.Read(path));
    }

    [Fact]
    public void Similarly_Named_Files_Are_Not_Partials()
    {
        string path = Path.Combine(RepoRoot.Find(), "dream", "Rendering", "Core", "Render.cs");

        Assert.DoesNotContain(
            PartialClassSource.Files(path),
            file => Path.GetFileName(file).StartsWith("RenderCounters", StringComparison.Ordinal));
    }
}
