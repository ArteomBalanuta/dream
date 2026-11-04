using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using Dream.Simulation.World.MapSources;
using Xunit;
using Xunit.Abstractions;

namespace Dream.Tests.Simulation.World;

// The noise-driven generator ("natural", version 1). The default generator's own output is pinned
// by MapSourceGoldenTests; these tests cover what the new key adds: that it is registered, that it
// is deterministic, that it changes only the material of a cell and never the shape of the world,
// that the hand-placed landmarks survive the noise, and that the distribution is actually more
// coherent than the modulo-hash scatter it replaces.
public class NaturalMapGeneratorTests(ITestOutputHelper output)
{
    private const int Size = 56;
    private const int Height = 8;
    private const ulong Seed = 0x1234_5678_9ABC_DEF0UL;

    // What a generated floor may be made of. Lamp is player-placed only; Snow has no generator.
    private static readonly GeometryMaterialId[] LegalSurface =
    [
        GeometryMaterialId.Grass,
        GeometryMaterialId.Dirt,
        GeometryMaterialId.Stone,
        GeometryMaterialId.Sand,
        GeometryMaterialId.Mud,
        GeometryMaterialId.Rock,
        GeometryMaterialId.GrassPath
    ];

    [Fact]
    public void CatalogHostsTheNaturalGenerator()
    {
        Assert.True(WorldGeneratorCatalog.IsSupported(
            WorldGeneratorCatalog.NaturalKey,
            WorldGeneratorCatalog.NaturalVersion));
        Assert.True(WorldGeneratorCatalog.TryGet(
            WorldGeneratorCatalog.NaturalKey,
            WorldGeneratorCatalog.NaturalVersion,
            out IMapGenerator? generator));
        Assert.IsType<NaturalMapGenerator>(generator);

        // The default generator keeps its own key and version: a world row built before this
        // generator existed must still resolve.
        Assert.True(WorldGeneratorCatalog.IsSupported(
            WorldGeneratorCatalog.DefaultKey,
            WorldGeneratorCatalog.DefaultVersion));
    }

    [Fact]
    public void NaturalWorldValidatesAndKeepsItsGenerator()
    {
        WorldDescriptor world = WorldDescriptor.ForFloorSize(
            Size,
            generatorKey: WorldGeneratorCatalog.NaturalKey);
        world.Validate();
        Assert.Equal(WorldGeneratorCatalog.NaturalKey, world.GeneratorKey);
        Assert.Equal(WorldGeneratorCatalog.NaturalVersion, world.GeneratorVersion);

        // An unregistered key never reaches a world row.
        Assert.Throws<ArgumentException>(() =>
            WorldDescriptor.ForFloorSize(Size, generatorKey: "no-such-generator").Validate());
    }

    [Fact]
    public void SameRequestGivesTheSameCellsEveryTime()
    {
        IReadOnlyList<GeometryCellRecord> first = Natural(Size);
        IReadOnlyList<GeometryCellRecord> second = Natural(Size);
        Assert.Equal(first.Count, second.Count);
        Assert.Equal(Fingerprint(first), Fingerprint(second));
    }

    [Fact]
    public void SeedDoesNotChangeTheWorld()
    {
        // Deliberate: the client rebuilds a generated world from its descriptor and never receives
        // the simulation seed, so a seed-dependent generator would desynchronise the ground.
        IReadOnlyList<GeometryCellRecord> withSeed = Natural(Size, Seed);
        IReadOnlyList<GeometryCellRecord> withoutSeed = Natural(Size, 0UL);
        Assert.Equal(Fingerprint(withSeed), Fingerprint(withoutSeed));
    }

