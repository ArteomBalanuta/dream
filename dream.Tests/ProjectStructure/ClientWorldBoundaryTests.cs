using Xunit;

namespace Dream.Tests.ProjectStructure;

// The desktop client used to run the authoritative WorldSessionRuntime in a "mirror" mode,
// so server-only paths executed on the client unless guarded. Client code now uses
// ClientWorldSession / IClientWorldState only.
public class ClientWorldBoundaryTests
{
    [Fact]
    public void ClientCode_DoesNotUseTheAuthoritativeRuntime()
    {
        string clientRoot = Path.Combine(FindRepoRoot(), "dream", "Client");
        string[] offending = Directory
            .EnumerateFiles(clientRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => File.ReadLines(path)
                .Select(line => line.Trim())
                .Where(line => !line.StartsWith("//", StringComparison.Ordinal))
                .Any(line => line.Contains("WorldSessionRuntime", StringComparison.Ordinal)))
            .Select(path => Path.GetRelativePath(clientRoot, path))
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
