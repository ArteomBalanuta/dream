using Dream.Rendering.Core;
using Dream.Simulation.Players;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using OpenTK.Mathematics;
using System.Diagnostics;

namespace Dream.Rendering.World;

public sealed class StaticWorldChunkRenderer : IDisposable
{
    private readonly Dictionary<StaticWorldChunkMeshKey, StaticMeshBuffer> _buffers = new();
    private readonly Dictionary<StaticWorldChunkMeshKey, StaticWorldChunkMesh> _meshes = new();
    private readonly Dictionary<StaticWorldChunkMeshKey, int> _signatures = new();
    private int _lastExportSignature;
    private bool _hasCachedExport;
    private bool _disposed;

    public static IReadOnlyList<StaticWorldChunkMesh> PlanMeshes(IReadOnlyList<StaticVoxelRenderCell> cells)
    {
        return PlanBuildInputs(cells)
            .Select(input => input.BuildMesh())
            .Where(mesh => mesh.ExposedFaceCount > 0)
            .ToArray();
    }

    public static IReadOnlyList<StaticWorldChunkBuildInput> PlanBuildInputs(IReadOnlyList<StaticVoxelRenderCell> cells)
    {
        return PlanBuildInputs(
            cells,
            cachedKeys: new HashSet<StaticWorldChunkMeshKey>(),
            dirtyChunks: new HashSet<StaticWorldChunkKey>());
    }

    public static IReadOnlyList<StaticWorldChunkBuildInput> PlanBuildInputs(
        IReadOnlyList<StaticVoxelRenderCell> cells,
        IReadOnlySet<StaticWorldChunkMeshKey> cachedKeys,
        IReadOnlySet<StaticWorldChunkKey> dirtyChunks)
    {
        Dictionary<StaticWorldChunkKey, StaticVoxelRenderCell[]> cellsByChunk = cells
            .GroupBy(cell => StaticWorldChunkKey.FromWorldPosition(cell.Center))
            .ToDictionary(group => group.Key, group => group.ToArray());
        List<StaticWorldChunkBuildInput> inputs = new();

        foreach ((StaticWorldChunkKey chunkKey, StaticVoxelRenderCell[] chunkCells) in cellsByChunk)
        {
            StaticVoxelRenderCell[] occluderCells = CollectNeighborOccluders(chunkKey, cellsByChunk);
            foreach (IGrouping<GeometryMaterialId, StaticVoxelRenderCell> materialGroup in chunkCells.GroupBy(cell => cell.MaterialId))
            {
                StaticVoxelRenderCell[] materialCells = materialGroup.ToArray();
                foreach (StaticTerrainMaterialRole role in StaticTerrainMaterialCatalog.ResolveRoles(materialGroup.Key))
                {
                    StaticWorldChunkMeshKey meshKey = new(chunkKey, materialGroup.Key, role);
                    if (cachedKeys.Contains(meshKey) && !dirtyChunks.Contains(chunkKey))
                    {
                        continue;
                    }

                    inputs.Add(new StaticWorldChunkBuildInput(
                        meshKey,
                        materialCells,
                        occluderCells,
                        CalculateInputSignature(meshKey, materialCells, occluderCells)));
                }
            }
        }

        return inputs;
    }

    public static bool IsChunkVisible(Vector3 cameraPosition, Vector3 cameraFront, StaticWorldChunkMesh mesh)
    {
        if (DistanceSquaredToBounds(cameraPosition, mesh.Bounds) <=
            WorldVisibility.MaxRenderDistance * WorldVisibility.MaxRenderDistance)
        {
            return true;
        }

        return WorldVisibility.IsPositionVisible(
            cameraPosition,
            cameraFront,
            mesh.Bounds.Center,
            mesh.Bounds.Size.Length * 0.5f);
    }

    public StaticWorldChunkRenderStats Draw(Render render, Camera camera, GeometryRenderExport export)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        int exportSignature = CalculateExportSignature(export);
        stopwatch.Stop();
        double signatureMilliseconds = stopwatch.Elapsed.TotalMilliseconds;

        stopwatch.Restart();
        bool exportUnchanged = _hasCachedExport && exportSignature == _lastExportSignature;
        IReadOnlyList<StaticVoxelRenderCell> cells = exportUnchanged
            ? []
            : StaticWorldChunkExtractor.ExtractCells(export);
        stopwatch.Stop();
        double extractMilliseconds = stopwatch.Elapsed.TotalMilliseconds;

