using OpenTK.Mathematics;
using Xunit;
using Dream.Rendering.World;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using static Dream.Tests.Simulation.World.PresentationExportCacheAssertions;

namespace Dream.Tests.Simulation.World;

// The presentation export caches its static part per Map.StaticContentVersion. After every kind
// of mutation it must still match an uncached capture: all static content, plus dynamic content
// within the radius.
//
// The scenarios are split over three classes so they run in parallel: placed and moved cubes
// (this class), static geometry edits (PresentationExportCacheStaticEditTests), and the content
// version and export signature (PresentationExportCacheVersionTests).
public class PresentationExportCacheTests
{
    [Fact]
    public void PlacingAndRemovingCubes_MatchesUncachedExport()
    {
        Map map = CreateStartupTerrainMap();
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
        Map map = CreateStartupTerrainMap();
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
        Map map = CreateStartupTerrainMap();
        Cube cube = Assert.IsType<Cube>(map.TryAddCube(FindOpenCell(map, 4, 4, run: 3), isStatic: true, withPhysics: false));

        AssertEquivalentAfter(map, () => map.UpdateCubePosition(cube, cube.Position + new Vector3(0.2f, 0f, 0f), syncPhysics: false));
        AssertEquivalentAfter(map, () => map.UpdateCubePosition(cube, cube.Position + new Vector3(1f, 0f, 0f), syncPhysics: false));
        Assert.Contains(cube, map.CaptureFullGeometryRenderExport().DynamicCompatibilityCubes);
    }

    [Fact]
    public void CopiedPlacedCubes_MatchUncachedExport()
    {
        Map source = CreateStartupTerrainMap();
        source.AddCube(FindOpenCell(source, 4, 4), withPhysics: false);
        source.AddCube(FindOpenCell(source, 8, 8), isStatic: true, withPhysics: false);
        Map target = CreateStartupTerrainMap();

        AssertEquivalentAfter(target, () => source.CopyPlacedCubesTo(target));
    }
}

// Shared by the PresentationExportCache* test classes.
internal static class PresentationExportCacheAssertions
{
    public const float LocalRadius = 4f;
    public static readonly Vector3 LocalCenter = new(5f, 1.5f, 5f);

    // Smaller than the startup floor, but at least 20 cells wide so the floor is still enriched
    // terrain with raised landmarks. The world generator adds the merged sculpture cells only from
    // 48x32 cells, so tests that need those (or compare them against the cache) use the startup
    // size instead.
    public const int SmallTerrainSize = 24;

    public static Map CreateStartupTerrainMap()
    {
        // Startup size, so the merged sculpture cells are generated.
        return CreateTerrainMap(WorldDefaults.StartupFloorSize);
    }

    public static Map CreateTerrainMap(int size)
    {
        return new Map(
            physics: null,
            generateStartupTerrain: true,
            width: size,
            depth: size);
    }

    public static GeometryRenderExport Capture(Map map, Vector3 center, float radius)
    {
        return map.CapturePresentationGeometryRenderExport(center, radius);
    }

    public static void AssertEquivalentAfter(Map map, Action mutate)
    {
        // Warm the cache first so the mutation has to invalidate it.
        Capture(map, LocalCenter, LocalRadius);
        mutate();

        AssertMatchesUncached(map, LocalCenter, LocalRadius);
        AssertMatchesUncached(map, new Vector3(18f, 1.5f, 18f), LocalRadius);
        AssertMatchesUncached(map, new Vector3(map.Width / 2f, 1.5f, map.Depth / 2f), map.Width * 2f);
        Assert.Empty(map.FindInvariantViolations());
    }

    public static void AssertMatchesUncached(Map map, Vector3 center, float radius)
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

    public static Vector3 FindOpenCell(Map map, int startX, int startZ, int run = 1)
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

    private static void AssertSameReferences(IEnumerable<Cube> expected, IEnumerable<Cube> actual)
    {
        Cube[] expectedItems = expected.ToArray();
        Cube[] actualItems = actual.ToArray();
        Assert.Equal(expectedItems.Length, actualItems.Length);
        HashSet<Cube> actualSet = actualItems.ToHashSet();
        Assert.All(expectedItems, cube => Assert.Contains(cube, actualSet));
    }

    private static void AssertSameItems<T>(IEnumerable<T> expected, IEnumerable<T> actual)
        where T : notnull
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
}
