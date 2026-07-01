using System.IO;
using Xunit;
using Dream.Simulation.World;

namespace Dream.Tests.ProjectStructure;

public class DependencyDirectionTests
{
    [Fact]
    public void ServerSources_DoNotReferenceClientOrRenderingNamespaces()
    {
        AssertNoReferences(
            Path.Combine("dream", "Server"),
            [
                "Dream.Client.",
                "Dream.Rendering.",
                "ImGuiNET",
                "OpenTK.Windowing"
            ]);
    }

    [Fact]
    public void MultiplayerProtocolSources_DoNotReferencePresentationOrRenderingNamespaces()
    {
        AssertNoReferences(
            Path.Combine("dream", "Multiplayer", "Protocol"),
            [
                "Dream.Client.",
                "Dream.Rendering.",
                "ImGuiNET",
                "OpenTK.Graphics",
                "OpenTK.Windowing"
            ]);
    }

    private static void AssertNoReferences(string relativeFolder, IReadOnlyList<string> forbiddenTokens)
    {
        string repoRoot = FindRepoRoot();
        string folder = Path.Combine(repoRoot, relativeFolder);

        foreach (string file in Directory.GetFiles(folder, "*.cs", SearchOption.AllDirectories))
        {
            string content = File.ReadAllText(file);
            foreach (string forbiddenToken in forbiddenTokens)
            {
                Assert.DoesNotContain(forbiddenToken, content);
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
