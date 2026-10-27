using OpenTK.Mathematics;
using Xunit;
using Dream.Rendering.World;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;

namespace Dream.Tests.Simulation.World;

// The presentation export caches its static part per Map.StaticContentVersion. After every kind
// of mutation it must still match an uncached capture: all static content, plus dynamic content
// within the radius.
public class PresentationExportCacheTests
{
    private const float LocalRadius = 4f;
    private static readonly Vector3 LocalCenter = new(5f, 1.5f, 5f);

    [Fact]
    public void PlacingAndRemovingCubes_MatchesUncachedExport()
    {
        Map map = CreateTerrainMap();
        Vector3 dynamicCell = FindOpenCell(map, 4, 4);
        Vector3 staticCell = FindOpenCell(map, 12, 12);

        Cube? dynamicCube = null;
        Cube? staticCube = null;
        AssertEquivalentAfter(map, () => dynamicCube = map.TryAddCube(dynamicCell, withPhysics: false));
        AssertEquivalentAfter(map, () => staticCube = map.TryAddCube(staticCell, isStatic: true, withPhysics: false));
        AssertEquivalentAfter(map, () => Assert.True(map.RemoveCube(dynamicCube!)));
        AssertEquivalentAfter(map, () => Assert.True(map.RemoveCube(staticCube!)));
    }

    [Fact]
    public void MovingDynamicCube_MatchesUncachedExport()
    {
        Map map = CreateTerrainMap();
        Cube cube = Assert.IsType<Cube>(map.TryAddCube(FindOpenCell(map, 4, 4, run: 6), withPhysics: false));

        for (int step = 0; step < 8; step++)
        {
            AssertEquivalentAfter(map, () => map.UpdateCubePosition(cube, cube.Position + new Vector3(0.4f, 0f, 0f)));
        }

        AssertEquivalentAfter(map, () => map.UpdateCubePosition(cube, cube.Position + new Vector3(0.6f, 0f, 0f), syncPhysics: false));
        AssertEquivalentAfter(map, () => map.UpdateCubePosition(cube, cube.Position + new Vector3(0f, 0f, 30f), syncPhysics: false));
    }

    [Fact]
    public void MovingStaticCube_BecomesDynamic_MatchesUncachedExport()
    {
        Map map = CreateTerrainMap();
        Cube cube = Assert.IsType<Cube>(map.TryAddCube(FindOpenCell(map, 4, 4, run: 3), isStatic: true, withPhysics: false));

        AssertEquivalentAfter(map, () => map.UpdateCubePosition(cube, cube.Position + new Vector3(0.2f, 0f, 0f), syncPhysics: false));
        AssertEquivalentAfter(map, () => map.UpdateCubePosition(cube, cube.Position + new Vector3(1f, 0f, 0f), syncPhysics: false));
        Assert.Contains(cube, map.CaptureFullGeometryRenderExport().DynamicCompatibilityCubes);
    }

    [Fact]
    public void SubdividingPlacedAndGroundCells_MatchesUncachedExport()
    {
        Map map = CreateTerrainMap();
        Vector3 placed = FindOpenCell(map, 4, 4);
        map.AddCube(placed, withPhysics: false);
        GeometryCellKey placedKey = new((int)placed.X, 1, (int)placed.Z);
        GeometryCellKey groundKey = new(2, 0, 2);

        AssertEquivalentAfter(map, () => map.ApplyGeometrySubcellSolid(placedKey, new GeometrySubcellKey(0, 0, 0), isSolid: false));
        AssertEquivalentAfter(map, () => map.ApplyGeometrySubcellSolid(placedKey, new GeometrySubcellKey(1, 1, 1), isSolid: false));
        AssertEquivalentAfter(map, () => map.ApplyGeometrySubcellSolid(groundKey, new GeometrySubcellKey(0, 0, 0), isSolid: false));
        AssertEquivalentAfter(map, () => Assert.True(map.RestoreSolidCubeCell(placedKey)));
    }

