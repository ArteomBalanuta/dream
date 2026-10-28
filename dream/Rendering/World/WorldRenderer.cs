using Dream.Client.Presentation;
using Dream.Rendering.Core;
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
        for (int batchIndex = 0; batchIndex < dynamicBlockBatches.Count; batchIndex++)
        {
            WorldGeometryRenderBatch batch = dynamicBlockBatches[batchIndex];
            if (batch.Animation != null)
            {
                _render.DrawGeometryBlockBatch(camera, batch.Animation, batch.ShaderName, batch.Blocks);
                continue;
            }

            IReadOnlyList<GeometryRenderBlockDescriptor> blocks = batch.Blocks;
            for (int i = 0; i < blocks.Count; i++)
            {
                _render.DrawGeometryBlock(camera, blocks[i]);
            }
        }

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
        _staticChunkRenderer.Dispose();
    }

    private void DrawCubeBatches(Camera camera, IReadOnlyList<WorldRenderBatch> batches)
    {
        for (int batchIndex = 0; batchIndex < batches.Count; batchIndex++)
        {
            WorldRenderBatch batch = batches[batchIndex];
            if (batch.Animation != null)
            {
                _render.DrawAnimatedCubeBatch(camera, batch.Animation, batch.ShaderName, batch.Cubes);
                continue;
            }

            IReadOnlyList<Cube> cubes = batch.Cubes;
            for (int i = 0; i < cubes.Count; i++)
            {
                _render.DrawCube(camera, cubes[i]);
            }
        }
    }

    // Remote players intentionally stay out of the world-geometry renderer so
    // replicated actor ownership does not drift back into the cube/world path.
}