    [Fact]
    public void ChangesOnlyTheMaterialOfACell()
    {
        IReadOnlyList<GeometryCellRecord> classic = Default(Size);
        IReadOnlyList<GeometryCellRecord> natural = Natural(Size);

        Assert.Equal(classic.Count, natural.Count);
        bool anyMaterialMoved = false;
        for (int i = 0; i < classic.Count; i++)
        {
            GeometryCellRecord a = classic[i];
            GeometryCellRecord b = natural[i];
            // Same cell, same position in the insertion order: the geometry walk is shared, so the
            // startup physics body order cannot drift between the two generators.
            Assert.Equal(a.Key, b.Key);
            Assert.Equal(a.Definition.ShapeKind, b.Definition.ShapeKind);
            Assert.Equal(a.Definition.SubdivisionResolution, b.Definition.SubdivisionResolution);
            Assert.Equal(a.Definition.IsSolid, b.Definition.IsSolid);
            Assert.Equal(a.Definition.IsFloor, b.Definition.IsFloor);
            anyMaterialMoved |= a.Definition.MaterialId != b.Definition.MaterialId;
        }

        Assert.True(anyMaterialMoved, "the natural generator produced the classic distribution");
    }

    [Fact]
    public void SurfaceMaterialsStayLegal()
    {
        foreach (GeometryCellRecord cell in Natural(Size))
        {
            Assert.Contains(cell.Definition.MaterialId, LegalSurface);
        }
    }

    [Fact]
    public void KeepsTheHandPlacedLandmarks()
    {
        Dictionary<GeometryCellKey, GeometryCellRecord> cells = ByKey(Natural(Size));

        for (int x = 0; x < Size; x++)
        {
            for (int z = 0; z < Size; z++)
            {
                GeometryMaterialId material = cells[new GeometryCellKey(x, 0, z)].Definition.MaterialId;
                int rim = TerrainLandmarks.RimDistance(x, z, Size, Size);
                if (rim == 0)
                {
                    Assert.Equal(GeometryMaterialId.Stone, material);
                }
                else if (rim <= 2)
                {
                    Assert.Equal(GeometryMaterialId.Rock, material);
                }
                else if (TerrainLandmarks.IsSpawnPath(x, z, Size, Size))
                {
                    Assert.Equal(GeometryMaterialId.Dirt, material);
                }
                else if (TerrainLandmarks.IsCentralPath(x, z, Size, Size))
                {
                    Assert.Equal(GeometryMaterialId.GrassPath, material);
                }
            }
        }

        // The bridge onto the spawn island is still walkable dirt, and the island's rim still reads
        // as a stone edge.
        Assert.Equal(
            GeometryMaterialId.Dirt,
            cells[new GeometryCellKey(7, 1, 5)].Definition.MaterialId);
        Assert.Equal(
            GeometryMaterialId.Stone,
            cells[new GeometryCellKey(9, 1, 3)].Definition.MaterialId);
    }

    [Fact]
    public void DistributionIsCoherentNotSpeckled()
    {
        // The measure of "organic" that can be asserted. Both policies leave one huge grass region,
        // so counting patches or side-by-side disagreements separates them poorly; what differs is
        // the size of a patch of anything else. The modulo hash can only ever place a lone 3x3
        // block, because it quantises to 3x3 and its neighbouring blocks are uncorrelated, while
        // continuous fields give a basin that runs for dozens of cells.
        //
        // Sand is the honest material to measure: neither policy ever places it for a rim, a path
        // or a landmark, so the number is the distribution's own work.
        int classicSand = LargestPatch(Default(Size), GeometryMaterialId.Sand);
        int naturalSand = LargestPatch(Natural(Size), GeometryMaterialId.Sand);
        output.WriteLine($"largest sand patch: classic {classicSand}, natural {naturalSand}");
        Assert.True(
            naturalSand > classicSand * 5,
            $"the natural sand patch ({naturalSand} cells) is not a region next to the classic block ({classicSand})");
    }

    [Fact]
    public void EveryBiomeMaterialAppears()
    {
        Dictionary<GeometryMaterialId, int> histogram = [];
        foreach (GeometryCellRecord cell in Natural(Size))
        {
            GeometryMaterialId material = cell.Definition.MaterialId;
            histogram[material] = histogram.TryGetValue(material, out int count) ? count + 1 : 1;
        }

        foreach ((GeometryMaterialId material, int count) in histogram.OrderByDescending(e => e.Value))
        {
            output.WriteLine($"{material}: {count}");
        }

        // Every material the policy can weight has to earn some ground on a startup world, or the
        // ramp that gates it is dead code.
        Assert.Contains(GeometryMaterialId.Grass, histogram.Keys);
        Assert.Contains(GeometryMaterialId.Dirt, histogram.Keys);
        Assert.Contains(GeometryMaterialId.Stone, histogram.Keys);
        Assert.Contains(GeometryMaterialId.Rock, histogram.Keys);
        Assert.Contains(GeometryMaterialId.Sand, histogram.Keys);
        Assert.Contains(GeometryMaterialId.Mud, histogram.Keys);
    }