    [Fact]
    public void ChangingMergedSculptureSubcells_MatchesUncachedExport()
    {
        Map map = CreateTerrainMap();
        GeometryCellRecord sculpture = map.GeometryCells.Values.First(record => record.SubdivisionState != null && record.Key.Y > 0);
        long version = map.StaticContentVersion;

        AssertEquivalentAfter(map, () => map.ApplyGeometrySubcellSolid(sculpture.Key, new GeometrySubcellKey(0, 0, 0), isSolid: false));
        Assert.NotEqual(version, map.StaticContentVersion);
        AssertEquivalentAfter(map, () => map.ApplyGeometrySubcellSolid(sculpture.Key, new GeometrySubcellKey(0, 0, 0), isSolid: true));
        AssertEquivalentAfter(map, () => Assert.True(map.RestoreGeneratedStartupCell(sculpture.Key)));
    }

    [Fact]
    public void FractureAndRubble_MatchesUncachedExport()
    {
        Map map = CreateTerrainMap();
        Vector3 dynamicCell = FindOpenCell(map, 4, 4);
        Vector3 staticCell = FindOpenCell(map, 6, 6);
        map.AddCube(dynamicCell, withPhysics: false);
        map.AddCube(staticCell, isStatic: true, withPhysics: false);
        GeometryCellKey dynamicKey = new((int)dynamicCell.X, 1, (int)dynamicCell.Z);
        GeometryCellKey staticKey = new((int)staticCell.X, 1, (int)staticCell.Z);

        AssertEquivalentAfter(map, () => Assert.True(map.FractureCubeIntoPieces(dynamicKey)));
        AssertEquivalentAfter(map, () => Assert.True(map.FractureCubeIntoPieces(staticKey)));
        GeometryFracturePiece piece = map.TryGetRubbleAtCell(dynamicKey)!.FracturePieces![0];
        AssertEquivalentAfter(map, () => Assert.True(map.UpdateFracturePiecePosition(dynamicKey, 0, piece.Center + new Vector3(0f, 0f, 20f))));
        AssertEquivalentAfter(map, () => Assert.True(map.RemoveRubble(staticKey)));
        AssertEquivalentAfter(map, () => Assert.True(map.RestoreFracturePieces(
            staticKey,
            GeometryMaterialId.Sand,
            CubeFracturePattern.CreateFirstPassRubble(staticKey),
            withPhysics: false)));
    }

    [Fact]
    public void CopiedPlacedCubes_MatchUncachedExport()
    {
        Map source = CreateTerrainMap();
        source.AddCube(FindOpenCell(source, 4, 4), withPhysics: false);
        source.AddCube(FindOpenCell(source, 8, 8), isStatic: true, withPhysics: false);
        Map target = CreateTerrainMap();

        AssertEquivalentAfter(target, () => source.CopyPlacedCubesTo(target));
    }

    [Fact]
    public void StaticContentVersion_IsStableWithoutStaticChanges()
    {
        Map map = CreateTerrainMap();
        Cube cube = Assert.IsType<Cube>(map.TryAddCube(FindOpenCell(map, 4, 4, run: 4), withPhysics: false));
        long version = map.StaticContentVersion;

        GeometryRenderExport first = Capture(map, LocalCenter, LocalRadius);
        GeometryRenderExport second = Capture(map, LocalCenter, LocalRadius);
        map.UpdateCubePosition(cube, cube.Position + new Vector3(0.3f, 0f, 0f));
        map.UpdateCubePosition(cube, cube.Position + new Vector3(1f, 0f, 0f));
        GeometryRenderExport moved = Capture(map, LocalCenter, LocalRadius);

        Assert.Equal(version, map.StaticContentVersion);
        Assert.Equal(version, first.StaticContentVersion);
        Assert.Equal(version, second.StaticContentVersion);
        Assert.Equal(version, moved.StaticContentVersion);
        Assert.Same(first.CompatibilityCubes, moved.CompatibilityCubes);
        Assert.Same(first.Blocks, moved.Blocks);
    }

    [Fact]
    public void StaticContentVersion_ChangesAfterStaticChanges()
    {
        Map map = CreateTerrainMap();
        long initial = map.StaticContentVersion;
        Cube cube = Assert.IsType<Cube>(map.TryAddCube(FindOpenCell(map, 4, 4, run: 2), isStatic: true, withPhysics: false));
        long afterAdd = map.StaticContentVersion;
        map.UpdateCubePosition(cube, cube.Position + new Vector3(0.2f, 0f, 0f), syncPhysics: false);
        long afterInCellMove = map.StaticContentVersion;
        map.RemoveCube(cube);
        long afterRemove = map.StaticContentVersion;

        Assert.NotEqual(initial, afterAdd);
        Assert.NotEqual(afterAdd, afterInCellMove);
        Assert.NotEqual(afterInCellMove, afterRemove);
    }

