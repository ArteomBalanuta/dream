using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Dream.Tests.ProjectStructure;

// Tokens are namespaces or type names. Each matches as a whole name: "Dream.Rendering" catches
// "using Dream.Rendering;", "Dream.Rendering.Core" and "global::Dream.Rendering", but not an
// unrelated "Dream.RenderingTools".
public class DependencyDirectionTests
{
    private static readonly string[] ServerForbiddenTokens =
    [
        "Dream.Client",
        "Dream.Rendering",
        "ImGuiNET",
        "OpenTK.Windowing"
    ];

    private static readonly string[] ProtocolForbiddenTokens =
    [
        "Dream.Client",
        "Dream.Rendering",
        "ImGuiNET",
        "OpenTK.Graphics",
        "OpenTK.Windowing"
    ];

    // ARCH-2: Simulation is the lower layer. The rules, the persistence ports and the tick-driven
    // host contract live in Simulation; Server implements and composes them.
    private static readonly string[] SimulationForbiddenTokens =
    [
        "Dream.Client",
        "Dream.Rendering",
        "Dream.Application",
        "Dream.Server",
        "ImGuiNET",
        "OpenTK.Windowing",
        "OpenTK.Graphics"
    ];

    // The client reaches the server only to host or join one (bootstrap, the integrated and
    // on-demand local servers, connection options and performance snapshots). Never persistence.
    private static readonly string[] ClientForbiddenTokens =
    [
        "Dream.Server.Persistence",
        "Dream.Server.Rules",
        "Dream.Simulation.Persistence"
    ];

    // Ratchet: the only client files that may reference Dream.Server. A file that stops referencing
    // it must leave the list (the test fails until it does); never add one.
    private static readonly string[] ClientFilesAllowedToReferenceServer =
    [
        "Networking/RemoteClientOptions.cs",
        "Networking/RemoteClientSession.cs",
        "Scenes/PlayScene.Render.cs",
        "Startup/ILocalServerHandle.cs",
        "Startup/IntegratedLocalServerHandle.cs",
        "Startup/NetworkSessionCoordinator.cs",
        "Startup/OnDemandLocalServerHandle.cs"
    ];

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

    // Whole tree, so every Simulation folder (and any new one) stays headless.
    [Fact]
    public void SimulationSources_DoNotReferenceClientRenderingOrApplicationNamespaces()
    {
        AssertNoReferences(Path.Combine("dream", "Simulation"), SimulationForbiddenTokens);
    }

    [Fact]
    public void ClientSources_DoNotReferencePersistenceOrServerRules()
    {
        AssertNoReferences(Path.Combine("dream", "Client"), ClientForbiddenTokens);
    }

    [Fact]
    public void ClientSources_ReferenceServerOnlyFromAllowlistedFiles()
    {
        string clientRoot = Path.Combine(FindRepoRoot(), "dream", "Client");
        string[] referencing = Directory
            .GetFiles(clientRoot, "*.cs", SearchOption.AllDirectories)
            .Where(file => ReferencesName(File.ReadAllText(file), "Dream.Server"))
            .Select(file => Path.GetRelativePath(clientRoot, file).Replace(Path.DirectorySeparatorChar, '/'))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(ClientFilesAllowedToReferenceServer.Order(StringComparer.Ordinal), referencing);
    }

    // DEC-3 moved the GameSettings coupling out of Simulation.Players; desktop client
    // configuration must not leak back into the headless simulation either.
    [Fact]
    public void SimulationSources_DoNotReferenceDesktopConfiguration()
    {
        AssertNoReferences(Path.Combine("dream", "Simulation"), ["Dream.Configuration", "GameSettings"], wholeNames: false);
    }

    [Theory]
    [InlineData("using Dream.Rendering;", "Dream.Rendering", true)]
    [InlineData("using Dream.Client;", "Dream.Client", true)]
    [InlineData("using Dream.Rendering.Core;", "Dream.Rendering", true)]
    [InlineData("global::Dream.Client.Startup.Foo x;", "Dream.Client", true)]
    [InlineData("using Dream.Rendering", "Dream.Rendering", true)]
    [InlineData("var x = Dream.Rendering\n.Core;", "Dream.Rendering", true)]
    [InlineData("using Dream.RenderingTools;", "Dream.Rendering", false)]
    [InlineData("using My.Dream.Client;", "Dream.Client", false)]
    [InlineData("using Dream.Simulation;", "Dream.Client", false)]
    public void ReferencesName_MatchesTheNamespaceAtANameBoundary(string content, string name, bool expected)
    {
        Assert.Equal(expected, ReferencesName(content, name));
    }

    private static void AssertNoReferences(string relativeFolder, IReadOnlyList<string> forbiddenTokens, bool wholeNames = true)
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
                    wholeNames
                        ? ReferencesName(content, forbiddenToken)
                        : content.Contains(forbiddenToken, StringComparison.Ordinal),
                    $"{Path.GetRelativePath(repoRoot, file)} references forbidden token '{forbiddenToken}'.");
            }
        }
    }

    // The token must not continue an identifier or dotted name on its left, and must end at a
    // name boundary on its right (".", ";", whitespace, end of text and so on).
    private static bool ReferencesName(string content, string name)
    {
        return Regex.IsMatch(content, $@"(?<![\w.]){Regex.Escape(name)}(?!\w)");
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
