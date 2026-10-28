using Dream.Rendering.Models;
using Dream.Rendering.World;
using Dream.Simulation.Players;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace Dream.Rendering.Core;

public partial class Render
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

    public void DrawGeometryBlockBatch(
        Camera camera,
        AnimatedModel animation,
        string shaderName,
        IReadOnlyList<GeometryRenderBlockDescriptor> blocks)
    {
        if (blocks.Count == 0)
        {
            return;
        }

        Shader shader = GetShader(shaderName);
        Matrix4 view = camera.GetViewMatrix();
        Matrix4[] modelMatrices = _geometryBlockMatrices.Rent(blocks.Count);
        // A batch shares one material, and its animation is that material's resolved model.
        float assetScale = ResolveCubeAssetScale(animation);
        for (int i = 0; i < blocks.Count; i++)
        {
            GeometryRenderBlockDescriptor block = blocks[i];
            modelMatrices[i] = CubeTransforms.CreateCenteredVisualModelMatrix(
                block.Center,
                block.VisualSize,
                assetScale);
        }

        animation.BeginRender(shader, view, _projection);
        animation.RenderPreparedInstanced(shader, modelMatrices, blocks.Count);
        animation.EndRender();
    }

    private void DrawTerrainCubeFallback(
        Camera camera,
        Matrix4 modelMatrix,
        GeometryMaterialId materialId)
    {
        StaticTerrainMaterial material = StaticTerrainMaterialCatalog.Resolve(materialId);
        BindTerrainMaterial(camera, modelMatrix, material);
        GL.Enable(EnableCap.CullFace);
        GL.CullFace(TriangleFace.Back);
        _fallbackCubeBuffer.Draw();
    }

    private void BindTerrainMaterial(
        Camera camera,
        Matrix4 modelMatrix,
        StaticTerrainMaterial material)
    {
        Shader shader = GetShader("cube");
        int texture = _staticTerrainTextures.GetOrLoad(material);
        shader.Use();
        shader.SetModelMatrix(modelMatrix);
        shader.SetMatrix4("view", camera.GetViewMatrix());
        shader.SetMatrix4("projection", _projection);
        shader.SetBool("useSkinning", false);
        shader.SetBool("useInstancing", false);
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

    public void DrawAnimatedCubeBatch(Camera camera, AnimatedModel animation, string shaderName, IReadOnlyList<Cube> cubes)
    {
        if (cubes.Count == 0)
        {
            return;
        }

        Shader shader = GetShader(shaderName);
        Matrix4 view = camera.GetViewMatrix();
        Matrix4[] modelMatrices = _animatedCubeMatrices.Rent(cubes.Count);
        // A batch shares one material, and its animation is that material's resolved model.
        float assetScale = ResolveCubeAssetScale(animation);
        for (int i = 0; i < cubes.Count; i++)
        {
            Cube cube = cubes[i];
            modelMatrices[i] = CubeTransforms.CreateCenteredVisualModelMatrix(cube.Position, cube.Side, assetScale);
        }

        animation.BeginRender(shader, view, _projection);
        animation.RenderPreparedInstanced(shader, modelMatrices, cubes.Count);
        animation.EndRender();
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
