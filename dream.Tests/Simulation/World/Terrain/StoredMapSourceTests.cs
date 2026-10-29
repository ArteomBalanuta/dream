using System.Diagnostics;
using BulletSharp;
using Dream.Simulation.Persistence;
using Dream.Simulation.Physics;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using Dream.Simulation.World.MapSources;
using Dream.Simulation.World.Terrain;
using Xunit;
using Xunit.Abstractions;

namespace Dream.Tests.Simulation.World.Terrain;

public class StoredMapSourceTests(ITestOutputHelper output)
{
    private static MapDefinition Def(int size) =>
        new(size, 8, size, WorldMapSource.Generated, WorldGeneratorCatalog.DefaultKey, WorldGeneratorCatalog.DefaultVersion);

    public static IEnumerable<object[]> Sizes() => [[56], [100]];

    [Theory]
    [MemberData(nameof(Sizes))]
    public void ExportCoversTheChunkGridOrderedByZThenX(int size)
    {
        IReadOnlyList<PersistedTerrainChunk> chunks = TerrainChunkExporter.Export(new GeneratedMapSource(Def(size)));
        int perSide = (size + 15) / 16;

        Assert.Equal(perSide * perSide, chunks.Count);
        Assert.True(chunks.Select(c => (c.ChunkZ, c.ChunkX)).SequenceEqual(
            Enumerable.Range(0, perSide).SelectMany(z => Enumerable.Range(0, perSide).Select(x => (z, x)))));
        Assert.All(chunks, c => Assert.Equal(TerrainChunkFormat.CurrentVersion, c.FormatVersion));
        Assert.Equal(new GeneratedMapSource(Def(size)).CreateCells().Count, chunks.Sum(c => c.CellCount));
    }

    [Fact]
    public void StoredSourceReturnsFreshCanonicallyOrderedRecordsOnEveryCall()
    {
        MapDefinition def = Def(56);
        StoredMapSource stored = new(def, TerrainChunkExporter.Export(new GeneratedMapSource(def)));

        IReadOnlyList<GeometryCellRecord> a = stored.CreateCells();
        IReadOnlyList<GeometryCellRecord> b = stored.CreateCells();

        Assert.Equal(a.Count, b.Count);
        Assert.All(a.Zip(b), pair => Assert.NotSame(pair.First, pair.Second));
        Assert.True(a.Select(c => c.Key).SequenceEqual(b.Select(c => c.Key)));
        Assert.True(a.Select(c => c.Key).SequenceEqual(
            a.Select(c => c.Key).OrderBy(k => k.X).ThenBy(k => k.Z).ThenBy(k => k.Y)));
        Assert.All(a, c => Assert.Null(c.FracturePieces));
    }

    [Fact]
    public void StoredSourceValidatesCoverageAndFormatVersion()
    {
        MapDefinition def = Def(20);
        List<PersistedTerrainChunk> chunks = [.. TerrainChunkExporter.Export(new GeneratedMapSource(def))];
        Assert.Equal(4, chunks.Count);
        _ = new StoredMapSource(def, chunks);

        Assert.Throws<InvalidDataException>(() => new StoredMapSource(def, chunks.Take(3).ToList()));
        Assert.Throws<InvalidDataException>(() => new StoredMapSource(def, [.. chunks.Take(3), chunks[0]]));
        Assert.Throws<InvalidDataException>(() => new StoredMapSource(def, [.. chunks.Take(3), chunks[3] with { ChunkX = 2 }]));
        Assert.Throws<InvalidDataException>(() => new StoredMapSource(def, [.. chunks.Take(3), chunks[3] with { FormatVersion = 2 }]));

        // A corrupt blob or a wrong cell count surfaces when the cells are decoded.
        StoredMapSource wrongCount = new(def, [.. chunks.Take(3), chunks[3] with { CellCount = chunks[3].CellCount + 1 }]);
        Assert.Throws<InvalidDataException>(() => wrongCount.CreateCells());
    }

    [Theory]
    [MemberData(nameof(Sizes))]
    public void AMapBuiltFromTheExportedSourceMatchesTheGeneratedMap(int size)
    {
        MapDefinition def = Def(size);
        IReadOnlyList<PersistedTerrainChunk> chunks = TerrainChunkExporter.Export(new GeneratedMapSource(def));
        using PhysicsIntegrator generatedPhysics = new();
        using PhysicsIntegrator storedPhysicsA = new();
        using PhysicsIntegrator storedPhysicsB = new();
        Map generated = new(new GeneratedMapSource(def), generatedPhysics, headless: true);
        Map storedA = new(new StoredMapSource(def, chunks), storedPhysicsA, headless: true);
        Map storedB = new(new StoredMapSource(def, chunks), storedPhysicsB, headless: true);

        Assert.Equal(generated.WorldChecksum, storedA.WorldChecksum);
        Assert.Equal(generated.GeometryCells.Count, storedA.GeometryCells.Count);
        Assert.Equal(
            generated.GeometryCells.Values.Select(Effective).OrderBy(s => s, StringComparer.Ordinal),
            storedA.GeometryCells.Values.Select(Effective).OrderBy(s => s, StringComparer.Ordinal));
        Assert.Equal(CollisionMultiset(generated), CollisionMultiset(storedA));
        Assert.Equal(generatedPhysics.MapCubeBodies.Count, storedPhysicsA.MapCubeBodies.Count);
        Assert.Equal(BodyMultiset(generatedPhysics), BodyMultiset(storedPhysicsA));
        Assert.Equal(generated.CellSupportBodyCount, storedA.CellSupportBodyCount);
        Assert.Empty(storedA.FindInvariantViolations());

        // Two maps built from the same chunks are identical in insertion and body order.
        Assert.True(storedA.GeometryCells.Keys.SequenceEqual(storedB.GeometryCells.Keys));
        Assert.Equal(BodySequence(storedPhysicsA), BodySequence(storedPhysicsB));
        Assert.Equal(CollisionSequence(storedA), CollisionSequence(storedB));
        Assert.Equal(storedA.WorldChecksum, storedB.WorldChecksum);
    }