    [Fact]
    public void StaticContentVersion_DiffersBetweenMaps()
    {
        Map first = CreateTerrainMap();
        Map second = CreateTerrainMap();

        Assert.NotEqual(0, first.StaticContentVersion);
        Assert.NotEqual(first.StaticContentVersion, second.StaticContentVersion);
    }

    [Fact]
    public void ExportSignature_IsStableForUnchangedFrames_AndChangesWithStaticContent()
    {
        Map map = CreateTerrainMap();
        Capture(map, LocalCenter, LocalRadius);

        int first = StaticWorldChunkRenderer.CalculateExportSignature(Capture(map, LocalCenter, LocalRadius));
        int second = StaticWorldChunkRenderer.CalculateExportSignature(Capture(map, LocalCenter, LocalRadius));
        map.AddCube(FindOpenCell(map, 4, 4), isStatic: true, withPhysics: false);
        GeometryRenderExport changedExport = Capture(map, LocalCenter, LocalRadius);
        int changed = StaticWorldChunkRenderer.CalculateExportSignature(changedExport);
        int settled = StaticWorldChunkRenderer.CalculateExportSignature(Capture(map, LocalCenter, LocalRadius));

        Assert.Equal(first, second);
        Assert.NotEmpty(changedExport.StaticDirtyCells);
        Assert.NotEqual(first, changed);
        Assert.NotEqual(first, settled);
    }

    [Fact]
    public void ExportSignature_DoesNotDependOnStaticDirtyCellOrder()
    {
        GeometryRenderExport export = Capture(CreateTerrainMap(), LocalCenter, LocalRadius) with
        {
            StaticDirtyCells = [new GeometryCellKey(1, 1, 1), new GeometryCellKey(2, 1, 1)]
        };
        GeometryRenderExport reordered = export with
        {
            StaticDirtyCells = [new GeometryCellKey(2, 1, 1), new GeometryCellKey(1, 1, 1)]
        };

        Assert.Equal(
            StaticWorldChunkRenderer.CalculateExportSignature(export),
            StaticWorldChunkRenderer.CalculateExportSignature(reordered));
    }

    [Fact]
    public void ExportSignature_WithoutVersion_StillHashesContent()
    {
        Map map = CreateTerrainMap();
        Capture(map, LocalCenter, LocalRadius);
        GeometryRenderExport before = Capture(map, LocalCenter, LocalRadius) with { StaticContentVersion = 0 };
        GeometryRenderExport beforeAgain = Capture(map, LocalCenter, LocalRadius) with { StaticContentVersion = 0 };
        map.AddCube(FindOpenCell(map, 4, 4), isStatic: true, withPhysics: false);
        GeometryRenderExport after = Capture(map, LocalCenter, LocalRadius) with
        {
            StaticContentVersion = 0,
            StaticDirtyCells = []
        };

        Assert.Equal(
            StaticWorldChunkRenderer.CalculateExportSignature(before),
            StaticWorldChunkRenderer.CalculateExportSignature(beforeAgain));
        Assert.NotEqual(
            StaticWorldChunkRenderer.CalculateExportSignature(before),
            StaticWorldChunkRenderer.CalculateExportSignature(after));
    }

    [Fact]
    public void UncachedExports_KeepUnknownVersion()
    {
        Map map = CreateTerrainMap();

        Assert.Equal(0, map.CaptureFullGeometryRenderExport().StaticContentVersion);
        Assert.Equal(0, map.CaptureGeometryRenderExport(LocalCenter, LocalRadius).StaticContentVersion);
    }

    private static Map CreateTerrainMap()
    {
        // Startup size, so the merged sculpture cells are generated.
        return new Map(
            physics: null,
            loadVisualAssets: false,
            generateStartupTerrain: true,
            width: WorldDefaults.StartupFloorSize,
            depth: WorldDefaults.StartupFloorSize);
    }

    private static GeometryRenderExport Capture(Map map, Vector3 center, float radius)
    {
        return map.CapturePresentationGeometryRenderExport(center, radius);
    }

