using BulletSharp;
using Dream.Simulation.Physics;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using Dream.Simulation.World.MapSources;
using Xunit;

namespace Dream.Tests.Simulation.World;

// RemoveSourceCell is the live destruction path (RemoveCube) applied to a cell of the base terrain.
// Wave D replays persisted terrain removals through it on restart, so a map that removed a cell must
// be indistinguishable from a map whose source never had the cell.
public class MapSourceSeamTests
{
    private const int Size = 56;

    private static MapDefinition Definition { get; } =
        new(Size, 8, Size, WorldMapSource.Generated, WorldGeneratorCatalog.DefaultKey, WorldGeneratorCatalog.DefaultVersion);

    private static GeneratedMapSource CreateSource() => new(Definition);

    private static GeometryCellKey FirstRaisedCell(bool sculpture)
    {
        return CreateSource().CreateCells()
            .First(record => record.Key.Y > 0 && (record.SubdivisionState != null) == sculpture)
            .Key;
    }

    // The last raised cells of each kind, so a test can remove more than one.
    private static List<GeometryCellKey> SomeCells()
    {
        IReadOnlyList<GeometryCellRecord> cells = CreateSource().CreateCells();
        List<GeometryCellKey> result = [];
        result.Add(cells.First(record => record.Key.Y > 0 && record.SubdivisionState == null).Key);
        result.Add(cells.Last(record => record.Key.Y > 0 && record.SubdivisionState == null).Key);
        result.Add(cells.First(record => record.Key.Y > 0 && record.SubdivisionState != null).Key);
        result.Add(cells.Last(record => record.Key.Y > 0 && record.SubdivisionState != null).Key);
        return result;
    }

