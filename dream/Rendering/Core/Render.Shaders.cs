using Dream.Assets;

namespace Dream.Rendering.Core;

public partial class Render
{
    private void InitializeShaders()
    {
        LoadShader("default",
            AssetPaths.Shader("vertexShader.glsl"),
            AssetPaths.Shader("fragmentShader.glsl"));

        LoadWorldShader("cube",
            AssetPaths.Shader("cubeVertex.glsl"),
            AssetPaths.Shader("cubeFragment.glsl"));

        LoadTerrainShader(TerrainShaderName,
            AssetPaths.Shader("cubeVertex.glsl"),
            AssetPaths.Shader("cubeFragment.glsl"));

        LoadWorldShader("enemy",
            AssetPaths.Shader("cubeVertex.glsl"),
            AssetPaths.Shader("enemyFragment.glsl"));
    }

    public Shader GetShader(string name)
    {
        return _shaders[name];
    }

    // Builds the new shader before replacing, so a failed compile keeps the previous one. The
    // replaced shader is disposed only after the new one is stored.
    public void LoadShader(string name, string vertexPath, string fragmentPath)
    {
        Shader shader = new(vertexPath, fragmentPath);
        _shaders.Replace(name, shader);
    }

    // A world-pass program: the opaque program plus its glTF MASK variant (the same sources with
    // Shader.AlphaMaskDefine), which AnimatedModel switches to only for MASK materials. The variant
    // is owned and disposed by the opaque program. Like LoadShader, a failed compile of either
    // keeps the previous pair.
    public void LoadWorldShader(string name, string vertexPath, string fragmentPath)
    {
        Shader shader = new(vertexPath, fragmentPath);
        try
        {
            shader.AttachAlphaMaskVariant(new Shader(vertexPath, fragmentPath, Shader.AlphaMaskDefine));
        }
        catch
        {
            shader.Dispose();
            throw;
        }

        _shaders.Replace(name, shader);
    }

    // The terrain-only program: the cube sources with Shader.TerrainDefine. Terrain is opaque, so it
    // has no MASK variant. Uniform blocks (frame lighting, lights, boundary mist) are bound to their
    // fixed binding points at link time inside Shader, so it needs no further frame setup.
    public void LoadTerrainShader(string name, string vertexPath, string fragmentPath)
    {
        Shader shader = new(vertexPath, fragmentPath, Shader.TerrainDefine);
        // The two terrain samplers keep their units for the program's life: the tile array on unit 2
        // and the world material map on unit 3 (unit 0 is the per-material texture, 1 the war mist).
        shader.Use();
        shader.SetInt(TerrainTilesUniform, TerrainTilesUnit);
        shader.SetInt(TerrainMaterialMapUniform, TerrainMaterialMapUnit);
        _shaders.Replace(name, shader);
    }
}
