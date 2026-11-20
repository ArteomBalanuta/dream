using Dream.Rendering.Models;
using Dream.Rendering.World;
using Dream.Simulation.Players;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace Dream.Rendering.Core;

public partial class Render : IWorldInstanceDrawTarget
{
    // The terrain-only program: the cube sources compiled with Shader.TerrainDefine.
    public const string TerrainShaderName = "terrain";

    // Texture units of the terrain program: 0 per-material base colour, 1 war mist (avoid), 2 the
    // tile array, 3 the world material map (bound by the static chunk renderer).
    public const int TerrainTilesUnit = 2;
    public const int TerrainMaterialMapUnit = 3;
    public const string TerrainTilesUniform = "uTerrainTiles";
    public const string TerrainMaterialMapUniform = "uTerrainMaterialMap";

    // Blend band (metres from a cell edge), height-blend depth and relief strength of the terrain shader.
    private const float TerrainBlendBand = 0.35f;
    private const float TerrainBlendDepth = 0.2f;
    private const float TerrainRelief = 0.025f;

    private readonly TerrainSurfaceSettings _terrainSurface = TerrainSurfaceSettings.FromEnvironment();

    public void DrawCube(Camera camera, Cube cube)
    {
        AnimatedModel? model = CubeMaterialModelCatalog.Resolve(cube.MaterialId);
        float assetScale = ResolveCubeAssetScale(model);
        Matrix4 modelMatrix = CubeTransforms.CreateCenteredVisualModelMatrix(cube.Position, cube.Side, assetScale);

        // Use the cube's pre-calculated center position directly
        if (model != null)
        {
            model.Render(_shaders[cube.ShaderName],
                                camera.GetViewMatrix(),
                                _projection,
                                modelMatrix);
        }
        else
        {
            DrawTerrainCubeFallback(camera, modelMatrix, cube.MaterialId);
        }
    }

    public void DrawGeometryBlock(Camera camera, GeometryRenderBlockDescriptor block)
    {
        AnimatedModel? model = CubeMaterialModelCatalog.Resolve(block.MaterialId);
        float assetScale = ResolveCubeAssetScale(model);
        Matrix4 modelMatrix = CubeTransforms.CreateCenteredVisualModelMatrix(block.Center, block.VisualSize, assetScale);

        if (model != null)
        {
            model.Render(_shaders[block.ShaderName],
                camera.GetViewMatrix(),
                _projection,
                modelMatrix);
        }
        else
        {
            DrawTerrainCubeFallback(camera, modelMatrix, block.MaterialId);
        }
    }

    // The batched static terrain pass. The static chunk renderer calls Begin once, then
    // BindStaticTerrainMaterial once per material slot followed by that slot's meshes, then End.
    // Every static mesh is in world space (model = identity), so the program, the transforms and
    // the material constants are set once per pass; only the base colour and texture change per
    // material. Each mesh sees the same uniform values as when it was drawn on its own.
    public void BeginStaticTerrainPass(Camera camera)
    {
        Shader shader = GetShader(TerrainShaderName);
        shader.Use();
        shader.SetModelMatrix(Matrix4.Identity);
        shader.SetMatrix4("view", camera.GetViewMatrix());
        shader.SetMatrix4("projection", _projection);
        shader.SetBool("useSkinning", false);
        shader.SetBool("useInstancing", false);
        // The cube program is shared with AnimatedModel, which leaves its last emissive flag set.
        shader.SetHasEmissiveTexture(false);
        // The real eye for the opt-in material response; the per-material surface is set in
        // BindStaticTerrainMaterial (zero = no response).
        shader.SetEquipmentEye(camera.Position);
        shader.SetFloat("material.metallicFactor", 0f);
        shader.SetFloat("material.roughnessFactor", 1f);
        shader.SetInt("material.baseColorTexture", 0);
        shader.SetFloat("uTerrainVariation", _terrainSurface.Variation);
        // The height blend needs the tile array (decoded in the background, -1 until ready); the
        // shader additionally needs the material map the chunk renderer binds on unit 3.
        int tiles = _terrainSurface.Blend ? _terrainTiles.GetOrLoad() : -1;
        shader.SetFloat("uTerrainBlend", tiles != -1 ? 1f : 0f);
        shader.SetFloat("uTerrainBlendBand", TerrainBlendBand);
        shader.SetFloat("uTerrainBlendDepth", TerrainBlendDepth);
        shader.SetFloat("uTerrainRelief", _terrainSurface.Relief ? TerrainRelief : 0f);
        if (tiles != -1)
        {
            GL.ActiveTexture(TextureUnit.Texture0 + TerrainTilesUnit);
            GL.BindTexture(TextureTarget.Texture2DArray, tiles);
        }

        GL.ActiveTexture(TextureUnit.Texture0);
        GL.Enable(EnableCap.DepthTest);
        // Back-face culling is safe here: the static meshes are in world space (model = identity, no
        // mirroring) and every face is wound counter-clockwise seen from outside. The terrain program has
        // no alpha-mask variant, so nothing in this pass is a cutout, and the fixed isometric camera only
        // ever sees the +X, +Y and +Z faces.
        GL.Enable(EnableCap.CullFace);
        GL.CullFace(TriangleFace.Back);
    }

