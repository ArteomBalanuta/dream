using System.IO;
using Xunit;
using Dream.Simulation.World;

namespace Dream.Tests.Assets;


public class CubeVertexShaderSkinningContractTests
{
    [Fact]
    public void CubeVertexShader_Declares_FirstPass_Skinning_Contract()
    {
        string path = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "dream",
            "shaders",
            "cubeVertex.glsl"));
        string shaderSource = File.ReadAllText(path);

        Assert.Contains("layout (location = 4) in vec4 aJoints;", shaderSource);
        Assert.Contains("layout (location = 5) in vec4 aWeights;", shaderSource);
        Assert.Contains("uniform bool useSkinning;", shaderSource);
        Assert.Contains("uniform mat4 jointMatrices", shaderSource);
    }
}