    [Theory]
    [InlineData(20)]
    [InlineData(48)]
    [InlineData(100)]
    public void WorksAtEverySupportedWorldSize(int size)
    {
        IReadOnlyList<GeometryCellRecord> cells = Natural(size);
        Assert.NotEmpty(cells);
        Assert.Equal(Natural(size).Count, cells.Count);
        foreach (GeometryCellRecord cell in cells)
        {
            Assert.Contains(cell.Definition.MaterialId, LegalSurface);
        }
    }

    // The biggest four-connected region of one material on the floor, flooded with an explicit
    // stack so a 100x100 world cannot recurse into a stack overflow.
    private static int LargestPatch(
        IReadOnlyList<GeometryCellRecord> cells,
        GeometryMaterialId wanted,
        int size = Size)
    {
        Dictionary<GeometryCellKey, GeometryCellRecord> byKey = ByKey(cells);
        GeometryMaterialId[,] floor = new GeometryMaterialId[size, size];
        bool[,] seen = new bool[size, size];
        for (int x = 0; x < size; x++)
        {
            for (int z = 0; z < size; z++)
            {
                floor[x, z] = byKey[new GeometryCellKey(x, 0, z)].Definition.MaterialId;
            }
        }

        int largest = 0;
        Stack<(int X, int Z)> pending = new();
        for (int x = 0; x < size; x++)
        {
            for (int z = 0; z < size; z++)
            {
                if (seen[x, z] || floor[x, z] != wanted)
                {
                    continue;
                }

                int patch = 0;
                seen[x, z] = true;
                pending.Push((x, z));
                while (pending.Count > 0)
                {
                    (int cx, int cz) = pending.Pop();
                    patch++;
                    Visit(cx - 1, cz);
                    Visit(cx + 1, cz);
                    Visit(cx, cz - 1);
                    Visit(cx, cz + 1);
                }

                largest = Math.Max(largest, patch);

                void Visit(int nx, int nz)
                {
                    if (nx < 0 || nz < 0 || nx >= size || nz >= size || seen[nx, nz] || floor[nx, nz] != wanted)
                    {
                        return;
                    }

                    seen[nx, nz] = true;
                    pending.Push((nx, nz));
                }
            }
        }

        return largest;
    }

    private static Dictionary<GeometryCellKey, GeometryCellRecord> ByKey(
        IReadOnlyList<GeometryCellRecord> cells)
    {
        Dictionary<GeometryCellKey, GeometryCellRecord> byKey = new(cells.Count);
        foreach (GeometryCellRecord cell in cells)
        {
            byKey[cell.Key] = cell;
        }

        return byKey;
    }

    private static string Fingerprint(IReadOnlyList<GeometryCellRecord> cells)
    {
        System.Text.StringBuilder text = new(cells.Count * 12);
        foreach (GeometryCellRecord cell in cells)
        {
            text.Append(cell.Key.X).Append(',')
                .Append(cell.Key.Y).Append(',')
                .Append(cell.Key.Z).Append(':')
                .Append((int)cell.Definition.MaterialId).Append(';');
        }

        return text.ToString();
    }

    private static IReadOnlyList<GeometryCellRecord> Natural(int size, ulong seed = Seed)
    {
        return Cells(WorldGeneratorCatalog.NaturalKey, size, seed);
    }

    private static IReadOnlyList<GeometryCellRecord> Default(int size, ulong seed = Seed)
    {
        return Cells(WorldGeneratorCatalog.DefaultKey, size, seed);
    }

    private static IReadOnlyList<GeometryCellRecord> Cells(string key, int size, ulong seed)
    {
        Assert.True(WorldGeneratorCatalog.TryGetLatest(key, out IMapGenerator? generator));
        return generator.Create(new MapGenerationRequest(size, Height, size, seed)).CreateCells();
    }
}