    // The slot was resolved when the mesh was uploaded; this reads the cached material table.
    public void BindStaticTerrainMaterial(int materialSlot)
    {
        StaticTerrainMaterial material = StaticTerrainMaterialCatalog.GetBySlot(materialSlot);
        Shader shader = GetShader(TerrainShaderName);
        int texture = _staticTerrainTextures.GetOrLoad(material);
        shader.SetVector4("material.baseColorFactor", material.BaseColor);
        shader.SetEmissiveFactor(material.Emissive);
        shader.SetEquipmentSurface(material.Surface);
        // Terrain is opaque: it always draws with the terrain program, which has no discard
        // (and no alpha-mask variant).
        shader.SetBool("material.hasBaseColorTexture", texture != -1);
        shader.SetInt("uTerrainLayer", ResolveTerrainLayer(material));
        if (texture != -1)
        {
            GL.BindTexture(TextureTarget.Texture2D, texture);
        }
    }

    // The tile array layer of a material: its GeometryMaterialId for the eight blendable tiles
    // (grass only for its top role), otherwise -1 (lamp, unknown, grass side and bottom).
    internal static int ResolveTerrainLayer(StaticTerrainMaterial material)
    {
        int id = (int)material.MaterialId;
        if ((uint)id >= StaticTerrainTileArray.LayerCount)
        {
            return -1;
        }

        bool grass = material.MaterialId == GeometryMaterialId.Grass;
        return grass && material.Role != StaticTerrainMaterialRole.Top ? -1 : id;
    }

    public static void EndStaticTerrainPass()
    {
        // The passes after this one assume culling is off.
        GL.Disable(EnableCap.CullFace);
        StaticMeshBuffer.EndBatchedDraws();
    }

    // IWorldInstanceDrawTarget: one instanced draw per primitive of the model for a whole batch
    // of cubes or blocks that share its material. The batch drawer filled the matrices (asset
    // scale included) into its reused array; AnimatedModel uploads them once per node.
    public void DrawModelInstances(
        Camera camera,
        AnimatedModel animation,
        string shaderName,
        Matrix4[] modelMatrices,
        int count)
    {
        if (count == 0)
        {
            return;
        }

        Shader shader = GetShader(shaderName);
        animation.BeginRender(shader, camera.GetViewMatrix(), _projection);
        animation.RenderPreparedInstanced(shader, modelMatrices, count);
        animation.EndRender();
    }

    // The fallback cube's per-instance model matrices. Owned with the fallback cube mesh: its VAO's
    // instanceModel attributes (locations 6..9, divisor 1) keep pointing at this buffer between
    // draws, so the two live and die together (Dispose deletes the VAO first, then this buffer).
    private readonly InstanceMatrixBuffer _fallbackInstances = new();

