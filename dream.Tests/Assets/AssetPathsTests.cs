using System.IO;
using Xunit;
using Dream.Assets;
using Dream.Rendering.Core;

namespace Dream.Tests.Assets;


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

    [Fact]
    public void Ui_Resolves_Under_Runtime_Ui_Directory()
    {
        string expected = Path.Combine(AppContext.BaseDirectory, "ui", "hud", "hud.json");

        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "ui"), AssetPaths.UiDirectory);
        Assert.Equal(expected, AssetPaths.Ui(Path.Combine("hud", "hud.json")));
    }

    [Fact]
    public void Ui_Assets_AreCopiedToTheOutputDirectory()
    {
        Assert.True(File.Exists(AssetPaths.Ui(Path.Combine("hud", "hud.json"))));
        Assert.True(File.Exists(AssetPaths.Ui(Path.Combine("hud", "hud.png"))));
        Assert.True(File.Exists(AssetPaths.Ui(Path.Combine("items", "items_rare.png"))));
    }
}