    [Fact]
    public void ExportOfAMapOmitsARemovedSourceCellAndKeepsTheFloor()
    {
        MapDefinition def = Def(56);
        using PhysicsIntegrator physics = new();
        Map map = new(new GeneratedMapSource(def), physics, headless: true);
        GeometryCellKey removed = map.GeometryCells.Keys.First(k => k.Y > 0 && map.IsSourceCell(k));
        int before = TerrainChunkExporter.Export(map).Sum(c => c.CellCount);
        Assert.Equal(map.GeometryCells.Count, before);

        Assert.True(map.RemoveSourceCell(removed));
        IReadOnlyList<PersistedTerrainChunk> chunks = TerrainChunkExporter.Export(map);

        Assert.Equal(before - 1, chunks.Sum(c => c.CellCount));
        IReadOnlyList<GeometryCellRecord> cells = new StoredMapSource(def, chunks).CreateCells();
        Assert.DoesNotContain(cells, c => c.Key == removed);
        Assert.Contains(cells, c => c.Key.Y == 0 && c.Definition.IsFloor);

        using PhysicsIntegrator rebuiltPhysics = new();
        Map rebuilt = new(new StoredMapSource(def, chunks), rebuiltPhysics, headless: true);
        Assert.Equal(map.WorldChecksum, rebuilt.WorldChecksum);
        Assert.Equal(CollisionMultiset(map), CollisionMultiset(rebuilt));
    }

    [Fact]
    public void World10kBlobSizeAndTiming()
    {
        MapDefinition def = Def(100);
        GeneratedMapSource source = new(def);
        source.CreateCells();
        Stopwatch watch = Stopwatch.StartNew();
        IReadOnlyList<PersistedTerrainChunk> chunks = TerrainChunkExporter.Export(source);
        double encodeMs = watch.Elapsed.TotalMilliseconds;
        StoredMapSource stored = new(def, chunks);
        watch.Restart();
        IReadOnlyList<GeometryCellRecord> cells = stored.CreateCells();
        double decodeMs = watch.Elapsed.TotalMilliseconds;

        long bytes = chunks.Sum(c => (long)c.Data.Length);
        output.WriteLine($"World10k: {chunks.Count} chunks, {cells.Count} cells, {bytes} blob bytes, encode(incl. generate) {encodeMs:F1} ms, decode {decodeMs:F1} ms");
        Assert.Equal(source.CreateCells().Count, cells.Count);
        Assert.True(bytes <= 256 * 1024, $"{bytes} bytes");
    }

    private static string Effective(GeometryCellRecord record)
    {
        GeometryCellDefinition d = record.Definition;
        System.Text.StringBuilder text = new();
        text.Append($"{record.Key}|{d.ShapeKind}|{d.MaterialId}|{d.SubdivisionResolution}|{d.IsSolid}|{d.IsFloor}|{record.SubdivisionState != null}|{record.FracturePieces == null}|");
        if (record.SubdivisionState != null)
        {
            int res = record.SubdivisionState.Resolution;
            text.Append(res).Append('|').Append(record.SubdivisionState.AllChildrenEmpty).Append('|');
            for (int x = 0; x < res; x++)
            {
                for (int y = 0; y < res; y++)
                {
                    for (int z = 0; z < res; z++)
                    {
                        text.Append(record.IsChildSolid(new GeometrySubcellKey(x, y, z)) ? '1' : '0');
                    }
                }
            }
        }

        return text.ToString();
    }

    private static string DescribeCollision(GeometryCollisionBodyDescriptor body) =>
        $"{body.Kind}|{body.Center}|{body.HalfExtents}|{body.IsStatic}|{body.CellKey}|{body.SubcellKey}|{body.FracturePieceIndex}";

    private static List<string> CollisionSequence(Map map) =>
        map.CaptureGeometryCollisionExport().Bodies.Select(DescribeCollision).ToList();

    private static List<string> CollisionMultiset(Map map) =>
        CollisionSequence(map).OrderBy(text => text, StringComparer.Ordinal).ToList();

    private static string Describe(RigidBody body)
    {
        BulletSharp.Math.Vector3 origin = body.WorldTransform.Origin;
        body.GetAabb(out BulletSharp.Math.Vector3 min, out BulletSharp.Math.Vector3 max);
        return $"{origin.X:R},{origin.Y:R},{origin.Z:R}|{body.CollisionShape.ShapeType}|{max.X - min.X:R},{max.Y - min.Y:R},{max.Z - min.Z:R}|{body.InvMass:R}|{body.CollisionFlags}";
    }

    // The Bullet collision-object order, the order the PhysicsBodyOrderHash golden walks.
    private static List<string> BodySequence(PhysicsIntegrator physics)
    {
        List<string> result = physics.MapCubeBodies.Select(Describe).ToList();
        foreach (CollisionObject collisionObject in physics.World.CollisionObjectArray)
        {
            result.Add(Describe((RigidBody)collisionObject));
        }

        return result;
    }

    private static List<string> BodyMultiset(PhysicsIntegrator physics) =>
        BodySequence(physics).OrderBy(text => text, StringComparer.Ordinal).ToList();
}
