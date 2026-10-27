using Xunit;

namespace Dream.Tests.ProjectStructure;

// After DEC-1/2/4/5 the simulation world, combat code, Player and Camera no longer depend on the
// client, the renderer or windowing. DependencyDirectionTests covers Combat, Inventory and Physics
// against a wider token list and SimulationWorldDependencyTests covers World against Rendering;
// this adds the remaining folder/token pairs and the two DEC-2 files. The no-finalizer rule is
// already asserted by ShaderLifetimeTests.No_Type_In_Dream_Assembly_Declares_A_Finalizer.
public class DecouplingDependencyTests
{
    private static readonly string[] ForbiddenTokens =
    [
        "Dream.Client.",
        "Dream.Rendering.",
        "OpenTK.Windowing"
    ];

    public static TheoryData<string> DecoupledSources => new()
    {
        Path.Combine("dream", "Simulation", "World"),
        Path.Combine("dream", "Simulation", "Combat"),
        Path.Combine("dream", "Simulation", "Players", "Player.cs"),
        Path.Combine("dream", "Simulation", "Players", "Camera.cs")
    };

    [Theory]
    [MemberData(nameof(DecoupledSources))]
    public void DecoupledSimulationSources_DoNotReferenceClientRenderingOrWindowing(string relativePath)
    {
        string repoRoot = FindRepoRoot();
        string fullPath = Path.Combine(repoRoot, relativePath);
        string[] files = File.Exists(fullPath)
            ? [fullPath]
            : Directory.Exists(fullPath)
                ? Directory.GetFiles(fullPath, "*.cs", SearchOption.AllDirectories)
                : throw new FileNotFoundException($"Expected source path to exist: {relativePath}");
        Assert.NotEmpty(files);

        List<string> offenders = [];
        foreach (string file in files)
        {
            string content = File.ReadAllText(file);
            foreach (string token in ForbiddenTokens)
            {
                if (content.Contains(token, StringComparison.Ordinal))
                {
                    offenders.Add($"{Path.GetRelativePath(repoRoot, file)} references '{token}'");
                }
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void CubeAndRenderBlockDescriptor_CarryNoVisualModel()
    {
        // DEC-5: the model is resolved by material in the renderer, never stored on simulation data.
        Assert.Null(typeof(Dream.Simulation.World.Cube).GetProperty("Animation"));
        Assert.Null(typeof(Dream.Simulation.World.Geometry.GeometryRenderBlockDescriptor).GetProperty("Animation"));
        Assert.DoesNotContain(
            typeof(Dream.Simulation.World.Cube).GetProperties(),
            property => property.PropertyType.Namespace?.StartsWith("Dream.Rendering", StringComparison.Ordinal) == true);
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
