using System.Xml.Linq;
using Xunit;

namespace Dream.Tests.Assets;


public class ProjectBuildConfigurationTests
{
    [Fact]
    public void DreamProject_DeclaresExecutableOutputWithAppHost()
    {
        // Walk up to the repo root instead of counting "..": the output folder's depth changes
        // when the build redirects BaseOutputPath, and a fixed hop then misses the project file.
        XDocument project = XDocument.Load(
            Path.Combine(Dream.Tests.Integration.RepoRoot.Find(), "dream", "dream.csproj"));

        string? outputType = ReadProperty(project, "OutputType");
        string? useAppHost = ReadProperty(project, "UseAppHost");

        Assert.Equal("Exe", outputType);
        Assert.Equal("true", useAppHost);
    }

    private static string? ReadProperty(XDocument project, string name)
    {
        return project
            .Descendants(name)
            .Select(element => element.Value.Trim())
            .FirstOrDefault();
    }
}
