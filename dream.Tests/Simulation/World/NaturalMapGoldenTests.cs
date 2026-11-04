using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using Dream.Simulation.World.MapSources;
using Xunit;

namespace Dream.Tests.Simulation.World;

// Goldens for generator key "natural", version 1.
//
// MapSourceGoldenTests pins the "default" generator; this pins the other one. A world row stores
// the key and version it was created with, and both the server and the client rebuild that world
// from the pair, so the cells a registered pair produces must stay byte-identical forever. The
// property tests in NaturalMapGeneratorTests say the distribution is sane; only these rows say it
// is the same distribution as yesterday's.
//
// Map.WorldChecksum cannot be leaned on here: WorldStateChecksum.TryHashCell skips Key.Y <= 0, so
// the floor -- most of what a material policy decides -- is outside it. The cell hash below covers
// every cell, floor included.
//
// If one of these rows moves, the natural distribution changed. That is a new generator version,
// not a re-recording, unless no world has ever been created with the current one.
public class NaturalMapGoldenTests
{
    private sealed record Golden(
        int Width,
        int Depth,
        int CellCount,
        ulong CellOrderHash,
        ulong WorldChecksum,
        int GrassCells,
        int StoneCells,
        int RockCells,
        int DirtCells,
        int SandCells,
        int MudCells,
        int GrassPathCells);

    private static readonly Golden[] s_goldens =
    [
        // Measured on the change that added the natural generator (HEAD was b485b693).
        new(56, 56, 3764, 0x1CB9AAB218095019UL, 0x9F57D9386B74159EUL, 1383, 775, 548, 509, 243, 27, 279),
        new(100, 100, 10628, 0x73BD81AAF9AED69AUL, 0x9B06C292F853D9D8UL, 4921, 986, 1475, 2097, 356, 250, 543),
    ];

    public static IEnumerable<object[]> Cases()
    {
        yield return [56, 56];
        yield return [100, 100];
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void NaturalMapMatchesGolden(int width, int depth)
    {
        Golden actual = Measure(width, depth);
        Golden? expected = s_goldens.FirstOrDefault(g => g.Width == width && g.Depth == depth);
        Assert.True(expected != null, "No golden for this case. Measured: " + Format(actual));
        Assert.True(expected == actual, "Expected: " + Format(expected) + "\nActual:   " + Format(actual));
    }

    private static Golden Measure(int width, int depth)
    {
        const int height = 8;
        Assert.True(WorldGeneratorCatalog.TryGet(
            WorldGeneratorCatalog.NaturalKey,
            WorldGeneratorCatalog.NaturalVersion,
            out IMapGenerator? generator));

        IMapSource source = generator.Create(new MapGenerationRequest(width, height, depth, 0));
        IReadOnlyList<GeometryCellRecord> cells = source.CreateCells();
        Map map = new(source, physics: null, headless: true);
        Assert.Empty(map.FindInvariantViolations());

        return new Golden(
            width,
            depth,
            cells.Count,
            HashCells(cells),
            map.WorldChecksum,
            Count(cells, GeometryMaterialId.Grass),
            Count(cells, GeometryMaterialId.Stone),
            Count(cells, GeometryMaterialId.Rock),
            Count(cells, GeometryMaterialId.Dirt),
            Count(cells, GeometryMaterialId.Sand),
            Count(cells, GeometryMaterialId.Mud),
            Count(cells, GeometryMaterialId.GrassPath));
    }

    private static int Count(IReadOnlyList<GeometryCellRecord> cells, GeometryMaterialId material)
    {
        int count = 0;
        foreach (GeometryCellRecord cell in cells)
        {
            if (cell.Definition.MaterialId == material)
            {
                count++;
            }
        }

        return count;
    }

    // Order-sensitive: the insertion order is the order the startup physics bodies are added in.
    private static ulong HashCells(IReadOnlyList<GeometryCellRecord> cells)
    {
        ulong h = 0xCBF29CE484222325UL;
        foreach (GeometryCellRecord cell in cells)
        {
            h = Add(h, (ulong)(uint)cell.Key.X);
            h = Add(h, (ulong)(uint)cell.Key.Y);
            h = Add(h, (ulong)(uint)cell.Key.Z);
            h = Add(h, (ulong)(int)cell.Definition.ShapeKind);
            h = Add(h, (ulong)(int)cell.Definition.MaterialId);
            h = Add(h, (ulong)cell.Definition.SubdivisionResolution);
            h = Add(h, cell.Definition.IsSolid ? 1UL : 0UL);
            h = Add(h, cell.Definition.IsFloor ? 1UL : 0UL);
            h = Add(h, cell.SubdivisionState != null ? 1UL : 0UL);
        }

        return h;
    }

    private static ulong Add(ulong h, ulong value)
    {
        unchecked
        {
            for (int shift = 0; shift < 64; shift += 8)
            {
                h ^= (value >> shift) & 0xFFUL;
                h *= 0x100000001B3UL;
            }

            return h;
        }
    }

    private static string Format(Golden g)
    {
        return $"new({g.Width}, {g.Depth}, {g.CellCount}, 0x{g.CellOrderHash:X16}UL, 0x{g.WorldChecksum:X16}UL, " +
            $"{g.GrassCells}, {g.StoneCells}, {g.RockCells}, {g.DirtCells}, {g.SandCells}, {g.MudCells}, {g.GrassPathCells})";
    }
}
