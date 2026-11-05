using Dream.Assets;
using Dream.Rendering.Shadows;

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

        // The shadow caster program. It reuses cubeVertex.glsl VERBATIM, the same file cube, the terrain variant and
        // enemy use: skinning, instancing and the wind sway all live there, so a caster is displaced by exactly the code
        // that displaces what it casts for, and a shadow cannot detach from a swaying canopy. Its fragment stage is empty
        // (its MASK variant discards, so contact discs and decals cast nothing).
        LoadWorldShader(ShadowDepthShaderName,
            AssetPaths.Shader("cubeVertex.glsl"),
            AssetPaths.Shader("depthFragment.glsl"));
    }

    public const string ShadowDepthShaderName = "shadowDepth";

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

        BindShadowSamplers(shader);
        if (shader.AlphaMaskVariant != null)
        {
            BindShadowSamplers(shader.AlphaMaskVariant);
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
        BindShadowSamplers(shader);
        _shaders.Replace(name, shader);
    }

    // The shadow samplers keep their units for the program's life, like the terrain samplers above and for the same
    // reason (AnimatedModel.SetTexture re-sets the material sampler uniforms per draw and must not fight them). A
    // program without the sampler (it is optimised out when nothing reads it) has location -1, which SetInt ignores.
    // The MASK variants are separate programs and need it too.
    private static void BindShadowSamplers(Shader shader)
    {
        shader.Use();
        shader.SetInt(ShadowSettings.SunMapUniform, ShadowSettings.SunTextureUnit);
        shader.SetInt(ShadowSettings.AtlasUniform, ShadowSettings.AtlasTextureUnit);
    }
}
