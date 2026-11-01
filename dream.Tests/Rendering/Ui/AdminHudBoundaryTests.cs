using Xunit;

namespace Dream.Tests.Rendering.Ui;

// The admin key only changes what this client draws. Its name must never appear in code that runs
// on or talks to the server.
public class AdminHudBoundaryTests
{
    [Theory]
    [InlineData("dream/Server")]
    [InlineData("dream/Simulation")]
    [InlineData("dream/Multiplayer")]
    public void AdminHudTokenIsAbsentFromServerSideCode(string folder)
    {
        string root = Path.Combine(FindRepoRoot(), folder.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(Directory.Exists(root), root);

        List<string> hits = [];
        foreach (string file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            if (File.ReadAllText(file).Contains("AdminHud", StringComparison.OrdinalIgnoreCase))
            {
                hits.Add(file);
            }
        }

        Assert.Empty(hits);
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