    // IWorldInstanceDrawTarget: every model-less cube or block of one material in a single
    // instanced draw of the fallback cube mesh, with the same program, material and cull state
    // as the single-cube fallback. The shader derives each instance's normal matrix from its
    // instanceModel (cofactor), the instanced counterpart of the CPU normalMatrix.
    public void DrawFallbackInstances(
        Camera camera,
        GeometryMaterialId materialId,
        Matrix4[] modelMatrices,
        int count)
    {
        if (count == 0)
        {
            return;
        }

        StaticTerrainMaterial material = StaticTerrainMaterialCatalog.Resolve(materialId);
        BindTerrainMaterial(camera, Matrix4.Identity, material, instanced: true);
        GL.Enable(EnableCap.CullFace);
        GL.CullFace(TriangleFace.Back);
        _fallbackInstances.Upload(modelMatrices, count);
        _fallbackCubeBuffer.DrawInstanced(_fallbackInstances, count);
        // Leave the program the way every non-instanced user of it expects.
        GetShader(TerrainShaderName).SetBool("useInstancing", false);
    }

    // The asset scale for a batch's model, for callers outside Render that build its matrices.
    public static float ResolveCubeVisualScale(AnimatedModel? animation) => ResolveCubeAssetScale(animation);

    private void DrawTerrainCubeFallback(
        Camera camera,
        Matrix4 modelMatrix,
        GeometryMaterialId materialId)
    {
        StaticTerrainMaterial material = StaticTerrainMaterialCatalog.Resolve(materialId);
        BindTerrainMaterial(camera, modelMatrix, material, instanced: false);
        GL.Enable(EnableCap.CullFace);
        GL.CullFace(TriangleFace.Back);
        _fallbackCubeBuffer.Draw();
    }

    // Instanced draws take their model (and normal) matrix from the instance attribute, so
    // `model` and `normalMatrix` are only uploaded for a single cube.
    private void BindTerrainMaterial(
        Camera camera,
        Matrix4 modelMatrix,
        StaticTerrainMaterial material,
        bool instanced)
    {
        Shader shader = GetShader(TerrainShaderName);
        int texture = _staticTerrainTextures.GetOrLoad(material);
        shader.Use();
        if (!instanced)
        {
            shader.SetModelMatrix(modelMatrix);
        }

        shader.SetMatrix4("view", camera.GetViewMatrix());
        shader.SetMatrix4("projection", _projection);
        shader.SetBool("useSkinning", false);
        shader.SetBool("useInstancing", instanced);
        shader.SetVector4("material.baseColorFactor", material.BaseColor);
        shader.SetFloat("material.metallicFactor", 0f);
        shader.SetFloat("material.roughnessFactor", 1f);
        shader.SetEmissiveFactor(material.Emissive);
        shader.SetHasEmissiveTexture(false);
        // The terrain program is shared with the batched pass, which leaves its last material's
        // surface in it: fallback and dynamic cubes draw the plain tile, so switch the response off.
        shader.SetEquipmentSurface(Vector4.Zero);
        // Fallback and dynamic cubes move, and the variation noise is keyed on world position, so
        // with it on they would "swim". They draw the plain tile (variation, blend and layer off),
        // the same look as the Grass/Dirt/Stone gltf cubes of the plain cube program.
        shader.SetFloat("uTerrainVariation", 0f);
        shader.SetFloat("uTerrainBlend", 0f);
        shader.SetInt("uTerrainLayer", -1);
        // Terrain is opaque: it always draws with the terrain program, which has no discard
        // (and no alpha-mask variant).
        shader.SetBool("material.hasBaseColorTexture", texture != -1);
        if (texture != -1)
        {
            GL.ActiveTexture(TextureUnit.Texture0);
            GL.BindTexture(TextureTarget.Texture2D, texture);
            shader.SetInt("material.baseColorTexture", 0);
        }

        GL.Enable(EnableCap.DepthTest);
    }

    // Compares the model's source path, so resolving a scale never requests or loads a model.
    private static float ResolveCubeAssetScale(AnimatedModel? animation)
    {
        if (animation == null)
        {
            return 1f;
        }

        string sourcePath = animation.SourcePath;
        if (string.Equals(sourcePath, ModelAssets.GrassCubePath, StringComparison.Ordinal))
        {
            return GrassCubeAssetDefinition.VisualScale;
        }

        if (string.Equals(sourcePath, ModelAssets.DirtCubePath, StringComparison.Ordinal))
        {
            return DirtCubeAssetDefinition.VisualScale;
        }

        if (string.Equals(sourcePath, ModelAssets.StoneCubePath, StringComparison.Ordinal))
        {
            return StoneCubeAssetDefinition.VisualScale;
        }

        return 1f;
    }
}
