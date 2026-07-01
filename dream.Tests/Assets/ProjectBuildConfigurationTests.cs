using System.Xml.Linq;
using Xunit;

namespace Dream.Tests.Assets;


public class ProjectBuildConfigurationTests
{
    [Fact]
    public void DreamProject_DeclaresExecutableOutputWithAppHost()
    {
        XDocument project = XDocument.Load(Path.Combine("..", "..", "..", "..", "dream", "dream.csproj"));

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
