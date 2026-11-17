using Dream.Rendering.World;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using OpenTK.Mathematics;
using System.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace Dream.Tests.Rendering.World;

// GPU-1 replaced the 27-chunk occluder copy and its per-mesh hashing with a face lookup and per-chunk
// hashes. LegacyPlanner below is the plan as it was before GPU-1 (57cdf83), kept verbatim as the
// reference: the new plan must produce the same mesh keys in the same order and the same faces,
// and its signatures must change for exactly the meshes whose legacy signatures change.
// HashCode is seeded per process, so signatures are compared within one run, not as constants.
public class StaticWorldChunkPlanEquivalenceTests(ITestOutputHelper output)
{
    // This body is a benchmark: its only output used to be an output.WriteLine, so it asserted
    // nothing and could not fail however badly either planner behaved. The timing is still reported
    // (it is the reason the test exists), but the ratio is deliberately NOT asserted: it is a wall
    // clock measured in the parallel phase, which would make this a flake rather than a gate.
    //
    // What IS asserted is the property the 40 repeated plans actually establish and nothing else in
    // this class covers: both planners are called repeatedly, on the same cells, from a cold and
    // then a warm state, and every one of those 80 calls must produce the SAME non-empty plan. The
    // current planner keeps per-chunk hashes, so a stateful or order-dependent bug there (a cached
    // signature leaking between calls, a dictionary enumerated in a different order on a later
    // call) shows up as a plan that differs from call to call - which the single-shot equivalence
    // test next door cannot see.
    [Fact]
    public void StartupWorld_ReportsLegacyAndCurrentPlanTimes()
    {
        IReadOnlyList<StaticVoxelRenderCell> cells = CaptureStartupCells();
        (double legacy, int legacyCount) = BestOf(() => LegacyPlanner.PlanBuildInputs(cells, Empty, NoDirty));
        (double current, int currentCount) = BestOf(() => StaticWorldChunkRenderer.PlanBuildInputs(cells));

        output.WriteLine($"cells {cells.Count}: legacy plan {legacy:F3} ms, current plan {current:F3} ms (warm, best of 20)");
        Assert.Equal(legacyCount, currentCount);
    }

    // Runs plan 40 times, returns the best warm time and the plan's key list (asserted identical on
    // every run, so a repeat-dependent plan fails here instead of being averaged away).
    private static (double BestMilliseconds, int Count) BestOf(Func<IReadOnlyList<StaticWorldChunkBuildInput>> plan)
    {
        double best = double.MaxValue;
        StaticWorldChunkMeshKey[]? firstKeys = null;
        for (int i = 0; i < 40; i++)
        {
            long start = Stopwatch.GetTimestamp();
            IReadOnlyList<StaticWorldChunkBuildInput> inputs = plan();
            double elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            StaticWorldChunkMeshKey[] keys = [.. inputs.Select(input => input.Key)];
            Assert.NotEmpty(keys);
            if (firstKeys is null)
            {
                firstKeys = keys;
            }
            else
            {
                Assert.Equal(firstKeys, keys);
            }

            if (i >= 20)
            {
                best = Math.Min(best, elapsed);
            }
        }

        return (best, firstKeys!.Length);
    }

    [Fact]
    public void StartupWorld_NewPlanMatchesLegacyKeysAndFaces()
    {
        IReadOnlyList<StaticVoxelRenderCell> cells = CaptureStartupCells();

        IReadOnlyList<StaticWorldChunkBuildInput> legacy = LegacyPlanner.PlanBuildInputs(cells, Empty, NoDirty);
        IReadOnlyList<StaticWorldChunkBuildInput> current = StaticWorldChunkRenderer.PlanBuildInputs(cells);

        AssertSameKeysAndFaces(legacy, current);
        Assert.Contains(current, input => input.BuildMesh().ExposedFaceCount > 0);
    }