        stopwatch.Restart();
        HashSet<StaticWorldChunkKey> dirtyChunks = exportUnchanged
            ? []
            : export.DirtyCells
                .Select(cell => StaticWorldChunkKey.FromWorldPosition(new Vector3(cell.X, cell.Y, cell.Z)))
                .ToHashSet();
        IReadOnlyList<StaticWorldChunkBuildInput> inputs = exportUnchanged
            ? []
            : PlanBuildInputs(cells, _signatures.Keys.ToHashSet(), dirtyChunks);
        stopwatch.Stop();
        double planMilliseconds = stopwatch.Elapsed.TotalMilliseconds;

        stopwatch.Restart();
        StaticWorldChunkSyncStats syncStats = exportUnchanged
            ? new StaticWorldChunkSyncStats(0, _meshes.Count)
            : SynchronizeBuffers(inputs, dirtyChunks);
        _lastExportSignature = exportSignature;
        _hasCachedExport = true;
        stopwatch.Stop();
        double syncMilliseconds = stopwatch.Elapsed.TotalMilliseconds;

        int visibleChunks = 0;
        int visibleMeshes = 0;
        int visibleFaces = 0;
        int visibleTriangles = 0;
        stopwatch.Restart();
        foreach (StaticWorldChunkMesh mesh in _meshes.Values)
        {
            if (!IsChunkVisible(camera.Position, camera.Front, mesh))
            {
                continue;
            }

            if (!_buffers.TryGetValue(new StaticWorldChunkMeshKey(mesh.Key, mesh.MaterialId, mesh.MaterialRole), out StaticMeshBuffer? buffer))
            {
                continue;
            }

            render.DrawStaticMesh(camera, buffer, mesh.MaterialId, mesh.MaterialRole);
            visibleChunks++;
            visibleMeshes++;
            visibleFaces += mesh.ExposedFaceCount;
            visibleTriangles += mesh.TriangleCount;
        }
        stopwatch.Stop();

