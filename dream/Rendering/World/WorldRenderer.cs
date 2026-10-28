using Dream.Client.Presentation;
using Dream.Rendering.Core;
using Dream.Rendering.Models;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using OpenTK.Mathematics;

namespace Dream.Rendering.World;

public class WorldRenderer : IDisposable
{
    private readonly Render _render;
    private readonly StaticWorldChunkRenderer _staticChunkRenderer = new();
    private readonly WorldRenderBatchPlanner _batchPlanner = new();
    private readonly WorldInstanceBatchDrawer _instanceDrawer = new();

    // Reused every frame and cleared before use. They and the planner's batches only hold this
    // frame's visible items, and nothing here or in Render keeps them past the draw calls.
    private readonly List<Cube> _visibleCubes = new();
    private readonly List<GeometryRenderBlockDescriptor> _visibleBlocks = new();
    private bool _disposed;

    public WorldRenderer(Render render)
    {
        _render = render;
    }

    // Half the diagonal of a unit cube: the per-axis half extent of a box that holds the cube in
    // any orientation, matching the bounding radius WorldVisibility uses.
    private const float CubeBoundingRadiusPerSide = 0.8660254f;

    // Distance and direction cull first (cheap, and keeps the render distance), then the frustum.
    public static bool IsCubeInView(Vector3 cameraPosition, Vector3 cameraFront, in ViewFrustum frustum, Cube cube)
    {
        if (!WorldVisibility.IsCubeVisible(cameraPosition, cameraFront, cube))
        {
            return false;
        }

        float halfExtent = cube.Side * CubeBoundingRadiusPerSide;
        return frustum.IntersectsBox(cube.Position, new Vector3(halfExtent));
    }

    public static bool IsGeometryBlockInView(
        Vector3 cameraPosition,
        Vector3 cameraFront,
        in ViewFrustum frustum,
        GeometryRenderBlockDescriptor block)
    {
        if (!WorldVisibility.IsGeometryBlockVisible(cameraPosition, cameraFront, block))
        {
            return false;
        }

        float halfExtent = block.Size.Length * 0.5f;
        return frustum.IntersectsBox(block.Center, new Vector3(halfExtent));
    }

    public static ViewFrustum CreateFrustum(Camera camera, Render render)
    {
        ArgumentNullException.ThrowIfNull(camera);
        ArgumentNullException.ThrowIfNull(render);
        return ViewFrustum.FromViewAndProjection(camera.GetViewMatrix(), render.ProjectionMatrix);
    }

    public WorldRenderStats DrawWorld(Camera camera, IEnumerable<Cube> cubes)
    {
        ViewFrustum frustum = CreateFrustum(camera, _render);
        Vector3 cameraPosition = camera.Position;
        Vector3 cameraFront = camera.Front;
        _visibleCubes.Clear();
        foreach (Cube cube in cubes)
        {
            if (!IsCubeInView(cameraPosition, cameraFront, frustum, cube))
            {
                continue;
            }

            _visibleCubes.Add(cube);
        }

        IReadOnlyList<WorldRenderBatch> batches = _batchPlanner.PlanCubeBatches(_visibleCubes);
        DrawCubeBatches(camera, batches);

        return new WorldRenderStats(
            _visibleCubes.Count,
            VisibleGeometryBlocks: 0,
            batches.Count,
            GeometryBatchCount: 0);
    }

    public WorldRenderStats DrawWorld(WorldSessionPresentationState state)
    {
        return DrawWorld(state.Camera, state.VisibleWorld);
    }

    public WorldRenderStats DrawWorld(Camera camera, GeometryRenderExport export)
    {
        // One frustum per frame, shared by the static chunks, dynamic cubes and dynamic blocks.
        ViewFrustum frustum = CreateFrustum(camera, _render);
        Vector3 cameraPosition = camera.Position;
        Vector3 cameraFront = camera.Front;
        StaticWorldChunkRenderStats staticStats = _staticChunkRenderer.Draw(_render, camera, frustum, export);
        _visibleCubes.Clear();
        IReadOnlyList<Cube> dynamicCubes = export.DynamicCompatibilityCubes;
        for (int i = 0; i < dynamicCubes.Count; i++)
        {
            Cube cube = dynamicCubes[i];
            if (!IsCubeInView(cameraPosition, cameraFront, frustum, cube))
            {
                continue;
            }

            _visibleCubes.Add(cube);
        }

        IReadOnlyList<WorldRenderBatch> dynamicBatches = _batchPlanner.PlanCubeBatches(_visibleCubes);
        DrawCubeBatches(camera, dynamicBatches);

        _visibleBlocks.Clear();
        IReadOnlyList<GeometryRenderBlockDescriptor> dynamicBlocks = export.DynamicBlocks;
        for (int i = 0; i < dynamicBlocks.Count; i++)
        {
            GeometryRenderBlockDescriptor block = dynamicBlocks[i];
            if (!IsGeometryBlockInView(cameraPosition, cameraFront, frustum, block))
            {
                continue;
            }

            _visibleBlocks.Add(block);
        }

        IReadOnlyList<WorldGeometryRenderBatch> dynamicBlockBatches = _batchPlanner.PlanGeometryBlockBatches(_visibleBlocks);
        _instanceDrawer.DrawGeometryBlockBatches(_render, camera, dynamicBlockBatches);

        return new WorldRenderStats(
            VisibleCompatibilityCubes: _visibleCubes.Count,
            VisibleGeometryBlocks: _visibleBlocks.Count,
            CompatibilityBatchCount: dynamicBatches.Count,
            GeometryBatchCount: dynamicBlockBatches.Count,
            VisibleStaticChunks: staticStats.VisibleChunks,
            StaticChunkMeshCount: staticStats.MeshCount,
            StaticChunkExposedFaces: staticStats.ExposedFaces,
            StaticChunkTriangles: staticStats.Triangles,
            StaticChunkSignatureMilliseconds: staticStats.SignatureMilliseconds,
            StaticChunkExtractMilliseconds: staticStats.ExtractMilliseconds,
            StaticChunkPlanMilliseconds: staticStats.PlanMilliseconds,
            StaticChunkSyncMilliseconds: staticStats.SyncMilliseconds,
            StaticChunkDrawMilliseconds: staticStats.DrawMilliseconds,
            StaticChunkRebuiltMeshes: staticStats.RebuiltMeshes,
            StaticChunkReusedMeshes: staticStats.ReusedMeshes);
    }