    [Fact]
    public void RemovingCellNextToChunkBorder_ChangesTheSameSignaturesAndFacesAsLegacy()
    {
        IReadOnlyList<StaticVoxelRenderCell> before = CaptureStartupCells();
        // A cell whose +X neighbour lies in the next chunk: removing it exposes that neighbour's -X face.
        StaticVoxelRenderCell removed = before.First(cell =>
            StaticWorldChunkKey.FromWorldPosition(cell.Center) !=
                StaticWorldChunkKey.FromWorldPosition(cell.Center + Vector3.UnitX) &&
            before.Any(other => other.Center == cell.Center + Vector3.UnitX && other.Size == cell.Size));
        StaticVoxelRenderCell exposedNeighbor = before.First(other => other.Center == removed.Center + Vector3.UnitX);
        StaticWorldChunkKey removedChunk = StaticWorldChunkKey.FromWorldPosition(removed.Center);
        StaticWorldChunkKey neighborChunk = StaticWorldChunkKey.FromWorldPosition(exposedNeighbor.Center);
        Assert.NotEqual(removedChunk, neighborChunk);
        StaticVoxelRenderCell[] after = before.Where(cell => cell != removed).ToArray();
        Assert.Equal(before.Count - 1, after.Length);

        Dictionary<StaticWorldChunkMeshKey, int> legacyBefore = Signatures(LegacyPlanner.PlanBuildInputs(before, Empty, NoDirty));
        Dictionary<StaticWorldChunkMeshKey, int> legacyAfter = Signatures(LegacyPlanner.PlanBuildInputs(after, Empty, NoDirty));
        Dictionary<StaticWorldChunkMeshKey, int> currentBefore = Signatures(StaticWorldChunkRenderer.PlanBuildInputs(before));
        Dictionary<StaticWorldChunkMeshKey, int> currentAfter = Signatures(StaticWorldChunkRenderer.PlanBuildInputs(after));

        HashSet<StaticWorldChunkMeshKey> legacyChanged = ChangedKeys(legacyBefore, legacyAfter);
        HashSet<StaticWorldChunkMeshKey> currentChanged = ChangedKeys(currentBefore, currentAfter);
        Assert.NotEmpty(legacyChanged);
        Assert.True(legacyChanged.SetEquals(currentChanged), "signature change sets differ");
        Assert.Contains(currentChanged, key => key.ChunkKey == neighborChunk);

        // The incremental plans against each planner's own cache rebuild the same meshes, with the
        // same faces, and the neighbour chunk across the border gains the exposed face.
        IReadOnlyList<StaticWorldChunkBuildInput> legacyIncremental = LegacyPlanner.PlanBuildInputs(after, legacyBefore, NoDirty);
        IReadOnlyList<StaticWorldChunkBuildInput> currentIncremental = StaticWorldChunkRenderer.PlanBuildInputs(after, currentBefore, NoDirty);
        AssertSameKeysAndFaces(legacyIncremental, currentIncremental);

        int neighborFacesBefore = StaticWorldChunkRenderer.PlanBuildInputs(before)
            .Where(input => input.Key.ChunkKey == neighborChunk)
            .Sum(input => input.BuildMesh().ExposedFaceCount);
        int neighborFacesAfter = currentIncremental
            .Where(input => input.Key.ChunkKey == neighborChunk)
            .Sum(input => input.BuildMesh().ExposedFaceCount);
        Assert.True(neighborFacesAfter > neighborFacesBefore, $"{neighborFacesBefore} -> {neighborFacesAfter}");
    }

    [Fact]
    public void MixedCellSizesAcrossChunkBorders_NewPlanMatchesLegacyFaces()
    {
        // Unit cubes, half and quarter blocks, and oversized slabs straddling the borders at 16 and 32.
        Random random = new(1234);
        List<StaticVoxelRenderCell> cells = new();
        GeometryMaterialId[] materials = [GeometryMaterialId.Grass, GeometryMaterialId.Stone, GeometryMaterialId.Sand];
        for (int x = 10; x < 38; x++)
        {
            for (int z = 10; z < 22; z++)
            {
                for (int y = 0; y < 3; y++)
                {
                    int roll = random.Next(10);
                    GeometryMaterialId material = materials[random.Next(materials.Length)];
                    if (roll < 6)
                    {
                        cells.Add(new StaticVoxelRenderCell(new Vector3(x + 0.5f, y + 0.5f, z + 0.5f), Vector3.One, material));
                    }
                    else if (roll < 8)
                    {
                        for (int s = 0; s < 8; s++)
                        {
                            if (random.Next(3) != 0)
                            {
                                Vector3 offset = new((s & 1) * 0.5f + 0.25f, ((s >> 1) & 1) * 0.5f + 0.25f, ((s >> 2) & 1) * 0.5f + 0.25f);
                                cells.Add(new StaticVoxelRenderCell(new Vector3(x, y, z) + offset, new Vector3(0.5f), material));
                            }
                        }
                    }
                    else if (roll < 9)
                    {
                        cells.Add(new StaticVoxelRenderCell(new Vector3(x + 0.125f, y + 0.125f, z + 0.125f), new Vector3(0.25f), material));
                    }
                }
            }
        }

        cells.Add(new StaticVoxelRenderCell(new Vector3(16f, 3.5f, 16f), new Vector3(20f, 1f, 12f), GeometryMaterialId.Stone));
        cells.Add(new StaticVoxelRenderCell(new Vector3(31f, 3.5f, 14f), new Vector3(10f, 1f, 1f), GeometryMaterialId.Grass));
        cells.Add(new StaticVoxelRenderCell(new Vector3(24f, -0.5f, 16f), new Vector3(40f, 1f, 40f), GeometryMaterialId.Sand));

        IReadOnlyList<StaticWorldChunkBuildInput> legacy = LegacyPlanner.PlanBuildInputs(cells, Empty, NoDirty);
        IReadOnlyList<StaticWorldChunkBuildInput> current = StaticWorldChunkRenderer.PlanBuildInputs(cells);

        AssertSameKeysAndFaces(legacy, current);
    }

