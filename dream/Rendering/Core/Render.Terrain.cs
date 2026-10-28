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
        Shader shader = GetShader("cube");
        shader.Use();
        shader.SetModelMatrix(Matrix4.Identity);
        shader.SetMatrix4("view", camera.GetViewMatrix());
        shader.SetMatrix4("projection", _projection);
        shader.SetBool("useSkinning", false);
        shader.SetBool("useInstancing", false);
        shader.SetFloat("material.metallicFactor", 0f);
        shader.SetFloat("material.roughnessFactor", 1f);
        shader.SetInt("material.baseColorTexture", 0);
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.Enable(EnableCap.DepthTest);
        // Static terrain meshes are already hidden-face culled. Draw remaining
        // exterior faces double-sided so clipped/edge cameras do not reveal
        // collision-only "transparent" blocks.
        GL.Disable(EnableCap.CullFace);
    }

    // The slot was resolved when the mesh was uploaded; this reads the cached material table.
    public void BindStaticTerrainMaterial(int materialSlot)
    {
        StaticTerrainMaterial material = StaticTerrainMaterialCatalog.GetBySlot(materialSlot);
        Shader shader = GetShader("cube");
        int texture = _staticTerrainTextures.GetOrLoad(material);
        shader.SetVector4("material.baseColorFactor", material.BaseColor);
        // Terrain is opaque: it always draws with the base "cube" program, which has no discard
        // (only its alpha-mask variant has one).
        shader.SetBool("material.hasBaseColorTexture", texture != -1);
        if (texture != -1)
        {
            GL.BindTexture(TextureTarget.Texture2D, texture);
        }
    }

    public static void EndStaticTerrainPass()
    {
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

    // IWorldInstanceDrawTarget: every model-less cube or block of one material in a single
    // instanced draw of the fallback cube mesh, with the same program, material and cull state
    // as the single-cube fallback. The shader derives each instance's normal matrix from its
    // instanceModel (cofactor), the instanced counterpart of the CPU normalMatrix.
    public void DrawFallbackInstances(
        Camera camera,
        GeometryMaterialId materialId,
        InstanceMatrixBuffer instances,
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
        instances.Upload(modelMatrices, count);
        _fallbackCubeBuffer.DrawInstanced(instances, count);
        // Leave the program the way every non-instanced user of it expects.
        GetShader("cube").SetBool("useInstancing", false);
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
        Shader shader = GetShader("cube");
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
        // Terrain is opaque: it always draws with the base "cube" program, which has no discard
        // (only its alpha-mask variant has one).
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