    private static void AssertEquivalentAfter(Map map, Action mutate)
    {
        // Warm the cache first so the mutation has to invalidate it.
        Capture(map, LocalCenter, LocalRadius);
        mutate();

        AssertMatchesUncached(map, LocalCenter, LocalRadius);
        AssertMatchesUncached(map, new Vector3(18f, 1.5f, 18f), LocalRadius);
        AssertMatchesUncached(map, new Vector3(map.Width / 2f, 1.5f, map.Depth / 2f), map.Width * 2f);
        Assert.Empty(map.FindInvariantViolations());
    }

    private static void AssertMatchesUncached(Map map, Vector3 center, float radius)
    {
        GeometryRenderExport cached = Capture(map, center, radius);
        GeometryRenderExport full = map.CaptureFullGeometryRenderExport();

        HashSet<Cube> excludedDynamicCubes = full.DynamicCompatibilityCubes
            .Where(cube => !IsWithinRadius(cube.Position, new Vector3(cube.Side), center, radius))
            .ToHashSet();

        AssertSameReferences(full.CompatibilityCubes, cached.CompatibilityCubes);
        AssertSameItems(full.Blocks, cached.Blocks);
        AssertSameReferences(
            full.DynamicCompatibilityCubes.Where(cube => !excludedDynamicCubes.Contains(cube)),
            cached.DynamicCompatibilityCubes);
        AssertSameItems(
            full.DynamicBlocks.Where(block => IsWithinRadius(block.Center, block.Size, center, radius)),
            cached.DynamicBlocks);
        AssertSameItems(
            full.Cells.Where(cell => !excludedDynamicCubes.Contains(cell.CompatibilityCube)),
            cached.Cells);
        Assert.Equal(cached.Cells.Count, cached.Cells.Select(cell => cell.Key).Distinct().Count());
        Assert.Equal(cached.Cells.Count, cached.Cells.Count());
        Assert.Equal(map.StaticContentVersion, cached.StaticContentVersion);
    }

    private static void AssertSameReferences(IEnumerable<Cube> expected, IEnumerable<Cube> actual)
    {
        Cube[] expectedItems = expected.ToArray();
        Cube[] actualItems = actual.ToArray();
        Assert.Equal(expectedItems.Length, actualItems.Length);
        HashSet<Cube> actualSet = actualItems.ToHashSet();
        Assert.All(expectedItems, cube => Assert.Contains(cube, actualSet));
    }

    private static void AssertSameItems<T>(IEnumerable<T> expected, IEnumerable<T> actual)
    {
        Dictionary<T, int> counts = new();
        int expectedCount = 0;
        foreach (T item in expected)
        {
            counts[item] = counts.GetValueOrDefault(item) + 1;
            expectedCount++;
        }

        int actualCount = 0;
        foreach (T item in actual)
        {
            Assert.True(counts.TryGetValue(item, out int remaining) && remaining > 0, $"unexpected item {item}");
            counts[item] = remaining - 1;
            actualCount++;
        }

        Assert.Equal(expectedCount, actualCount);
    }

    private static bool IsWithinRadius(Vector3 center, Vector3 size, Vector3 point, float radius)
    {
        Vector3 min = center - (size * 0.5f);
        Vector3 max = center + (size * 0.5f);
        float dx = Math.Max(Math.Max(min.X - point.X, 0f), point.X - max.X);
        float dy = Math.Max(Math.Max(min.Y - point.Y, 0f), point.Y - max.Y);
        float dz = Math.Max(Math.Max(min.Z - point.Z, 0f), point.Z - max.Z);
        return (dx * dx) + (dy * dy) + (dz * dz) <= radius * radius;
    }

    private static Vector3 FindOpenCell(Map map, int startX, int startZ, int run = 1)
    {
        for (int x = startX; x < map.Width - run; x++)
        {
            for (int z = startZ; z < map.Depth - 1; z++)
            {
                bool open = Enumerable.Range(0, run).All(offset =>
                    map.TryGetGeometryCellAtCell(x + offset, 1, z) == null &&
                    map.TryGetRubbleAtCell(new GeometryCellKey(x + offset, 1, z)) == null);
                if (open)
                {
                    return new Vector3(x, 1f, z);
                }
            }
        }

        throw new InvalidOperationException("No open cell found in the terrain world.");
    }
}
