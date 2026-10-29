using BulletSharp;
using Dream.Simulation.Physics;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using Dream.Simulation.World.MapSources;
using Xunit;

namespace Dream.Tests.Simulation.World;

// A destroyed sculpture (pyramid) cell used to keep its share of the merged static collision and
// render: an invisible static box stayed in the world. RemoveCube now drops it, as a map built
// without the cell never has it.
public class SculptureFractureColliderTests
{
    private static readonly MapDefinition Definition =
        new(56, 8, 56, WorldMapSource.Generated, WorldGeneratorCatalog.DefaultKey, WorldGeneratorCatalog.DefaultVersion);

    private static GeometryCellKey SculptureCell(int index)
    {
        return new GeneratedMapSource(Definition).CreateCells()
            .Where(record => record.Key.Y > 0 && record.SubdivisionState != null)
            .ElementAt(index).Key;
    }

    private sealed class WithoutCell(GeometryCellKey excluded) : IMapSource
    {
        public MapDefinition Definition => SculptureFractureColliderTests.Definition;

        public IReadOnlyList<GeometryCellRecord> CreateCells()
            => new GeneratedMapSource(Definition).CreateCells().Where(record => record.Key != excluded).ToArray();
    }

    private static List<string> StaticBodies(PhysicsIntegrator physics)
    {
        List<string> result = [];
        foreach (RigidBody body in physics.MapCubeBodies)
        {
            if (body.InvMass != 0f)
            {
                continue;
            }

            BulletSharp.Math.Vector3 origin = body.WorldTransform.Origin;
            body.GetAabb(out BulletSharp.Math.Vector3 min, out BulletSharp.Math.Vector3 max);
            result.Add($"{origin.X:R},{origin.Y:R},{origin.Z:R}|{max.X - min.X:R},{max.Y - min.Y:R},{max.Z - min.Z:R}");
        }

        result.Sort(StringComparer.Ordinal);
        return result;
    }

    private static List<string> StaticCollisionExport(Map map)
    {
        return map.CaptureGeometryCollisionExport().Bodies
            .Where(body => body.IsStatic)
            .Select(body => $"{body.Kind}|{body.Center}|{body.HalfExtents}|{body.CellKey}|{body.SubcellKey}")
            .OrderBy(text => text, StringComparer.Ordinal)
            .ToList();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(100)]
    public void LiveFracture_OfASculptureCellLeavesNoStaticColliderBehind(int index)
    {
        GeometryCellKey cell = SculptureCell(index);
        using PhysicsIntegrator fracturedPhysics = new();
        using PhysicsIntegrator withoutPhysics = new();
        Map fractured = new(new GeneratedMapSource(Definition), fracturedPhysics, headless: true);
        Map without = new(new WithoutCell(cell), withoutPhysics, headless: true);

        Assert.True(fractured.FractureCubeIntoPieces(cell));

        // Fracture adds dynamic rubble bodies; compare the static world only.
        Assert.Equal(StaticCollisionExport(without), StaticCollisionExport(fractured));
        Assert.Equal(StaticBodies(withoutPhysics), StaticBodies(fracturedPhysics));
        Assert.Equal(without.CellSupportBodyCount, fractured.CellSupportBodyCount);
        Assert.Empty(fractured.FindInvariantViolations());
    }

    [Fact]
    public void LiveFracture_OfManySculptureCellsKeepsTheStaticWorldInStep()
    {
        List<GeometryCellKey> cells = Enumerable.Range(0, 12).Select(i => SculptureCell(i * 9)).ToList();
        using PhysicsIntegrator fracturedPhysics = new();
        using PhysicsIntegrator withoutPhysics = new();
        Map fractured = new(new GeneratedMapSource(Definition), fracturedPhysics, headless: true);
        Map without = new(new WithoutCells(cells), withoutPhysics, headless: true);

        foreach (GeometryCellKey cell in cells)
        {
            Assert.True(fractured.FractureCubeIntoPieces(cell));
        }

        Assert.Equal(StaticCollisionExport(without), StaticCollisionExport(fractured));
        Assert.Equal(StaticBodies(withoutPhysics), StaticBodies(fracturedPhysics));
    }

    [Fact]
    public void LiveFracture_OnARenderingMapDropsTheCellFromTheMergedRenderBlocks()
    {
        GeometryCellKey cell = SculptureCell(3);
        Map fractured = new(new GeneratedMapSource(Definition), physics: null);
        Map without = new(new WithoutCell(cell), physics: null);
        long versionBefore = fractured.StaticContentVersion;

        Assert.True(fractured.FractureCubeIntoPieces(cell));

        Assert.NotEqual(versionBefore, fractured.StaticContentVersion);
        // Rubble pieces of the fractured cell are keyed by it; every other block must match.
        List<string> Blocks(Map map) => map.CaptureFullGeometryRenderExport().Blocks
            .Where(block => block.CellKey != cell)
            .Select(block => $"{block.Center}|{block.SideLength}|{block.SizeOverride}|{block.CellKey}")
            .OrderBy(text => text, StringComparer.Ordinal)
            .ToList();
        Assert.Equal(Blocks(without), Blocks(fractured));
    }

    private sealed class WithoutCells(IReadOnlyCollection<GeometryCellKey> excluded) : IMapSource
    {
        public MapDefinition Definition => SculptureFractureColliderTests.Definition;

        public IReadOnlyList<GeometryCellRecord> CreateCells()
            => new GeneratedMapSource(Definition).CreateCells().Where(record => !excluded.Contains(record.Key)).ToArray();
    }
}
