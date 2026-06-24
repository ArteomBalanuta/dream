using System.IO;
using Xunit;

public class AssetPathsTests
{
    [Fact]
    public void Shader_Resolves_Under_Runtime_Shader_Directory()
    {
        string expected = Path.Combine(AppContext.BaseDirectory, "shaders", "vertexShader.glsl");

        Assert.Equal(expected, AssetPaths.Shader("vertexShader.glsl"));
    }

    [Fact]
    public void Animation_Resolves_Under_Runtime_Animations_Directory()
    {
        string expected = Path.Combine(AppContext.BaseDirectory, "animations", "GrassTextureCube.gltf");

        Assert.Equal(expected, AssetPaths.Animation("GrassTextureCube.gltf"));
    }

    [Fact]
    public void Font_Resolves_Under_Runtime_Fonts_Directory()
    {
        string expected = Path.Combine(AppContext.BaseDirectory, "fonts", "Arial.ttf");

        Assert.Equal(expected, AssetPaths.Font("Arial.ttf"));
    }
}