    // Releases the static chunk meshes. Render calls this on the render thread at teardown.
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _staticChunkRenderer.Dispose();
        }
        finally
        {
            _instanceDrawer.Dispose();
        }
    }

    private void DrawCubeBatches(Camera camera, IReadOnlyList<WorldRenderBatch> batches)
    {
        _instanceDrawer.DrawCubeBatches(_render, camera, batches);
    }

    // Remote players intentionally stay out of the world-geometry renderer so
    // replicated actor ownership does not drift back into the cube/world path.
}

// What the instanced batch drawer needs from the renderer: one call per batch, each issuing the
// batch's instanced draw(s). Render implements it on the render thread; tests substitute a
// recording target, since there is no GL context there.
public interface IWorldInstanceDrawTarget
{
    // A batch whose material resolved to a model: one instanced draw per primitive of the model.
    void DrawModelInstances(Camera camera, AnimatedModel animation, string shaderName, Matrix4[] modelMatrices, int count);

    // A batch without a model: one instanced draw of the fallback cube for the whole batch. The
    // target uploads the matrices into `instances` before drawing.
    void DrawFallbackInstances(
        Camera camera,
        GeometryMaterialId materialId,
        InstanceMatrixBuffer instances,
        Matrix4[] modelMatrices,
        int count);
}

// Turns the planner's per-material batches into one instanced draw call per batch (per model
// primitive for model batches), for dynamic cubes and dynamic blocks (rubble) alike. The model
// matrices of each batch are written into one reused array, and model-less batches share one
// persistent InstanceMatrixBuffer, so a frame with as many instances as an earlier one allocates
// nothing. Each batch is drawn before the next one is filled, so one array serves all of them.
public sealed class WorldInstanceBatchDrawer : IDisposable
{
    private readonly ReusableMatrixBuffer _matrices = new();
    private readonly InstanceMatrixBuffer _fallbackInstances = new();

    public int MatrixCapacity => _matrices.Capacity;

    public InstanceMatrixBuffer FallbackInstances => _fallbackInstances;

    // Returns the number of batches drawn (one instanced submission each).
    public int DrawCubeBatches(IWorldInstanceDrawTarget target, Camera camera, IReadOnlyList<WorldRenderBatch> batches)
    {
        ArgumentNullException.ThrowIfNull(target);
        int drawn = 0;
        for (int batchIndex = 0; batchIndex < batches.Count; batchIndex++)
        {
            WorldRenderBatch batch = batches[batchIndex];
            IReadOnlyList<Cube> cubes = batch.Cubes;
            if (cubes.Count == 0)
            {
                continue;
            }

            Matrix4[] matrices = _matrices.Rent(cubes.Count);
            // A batch shares one material, and its animation is that material's resolved model.
            float assetScale = Render.ResolveCubeVisualScale(batch.Animation);
            for (int i = 0; i < cubes.Count; i++)
            {
                Cube cube = cubes[i];
                matrices[i] = CubeTransforms.CreateCenteredVisualModelMatrix(cube.Position, cube.Side, assetScale);
            }

            Submit(target, camera, batch.Animation, batch.ShaderName, cubes[0].MaterialId, matrices, cubes.Count);
            drawn++;
        }

        return drawn;
    }

    public int DrawGeometryBlockBatches(
        IWorldInstanceDrawTarget target,
        Camera camera,
        IReadOnlyList<WorldGeometryRenderBatch> batches)
    {
        ArgumentNullException.ThrowIfNull(target);
        int drawn = 0;
        for (int batchIndex = 0; batchIndex < batches.Count; batchIndex++)
        {
            WorldGeometryRenderBatch batch = batches[batchIndex];
            IReadOnlyList<GeometryRenderBlockDescriptor> blocks = batch.Blocks;
            if (blocks.Count == 0)
            {
                continue;
            }

            Matrix4[] matrices = _matrices.Rent(blocks.Count);
            float assetScale = Render.ResolveCubeVisualScale(batch.Animation);
            for (int i = 0; i < blocks.Count; i++)
            {
                GeometryRenderBlockDescriptor block = blocks[i];
                matrices[i] = CubeTransforms.CreateCenteredVisualModelMatrix(block.Center, block.VisualSize, assetScale);
            }

            Submit(target, camera, batch.Animation, batch.ShaderName, blocks[0].MaterialId, matrices, blocks.Count);
            drawn++;
        }

        return drawn;
    }

    public void Dispose()
    {
        _fallbackInstances.Dispose();
    }

    private void Submit(
        IWorldInstanceDrawTarget target,
        Camera camera,
        AnimatedModel? animation,
        string shaderName,
        GeometryMaterialId materialId,
        Matrix4[] matrices,
        int count)
    {
        if (animation != null)
        {
            target.DrawModelInstances(camera, animation, shaderName, matrices, count);
        }
        else
        {
            target.DrawFallbackInstances(camera, materialId, _fallbackInstances, matrices, count);
        }
    }
}
