using System.IO;
using Xunit;
using Dream.Simulation.World;

namespace Dream.Tests.Assets;


public class CubeVertexShaderSkinningContractTests
{
    [Fact]
    public void CubeVertexShader_Declares_FirstPass_Skinning_Contract()
    {
        // Walk up to the repo root instead of counting "..": the output folder's depth changes
        // when the build redirects BaseOutputPath, and a fixed hop then misses the shader.
        string path = Path.GetFullPath(Path.Combine(
            Dream.Tests.Integration.RepoRoot.Find(),
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
