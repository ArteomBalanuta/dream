using Xunit;

namespace Dream.Tests.ProjectStructure;

public class SimulationWorldDependencyTests
{
    [Fact]
    public void SimulationWorld_DoesNotReferenceRendering()
    {
        string worldRoot = Path.Combine(FindRepoRoot(), "dream", "Simulation", "World");
        string[] offending = Directory
            .EnumerateFiles(worldRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => File.ReadAllText(path).Contains("Dream.Rendering.", StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(worldRoot, path))
            .ToArray();

        Assert.Empty(offending);
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