    [Fact]
    public void Map_ExposesItsSourceAndMarksEveryRaisedCellAsASourceCell()
    {
        GeneratedMapSource source = CreateSource();
        Map map = new(source, physics: null, headless: true);

        Assert.Same(source, map.Source);
        Assert.All(map.GeometryCells.Keys, key => Assert.Equal(key.Y > 0, map.IsSourceCell(key)));
        Assert.False(map.IsSourceCell(new GeometryCellKey(0, 7, 0)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RemoveSourceCell_KeepsInvariantsAndChecksumsConsistent(bool sculpture)
    {
        using PhysicsIntegrator physics = new();
        Map map = new(CreateSource(), physics, headless: true);
        GeometryCellKey cell = FirstRaisedCell(sculpture);
        ulong checksumBefore = map.WorldChecksum;
        int countBefore = map.GeometryCells.Count;

        Assert.True(map.IsSourceCell(cell));
        Assert.True(map.RemoveSourceCell(cell));

        Assert.Empty(map.FindInvariantViolations());
        Assert.Equal(WorldStateChecksum.ComputeFull(map), map.WorldChecksum);
        // The checksum covers intact cubes and rubble only; a sculpture (subdivided) cell is not in it.
        if (!sculpture)
        {
            Assert.NotEqual(checksumBefore, map.WorldChecksum);
        }

        Assert.Equal(countBefore - 1, map.GeometryCells.Count);
        Assert.False(map.GeometryCells.ContainsKey(cell));
        Assert.False(map.IsSourceCell(cell));
        Assert.Null(map.Cubes[cell.X, cell.Y, cell.Z]);
        Assert.False(map.RemoveSourceCell(cell));
    }

    [Fact]
    public void RemoveSourceCell_RejectsFloorMissingAndPlacedCells()
    {
        Map map = new(CreateSource(), physics: null, headless: true);
        GeometryCellKey airCell = new(0, 7, 0);

        Assert.False(map.RemoveSourceCell(new GeometryCellKey(3, 0, 3)));
        Assert.False(map.RemoveSourceCell(airCell));

        Assert.True(map.AddCube(new OpenTK.Mathematics.Vector3(0.5f, 7.5f, 0.5f), isStatic: false, withPhysics: false));
        Assert.False(map.IsSourceCell(airCell));
        Assert.False(map.RemoveSourceCell(airCell));
    }

    [Fact]
    public void LiveFractureOfASourceCellIsNoLongerASourceCell()
    {
        Map map = new(CreateSource(), physics: null, headless: true);
        GeometryCellKey cell = FirstRaisedCell(sculpture: false);

        Assert.True(map.IsSourceCell(cell));
        Assert.True(map.FractureCubeIntoPieces(cell));

        Assert.False(map.IsSourceCell(cell));
        Assert.NotNull(map.TryGetRubbleAtCell(cell));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RemoveSourceCell_LeavesTheSameCollisionBodiesAsASourceWithoutTheCell(bool sculpture)
    {
        GeometryCellKey cell = FirstRaisedCell(sculpture);
        using PhysicsIntegrator removedPhysics = new();
        using PhysicsIntegrator filteredPhysics = new();
        Map removed = new(CreateSource(), removedPhysics, headless: true);
        Map filtered = new(new FilteredMapSource(CreateSource(), cell), filteredPhysics, headless: true);

        Assert.True(removed.RemoveSourceCell(cell));

        Assert.Equal(filtered.GeometryCells.Count, removed.GeometryCells.Count);
        Assert.Equal(filtered.WorldChecksum, removed.WorldChecksum);
        Assert.Equal(CollisionMultiset(filtered), CollisionMultiset(removed));
        Assert.Empty(removed.FindInvariantViolations());
        Assert.Empty(filtered.FindInvariantViolations());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RemoveSourceCell_LeavesTheSamePhysicsBodiesAsASourceWithoutTheCell(bool sculpture)
    {
        GeometryCellKey cell = FirstRaisedCell(sculpture);
        using PhysicsIntegrator removedPhysics = new();
        using PhysicsIntegrator filteredPhysics = new();
        Map removed = new(CreateSource(), removedPhysics, headless: true);
        Map filtered = new(new FilteredMapSource(CreateSource(), cell), filteredPhysics, headless: true);

        Assert.True(removed.RemoveSourceCell(cell));

        Assert.Equal(filteredPhysics.MapCubeBodies.Count, removedPhysics.MapCubeBodies.Count);
        Assert.Equal(BodyMultiset(filteredPhysics), BodyMultiset(removedPhysics));
        Assert.Equal(filtered.CellSupportBodyCount, removed.CellSupportBodyCount);
    }

    [Fact]
    public void TheSameRemovalSequenceGivesTheSamePhysicsBodyOrder()
    {
        List<GeometryCellKey> cells = SomeCells();
        using PhysicsIntegrator physicsA = new();
        using PhysicsIntegrator physicsB = new();
        Map a = new(CreateSource(), physicsA, headless: true);
        Map b = new(CreateSource(), physicsB, headless: true);

        foreach (GeometryCellKey cell in cells)
        {
            Assert.True(a.RemoveSourceCell(cell));
            Assert.True(b.RemoveSourceCell(cell));
        }

        Assert.Equal(BodySequence(physicsA), BodySequence(physicsB));
        Assert.Equal(a.WorldChecksum, b.WorldChecksum);
        Assert.Empty(a.FindInvariantViolations());
        Assert.Equal(WorldStateChecksum.ComputeFull(a), a.WorldChecksum);
    }

    [Fact]
    public void CopyPlacedCubesTo_KeepsTerrainRemovalsOnTheTarget()
    {
        using PhysicsIntegrator physicsA = new();
        using PhysicsIntegrator physicsB = new();
        Map previous = new(CreateSource(), physicsA, headless: true);
        Map rebuilt = new(CreateSource(), physicsB, headless: true);
        List<GeometryCellKey> cells = SomeCells();
        foreach (GeometryCellKey cell in cells)
        {
            Assert.True(previous.RemoveSourceCell(cell));
        }

        previous.CopyPlacedCubesTo(rebuilt);

        Assert.All(cells, cell =>
        {
            Assert.False(rebuilt.GeometryCells.ContainsKey(cell));
            Assert.False(rebuilt.IsSourceCell(cell));
        });
        Assert.Equal(previous.GeometryCells.Count, rebuilt.GeometryCells.Count);
        Assert.Equal(previous.WorldChecksum, rebuilt.WorldChecksum);
        Assert.Empty(rebuilt.FindInvariantViolations());
    }

    [Fact]
    public void RestoreGeneratedStartupCells_ReturnsARemovedCellToTheSourceSet()
    {
        Map map = new(CreateSource(), physics: null, headless: true);
        GeometryCellKey cell = FirstRaisedCell(sculpture: false);
        ulong checksumBefore = map.WorldChecksum;

        Assert.True(map.RemoveSourceCell(cell));
        Assert.Equal(1, map.RestoreGeneratedStartupCells([cell]));

        Assert.True(map.IsSourceCell(cell));
        Assert.Equal(checksumBefore, map.WorldChecksum);

        // Restored, then replaced by a CopyPlacedCubesTo target: no removal is replayed for it.
        Map target = new(CreateSource(), physics: null, headless: true);
        map.CopyPlacedCubesTo(target);
        Assert.True(target.GeometryCells.ContainsKey(cell));
    }

    private static List<string> CollisionMultiset(Map map)
    {
        return map.CaptureGeometryCollisionExport().Bodies
            .Select(body => $"{body.Kind}|{body.Center}|{body.HalfExtents}|{body.IsStatic}|{body.CellKey}|{body.SubcellKey}|{body.FracturePieceIndex}")
            .OrderBy(text => text, StringComparer.Ordinal)
            .ToList();
    }

    private static string Describe(RigidBody body)
    {
        BulletSharp.Math.Vector3 origin = body.WorldTransform.Origin;
        body.GetAabb(out BulletSharp.Math.Vector3 min, out BulletSharp.Math.Vector3 max);
        return $"{origin.X:R},{origin.Y:R},{origin.Z:R}|{body.CollisionShape.ShapeType}|{max.X - min.X:R},{max.Y - min.Y:R},{max.Z - min.Z:R}|{body.InvMass:R}|{body.CollisionFlags}";
    }

    private static List<string> BodySequence(PhysicsIntegrator physics)
    {
        return physics.MapCubeBodies.Select(Describe).ToList();
    }

    private static List<string> BodyMultiset(PhysicsIntegrator physics)
    {
        return BodySequence(physics).OrderBy(text => text, StringComparer.Ordinal).ToList();
    }

    private sealed class FilteredMapSource(IMapSource inner, GeometryCellKey excluded) : IMapSource
    {
        public MapDefinition Definition => inner.Definition;

        public IReadOnlyList<GeometryCellRecord> CreateCells()
        {
            return inner.CreateCells().Where(record => record.Key != excluded).ToArray();
        }
    }
}
