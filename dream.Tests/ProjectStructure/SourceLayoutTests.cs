using System.IO;
using Xunit;
using Dream.Application;
using Dream.Simulation.World;

namespace Dream.Tests.ProjectStructure;

public class SourceLayoutTests
{
    [Fact]
    public void ProductionSourceRoot_DoesNotRemainFlatAfterOrganizationPass()
    {
        string repoRoot = FindRepoRoot();
        string productionRoot = Path.Combine(repoRoot, "dream");

        string[] rootSourceFiles = Directory
            .GetFiles(productionRoot, "*.cs", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .Where(name => name is not "Program.cs")
            .ToArray()!;

        Assert.True(
            rootSourceFiles.Length <= 3,
            $"Only entry-point or compatibility files should remain at dream/ root. Found: {string.Join(", ", rootSourceFiles)}");
    }

    [Fact]
    public void ProductionSourceRoot_HasTopLevelBoundedContextFolders()
    {
        string repoRoot = FindRepoRoot();
        string productionRoot = Path.Combine(repoRoot, "dream");
        string[] expectedFolders =
        [
            "Application",
            "Client",
            "Server",
            "Multiplayer",
            "Simulation",
            "Rendering",
            "Assets",
            "Configuration",
            "Utilities"
        ];

        foreach (string folder in expectedFolders)
        {
            Assert.True(
                Directory.Exists(Path.Combine(productionRoot, folder)),
                $"Missing source folder: dream/{folder}");
        }
    }

    private static string FindRepoRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current != null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "dream")) &&
                Directory.Exists(Path.Combine(current.FullName, "dream.Tests")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root.");
    }
}
