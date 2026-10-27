using System.IO;
using Xunit;

namespace Dream.Tests.ProjectStructure;

public class DependencyDirectionTests
{
    private static readonly string[] ServerForbiddenTokens =
    [
        "Dream.Client.",
        "Dream.Rendering.",
        "ImGuiNET",
        "OpenTK.Windowing"
    ];

    private static readonly string[] ProtocolForbiddenTokens =
    [
        "Dream.Client.",
        "Dream.Rendering.",
        "ImGuiNET",
        "OpenTK.Graphics",
        "OpenTK.Windowing"
    ];

    private static readonly string[] SimulationForbiddenTokens =
    [
        "Dream.Client.",
        "Dream.Rendering.",
        "Dream.Application",
        "ImGuiNET",
        "OpenTK.Windowing",
        "OpenTK.Graphics"
    ];

    public static TheoryData<string> SimulationFolders => new()
    {
        Path.Combine("dream", "Simulation", "Combat"),
        Path.Combine("dream", "Simulation", "Inventory"),
        Path.Combine("dream", "Simulation", "Physics"),
        Path.Combine("dream", "Simulation", "Runtime"),
        Path.Combine("dream", "Simulation", "World")
    };

    [Fact]
    public void ServerSources_DoNotReferenceClientOrRenderingNamespaces()
    {
        AssertNoReferences(Path.Combine("dream", "Server"), ServerForbiddenTokens);
    }

    [Fact]
    public void MultiplayerProtocolSources_DoNotReferencePresentationOrRenderingNamespaces()
    {
        AssertNoReferences(Path.Combine("dream", "Multiplayer", "Protocol"), ProtocolForbiddenTokens);
    }

    [Theory]
    [MemberData(nameof(SimulationFolders))]
    public void SimulationSources_DoNotReferenceClientRenderingOrApplicationNamespaces(string relativeFolder)
    {
        AssertNoReferences(relativeFolder, SimulationForbiddenTokens);
    }

    private static void AssertNoReferences(string relativeFolder, IReadOnlyList<string> forbiddenTokens)
    {
        string repoRoot = FindRepoRoot();
        string folder = Path.Combine(repoRoot, relativeFolder);
        Assert.True(Directory.Exists(folder), $"Expected folder to exist: {relativeFolder}");

        foreach (string file in Directory.GetFiles(folder, "*.cs", SearchOption.AllDirectories))
        {
            string content = File.ReadAllText(file);
            foreach (string forbiddenToken in forbiddenTokens)
            {
                Assert.False(
                    content.Contains(forbiddenToken, StringComparison.Ordinal),
                    $"{Path.GetRelativePath(repoRoot, file)} references forbidden token '{forbiddenToken}'.");
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