    private static readonly IReadOnlyDictionary<StaticWorldChunkMeshKey, int> Empty =
        new Dictionary<StaticWorldChunkMeshKey, int>();

    private static readonly IReadOnlySet<StaticWorldChunkKey> NoDirty = new HashSet<StaticWorldChunkKey>();

    internal static IReadOnlyList<StaticVoxelRenderCell> CaptureStartupCells()
    {
        Map map = new(
            physics: null,
            generateStartupTerrain: true,
            width: WorldDefaults.StartupFloorSize,
            depth: WorldDefaults.StartupFloorSize);
        GeometryRenderExport export = map.CapturePresentationGeometryRenderExport(new Vector3(12f, 2f, 12f), 16f);
        IReadOnlyList<StaticVoxelRenderCell> cells = StaticWorldChunkExtractor.ExtractCells(export);
        Assert.NotEmpty(cells);
        return cells;
    }

    private static void AssertSameKeysAndFaces(
        IReadOnlyList<StaticWorldChunkBuildInput> legacy,
        IReadOnlyList<StaticWorldChunkBuildInput> current)
    {
        Assert.Equal(legacy.Select(input => input.Key), current.Select(input => input.Key));
        for (int i = 0; i < legacy.Count; i++)
        {
            Assert.Equal(legacy[i].MaterialCells, current[i].MaterialCells);
            StaticWorldChunkMesh expected = legacy[i].BuildMesh();
            StaticWorldChunkMesh actual = current[i].BuildMesh();
            Assert.Equal(expected.ExposedFaceCount, actual.ExposedFaceCount);
            Assert.Equal(expected.SourceCellCount, actual.SourceCellCount);
            Assert.Equal(expected.Bounds, actual.Bounds);
            Assert.True(expected.Vertices.SequenceEqual(actual.Vertices), $"vertices differ for {legacy[i].Key}");
            Assert.True(expected.Indices.SequenceEqual(actual.Indices), $"indices differ for {legacy[i].Key}");
            // The lookup hands the builder only occluders that can touch the chunk.
            Assert.True(current[i].OccluderCells.Count <= legacy[i].OccluderCells.Count);
        }
    }

    private static Dictionary<StaticWorldChunkMeshKey, int> Signatures(IReadOnlyList<StaticWorldChunkBuildInput> inputs)
    {
        return inputs.ToDictionary(input => input.Key, input => input.Signature);
    }

    private static HashSet<StaticWorldChunkMeshKey> ChangedKeys(
        Dictionary<StaticWorldChunkMeshKey, int> before,
        Dictionary<StaticWorldChunkMeshKey, int> after)
    {
        HashSet<StaticWorldChunkMeshKey> changed = new();
        foreach ((StaticWorldChunkMeshKey key, int signature) in after)
        {
            if (!before.TryGetValue(key, out int old) || old != signature)
            {
                changed.Add(key);
            }
        }

        foreach (StaticWorldChunkMeshKey key in before.Keys)
        {
            if (!after.ContainsKey(key))
            {
                changed.Add(key);
            }
        }

        return changed;
    }

    // The plan as it was before GPU-1, unchanged.
    private static class LegacyPlanner
    {
        public static IReadOnlyList<StaticWorldChunkBuildInput> PlanBuildInputs(
            IReadOnlyList<StaticVoxelRenderCell> cells,
            IReadOnlyDictionary<StaticWorldChunkMeshKey, int> cachedSignatures,
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
                        int signature = CalculateInputSignature(meshKey, materialCells, occluderCells);
                        if (!dirtyChunks.Contains(chunkKey) &&
                            cachedSignatures.TryGetValue(meshKey, out int cachedSignature) &&
                            cachedSignature == signature)
                        {
                            continue;
                        }

                        inputs.Add(new StaticWorldChunkBuildInput(
                            meshKey,
                            materialCells,
                            occluderCells,
                            signature));
                    }
                }
            }

            return inputs;
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
            AddCellsToSignature(ref hash, materialCells);
            AddCellsToSignature(ref hash, occluderCells);
            return hash.ToHashCode();
        }

        private static void AddCellsToSignature(ref HashCode hash, IReadOnlyList<StaticVoxelRenderCell> cells)
        {
            hash.Add(cells.Count);
            foreach (StaticVoxelRenderCell cell in cells)
            {
                hash.Add(cell.Center);
                hash.Add(cell.Size);
                hash.Add(cell.MaterialId);
            }
        }
    }
}
