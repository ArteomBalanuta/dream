using System.IO;
using Xunit;
using Dream.Simulation.World;

namespace Dream.Tests.ProjectStructure;

public class NamespaceConventionTests
{
    [Fact]
    public void MovedProductionFiles_UseDreamFileScopedNamespace()
    {
        string repoRoot = FindRepoRoot();
        string productionRoot = Path.Combine(repoRoot, "dream");
        string[] sourceFolders =
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

        foreach (string folder in sourceFolders)
        {
            foreach (string file in Directory.GetFiles(Path.Combine(productionRoot, folder), "*.cs", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(productionRoot, file);
                string expectedNamespace = "namespace Dream." +
                    Path.GetDirectoryName(relative)!.Replace(Path.DirectorySeparatorChar, '.').Replace(Path.AltDirectorySeparatorChar, '.') +
                    ";";
                string content = File.ReadAllText(file);

                Assert.Contains(expectedNamespace, content);
            }
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