        return new StaticWorldChunkRenderStats(
            visibleChunks,
            visibleMeshes,
            visibleFaces,
            visibleTriangles,
            signatureMilliseconds,
            extractMilliseconds,
            planMilliseconds,
            syncMilliseconds,
            stopwatch.Elapsed.TotalMilliseconds,
            syncStats.RebuiltMeshes,
            syncStats.ReusedMeshes);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        ClearBuffers();
        _disposed = true;
    }

    private StaticWorldChunkSyncStats SynchronizeBuffers(
        IReadOnlyList<StaticWorldChunkBuildInput> inputs,
        IReadOnlySet<StaticWorldChunkKey> dirtyChunks)
    {
        int rebuiltMeshes = 0;
        int reusedMeshes = 0;
        foreach (StaticWorldChunkMeshKey staleKey in _signatures.Keys
            .Where(key => dirtyChunks.Contains(key.ChunkKey))
            .ToArray())
        {
            RemoveMesh(staleKey);
        }

        foreach (StaticWorldChunkBuildInput input in inputs)
        {
            if (_signatures.TryGetValue(input.Key, out int existingSignature) &&
                existingSignature == input.Signature)
            {
                reusedMeshes++;
                continue;
            }

            if (_buffers.Remove(input.Key, out StaticMeshBuffer? oldBuffer))
            {
                oldBuffer.Dispose();
            }

            StaticWorldChunkMesh mesh = input.BuildMesh();
            _meshes[input.Key] = mesh;
            _signatures[input.Key] = input.Signature;

            if (mesh.ExposedFaceCount > 0)
            {
                _buffers[input.Key] = new StaticMeshBuffer(mesh);
            }

            rebuiltMeshes++;
        }

        return new StaticWorldChunkSyncStats(rebuiltMeshes, reusedMeshes);
    }

    private void ClearBuffers()
    {
        foreach (StaticMeshBuffer buffer in _buffers.Values)
        {
            buffer.Dispose();
        }

        _buffers.Clear();
        _meshes.Clear();
        _signatures.Clear();
        _lastExportSignature = 0;
        _hasCachedExport = false;
    }

    private void RemoveMesh(StaticWorldChunkMeshKey key)
    {
        if (_buffers.Remove(key, out StaticMeshBuffer? buffer))
        {
            buffer.Dispose();
        }

        _meshes.Remove(key);
        _signatures.Remove(key);
    }

    private static StaticVoxelRenderCell[] CollectNeighborOccluders(
        StaticWorldChunkKey chunkKey,
        IReadOnlyDictionary<StaticWorldChunkKey, StaticVoxelRenderCell[]> cellsByChunk)
    {
        List<StaticVoxelRenderCell> occluders = new();
        for (int x = chunkKey.X - 1; x <= chunkKey.X + 1; x++)
        {
            for (int y = chunkKey.Y - 1; y <= chunkKey.Y + 1; y++)
            {
                for (int z = chunkKey.Z - 1; z <= chunkKey.Z + 1; z++)
                {
                    if (cellsByChunk.TryGetValue(new StaticWorldChunkKey(x, y, z), out StaticVoxelRenderCell[]? chunkCells))
                    {
                        occluders.AddRange(chunkCells);
                    }
                }
            }
        }

        return occluders.ToArray();
    }

    private static int CalculateInputSignature(
        StaticWorldChunkMeshKey key,
        IReadOnlyList<StaticVoxelRenderCell> materialCells,
        IReadOnlyList<StaticVoxelRenderCell> occluderCells)
    {
        HashCode hash = new();
        hash.Add(key);
        AddCellsToSignature(hash, materialCells);
        AddCellsToSignature(hash, occluderCells);
        return hash.ToHashCode();
    }

    private static void AddCellsToSignature(HashCode hash, IReadOnlyList<StaticVoxelRenderCell> cells)
    {
        hash.Add(cells.Count);
        foreach (StaticVoxelRenderCell cell in cells)
        {
            hash.Add(cell.Center);
            hash.Add(cell.Size);
            hash.Add(cell.MaterialId);
        }
    }

    public static int CalculateExportSignature(GeometryRenderExport export)
    {
        HashCode hash = new();
        Dictionary<Cube, GeometryMaterialId> compatibilityMaterials = new(export.Cells.Count);
        foreach (GeometryRenderCell renderCell in export.Cells)
        {
            compatibilityMaterials[renderCell.CompatibilityCube] = renderCell.Cell.Definition.MaterialId;
        }

        hash.Add(export.CompatibilityCubes.Count);
        foreach (Cube cube in export.CompatibilityCubes)
        {
            hash.Add(cube.Position);
            hash.Add(cube.Side);
            GeometryMaterialId materialId = compatibilityMaterials.TryGetValue(cube, out GeometryMaterialId mappedMaterialId)
                ? mappedMaterialId
                : cube.MaterialId;
            hash.Add(materialId);
        }

        hash.Add(export.Blocks.Count);
        foreach (GeometryRenderBlockDescriptor block in export.Blocks)
        {
            hash.Add(block.CellKey);
            hash.Add(block.SubcellKey);
            hash.Add(block.Center);
            hash.Add(block.VisualSize);
            hash.Add(block.MaterialId);
        }

        return hash.ToHashCode();
    }

    private static float DistanceSquaredToBounds(Vector3 point, StaticWorldChunkBounds bounds)
    {
        float dx = DistanceOutsideAxis(point.X, bounds.Min.X, bounds.Max.X);
        float dy = DistanceOutsideAxis(point.Y, bounds.Min.Y, bounds.Max.Y);
        float dz = DistanceOutsideAxis(point.Z, bounds.Min.Z, bounds.Max.Z);
        return (dx * dx) + (dy * dy) + (dz * dz);
    }

    private static float DistanceOutsideAxis(float value, float min, float max)
    {
        if (value < min)
        {
            return min - value;
        }

        if (value > max)
        {
            return value - max;
        }

        return 0f;
    }
}

public readonly record struct StaticWorldChunkRenderStats(
    int VisibleChunks,
    int MeshCount,
    int ExposedFaces,
    int Triangles,
    double SignatureMilliseconds = 0d,
    double ExtractMilliseconds = 0d,
    double PlanMilliseconds = 0d,
    double SyncMilliseconds = 0d,
    double DrawMilliseconds = 0d,
    int RebuiltMeshes = 0,
    int ReusedMeshes = 0);

public readonly record struct StaticWorldChunkSyncStats(
    int RebuiltMeshes,
    int ReusedMeshes);
