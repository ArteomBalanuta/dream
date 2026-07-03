using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Simulation.World.Geometry;

public class MapGeometryFacadeTests
{
    [Fact]
    public void Map_Initializes_Floor_As_Geometry_Records()
    {
        Map map = new Map(width: 24, height: 8, depth: 24, loadVisualAssets: false, generateStartupTerrain: true);

        Assert.True(map.GeometryCells.Count >= 24 * 24);
        GeometryCellRecord floorRecord = Assert.IsType<GeometryCellRecord>(map.TryGetGeometryCellAtCell(0, 0, 0));
        Assert.True(floorRecord.Definition.IsFloor);
        Assert.Equal(GeometryShapeKind.SolidCube, floorRecord.Definition.ShapeKind);
        Assert.Equal(GeometryMaterialId.Stone, floorRecord.Definition.MaterialId);
    }

    [Fact]
    public void AddCube_Adds_Geometry_Record_Alongside_Compatibility_Cube()
    {
        Map map = new Map(width: 8, height: 8, depth: 8, loadVisualAssets: false);

        Cube cube = Assert.IsType<Cube>(map.TryAddCube(new Vector3(3f, 1f, 3f), withPhysics: false));
        GeometryCellRecord geometryCell = Assert.IsType<GeometryCellRecord>(map.TryGetGeometryCellAtCell(3, 1, 3));

        Assert.False(geometryCell.Definition.IsFloor);
        Assert.Equal(GeometryShapeKind.SolidCube, geometryCell.Definition.ShapeKind);
        Assert.Equal(new GeometryCellKey(3, 1, 3), geometryCell.Key);
        Assert.Equal(new Vector3(3.5f, 1.5f, 3.5f), cube.Position);
    }

    [Fact]
    public void ApplyGeometrySubcellSolid_Activates_Subdivision_Without_Adding_Neighbor_Cubes()
    {
        Map map = new Map(width: 8, height: 8, depth: 8, loadVisualAssets: false);
        map.AddCube(new Vector3(3f, 1f, 3f), withPhysics: false);
        int initialSolidCount = map.SolidCubes.Count;

        GeometryMutationResult result = map.ApplyGeometrySubcellSolid(
            new GeometryCellKey(3, 1, 3),
            new GeometrySubcellKey(1, 1, 1),
            isSolid: false);

        GeometryCellRecord geometryCell = Assert.IsType<GeometryCellRecord>(map.TryGetGeometryCellAtCell(3, 1, 3));
        Assert.True(result.SubdivisionActivated);
        Assert.True(geometryCell.SubdivisionState != null);
        Assert.Equal(initialSolidCount, map.SolidCubes.Count);
        Assert.Null(map.Cubes[3, 2, 3]);
    }

    [Fact]
    public void CaptureGeometryRenderExport_Uses_Nearby_Compatibility_Cubes()
    {
        Map map = new Map(width: 20, height: 8, depth: 20, loadVisualAssets: false);
        map.AddCube(new Vector3(3f, 1f, 3f), withPhysics: false);
        map.AddCube(new Vector3(18f, 1f, 18f), withPhysics: false);

        GeometryRenderExport export = map.CaptureGeometryRenderExport(new Vector3(2f, 1.5f, 2f), 8f);

        Assert.Contains(export.Cells, cell => cell.Key == new GeometryCellKey(3, 1, 3));
        Assert.DoesNotContain(export.Cells, cell => cell.Key == new GeometryCellKey(18, 1, 18));
        Assert.Contains(export.DynamicCompatibilityCubes, cube => cube.Position == new Vector3(3.5f, 1.5f, 3.5f));
    }

    [Fact]
    public void CaptureGeometryCollisionExport_Includes_Merged_Floor_And_Placed_Cube_Bodies()
    {
        Map map = new Map(width: 8, height: 8, depth: 8, loadVisualAssets: false);
        map.AddCube(new Vector3(3f, 1f, 3f), withPhysics: false);

        GeometryCollisionExport export = map.CaptureGeometryCollisionExport();

        Assert.Contains(export.Bodies, body => body.Kind == GeometryCollisionBodyKind.Floor);
        Assert.Contains(export.Bodies, body => body.Kind == GeometryCollisionBodyKind.Cell && body.CellKey == new GeometryCellKey(3, 1, 3));
    }

    [Fact]
    public void CaptureGeometryRenderExport_ForSubdividedCell_UsesRefinedBlocksInsteadOfCoarseCompatibilityCube()
    {
        Map map = new Map(width: 8, height: 8, depth: 8, loadVisualAssets: false);
        map.AddCube(new Vector3(3f, 1f, 3f), withPhysics: false);

        map.ApplyGeometrySubcellSolid(
            new GeometryCellKey(3, 1, 3),
            new GeometrySubcellKey(0, 0, 0),
            isSolid: false);

        GeometryRenderExport export = map.CaptureGeometryRenderExport(new Vector3(3f, 1.5f, 3f), 4f);

        Assert.DoesNotContain(export.CompatibilityCubes, cube => cube.Position == new Vector3(3.5f, 1.5f, 3.5f));
        Assert.DoesNotContain(export.DynamicCompatibilityCubes, cube => cube.Position == new Vector3(3.5f, 1.5f, 3.5f));
        Assert.Contains(export.Blocks, block => block.CellKey == new GeometryCellKey(3, 1, 3));
        Assert.DoesNotContain(export.Blocks, block => block.SubcellKey == new GeometrySubcellKey(0, 0, 0));
        Assert.Contains(new GeometryCellKey(3, 1, 3), export.DirtyCells);
    }

    [Fact]
    public void CaptureGeometryCollisionExport_ForSubdividedCell_UsesRefinedSubcellBodies()
    {
        Map map = new Map(width: 8, height: 8, depth: 8, loadVisualAssets: false);
        map.AddCube(new Vector3(3f, 1f, 3f), withPhysics: false);

        map.ApplyGeometrySubcellSolid(
            new GeometryCellKey(3, 1, 3),
            new GeometrySubcellKey(0, 0, 0),
            isSolid: false);

        GeometryCollisionExport export = map.CaptureGeometryCollisionExport();

        Assert.DoesNotContain(export.Bodies, body => body.Kind == GeometryCollisionBodyKind.Cell && body.CellKey == new GeometryCellKey(3, 1, 3));
        Assert.Contains(export.Bodies, body => body.Kind == GeometryCollisionBodyKind.Subcell && body.CellKey == new GeometryCellKey(3, 1, 3));
        Assert.DoesNotContain(export.Bodies, body => body.SubcellKey == new GeometrySubcellKey(0, 0, 0));
        Assert.Contains(new GeometryCellKey(3, 1, 3), export.DirtyCells);
    }

    [Fact]
    public void FractureCubeIntoPieces_ReplacesCoarseCellWithDeterministicRubblePieces()
    {
        Map map = new Map(width: 8, height: 8, depth: 8, loadVisualAssets: false);
        map.AddCube(new Vector3(3f, 1f, 3f), withPhysics: false);

        bool fractured = map.FractureCubeIntoPieces(new GeometryCellKey(3, 1, 3));

        GeometryCellRecord geometryCell = Assert.IsType<GeometryCellRecord>(map.TryGetGeometryCellAtCell(3, 1, 3));
        GeometryRenderExport export = map.CaptureGeometryRenderExport(new Vector3(3.5f, 1.5f, 3.5f), 4f);
        GeometryCollisionExport collision = map.CaptureGeometryCollisionExport();

        Assert.True(fractured);
        Assert.NotNull(geometryCell.SubdivisionState);
        Assert.DoesNotContain(export.CompatibilityCubes, cube => cube.Position == new Vector3(3.5f, 1.5f, 3.5f));
        Assert.DoesNotContain(export.DynamicCompatibilityCubes, cube => cube.Position == new Vector3(3.5f, 1.5f, 3.5f));
        Assert.Equal(4, export.Blocks.Count(block => block.CellKey == new GeometryCellKey(3, 1, 3)));
        Assert.Equal(4, collision.Bodies.Count(body => body.CellKey == new GeometryCellKey(3, 1, 3)));
        Assert.All(export.Blocks, block =>
        {
            Assert.InRange(block.SideLength, 0.18f, 0.42f);
            Assert.InRange((block.Center - new Vector3(3.5f, 1.5f, 3.5f)).Length, 0.05f, 0.6f);
        });
    }

    [Fact]
    public void FractureCubeIntoPieces_UsesSameCentersAndSizesForRenderAndCollisionPieces()
    {
        Map map = new Map(width: 8, height: 8, depth: 8, loadVisualAssets: false);
        map.AddCube(new Vector3(3f, 1f, 3f), withPhysics: false);

        map.FractureCubeIntoPieces(new GeometryCellKey(3, 1, 3));

        GeometryRenderExport renderExport = map.CaptureGeometryRenderExport(new Vector3(3.5f, 1.5f, 3.5f), 4f);
        GeometryCollisionExport collisionExport = map.CaptureGeometryCollisionExport();

        Assert.Equal(4, renderExport.Blocks.Count);
        Assert.Equal(4, collisionExport.Bodies.Count(body => body.Kind == GeometryCollisionBodyKind.Subcell));
        foreach (GeometryRenderBlockDescriptor block in renderExport.Blocks)
        {
            GeometryCollisionBodyDescriptor collisionBody = Assert.Single(
                collisionExport.Bodies,
                body => body.CellKey == block.CellKey &&
                    body.Center == block.Center &&
                    MathF.Abs((body.HalfExtents.X * 2f) - block.SideLength) < 0.0001f);

            Assert.Equal(block.SideLength * 0.5f, collisionBody.HalfExtents.X, 4);
            Assert.Equal(block.SideLength * 0.5f, collisionBody.HalfExtents.Y, 4);
            Assert.Equal(block.SideLength * 0.5f, collisionBody.HalfExtents.Z, 4);
        }
    }

    [Fact]
    public void StartupSculpture_VisualFillScale_DoesNotInflateCollisionBodies()
    {
        Map map = new Map(width: 56, height: 8, depth: 56, loadVisualAssets: false, generateStartupTerrain: true);

        GeometryRenderExport renderExport = map.CaptureGeometryRenderExport(new Vector3(18.5f, 2f, 16.5f), 8f);
        GeometryCollisionExport collisionExport = map.CaptureGeometryCollisionExport();
        GeometryRenderBlockDescriptor visualBlock = Assert.Single(
            renderExport.Blocks
                .Where(block => block.VisualFillScale > 1f)
                .Take(1));

        GeometryCollisionBodyDescriptor collisionBody = Assert.Single(
            collisionExport.Bodies,
            body =>
                body.Kind == GeometryCollisionBodyKind.Subcell &&
                ContainsPoint(body, visualBlock.Center));

        Assert.True(visualBlock.VisualSideLength > visualBlock.SideLength);
        AssertGridAlignedExtent(collisionBody.HalfExtents.X * 2f, visualBlock.SideLength);
        AssertGridAlignedExtent(collisionBody.HalfExtents.Y * 2f, visualBlock.SideLength);
        AssertGridAlignedExtent(collisionBody.HalfExtents.Z * 2f, visualBlock.SideLength);
    }

    [Fact]
    public void StartupSculpture_Merges_StaticSubcellCollisionBodies()
    {
        Map map = new Map(width: 56, height: 8, depth: 56, loadVisualAssets: false, generateStartupTerrain: true);

        GeometryRenderExport renderExport = map.CaptureGeometryRenderExport(new Vector3(24f, 2f, 16f), WorldVisibility.MaxRenderDistance);
        GeometryCollisionExport collisionExport = map.CaptureGeometryCollisionExport();
        int sculptedRenderBlocks = renderExport.Blocks.Count(block => block.CellKey.Y > 0);
        int sculptedCollisionBodies = collisionExport.Bodies.Count(body =>
            body.Kind == GeometryCollisionBodyKind.Subcell &&
            body.CellKey.HasValue &&
            body.CellKey.Value.Y > 0);

        Assert.InRange(sculptedRenderBlocks, 4_000, 12_000);
        Assert.True(sculptedCollisionBodies < 12_000);
    }

    [Fact]
    public void StartupSculpture_Merges_StaticSubcellCollisionBodies_Across_Cell_Boundaries()
    {
        Map map = new Map(width: 56, height: 8, depth: 56, loadVisualAssets: false, generateStartupTerrain: true);

        GeometryRenderExport renderExport = map.CaptureGeometryRenderExport(new Vector3(24f, 2f, 16f), WorldVisibility.MaxRenderDistance);
        GeometryCollisionExport collisionExport = map.CaptureGeometryCollisionExport();
        int sculptedRenderBlocks = renderExport.Blocks.Count(block => block.CellKey.Y > 0);
        int sculptedCollisionBodies = collisionExport.Bodies.Count(body =>
            body.Kind == GeometryCollisionBodyKind.Subcell &&
            body.CellKey.HasValue &&
            body.CellKey.Value.Y > 0);

        Assert.True(
            sculptedCollisionBodies <= sculptedRenderBlocks,
            $"Expected static collision merge to be at least as compact as render merge, but collision={sculptedCollisionBodies}, render={sculptedRenderBlocks}.");
    }

    [Fact]
    public void StartupSculpture_Uses_Coarse_StaticCollision_For_ResponsiveBootstrap()
    {
        Map map = new Map(width: 56, height: 8, depth: 56, loadVisualAssets: false, generateStartupTerrain: true);

        GeometryCollisionExport collisionExport = map.CaptureGeometryCollisionExport();
        int sculptedCollisionBodies = collisionExport.Bodies.Count(body =>
            body.Kind == GeometryCollisionBodyKind.Subcell &&
            body.CellKey.HasValue &&
            body.CellKey.Value.Y > 0);

        Assert.True(
            sculptedCollisionBodies < 1_000,
            $"Expected static startup sculpture collision to stay bootstrap-cheap, but exported {sculptedCollisionBodies} subcell bodies.");
    }

    private static bool ContainsPoint(GeometryCollisionBodyDescriptor body, Vector3 point)
    {
        Vector3 delta = point - body.Center;
        return MathF.Abs(delta.X) <= body.HalfExtents.X + 0.0001f &&
            MathF.Abs(delta.Y) <= body.HalfExtents.Y + 0.0001f &&
            MathF.Abs(delta.Z) <= body.HalfExtents.Z + 0.0001f;
    }

    private static void AssertGridAlignedExtent(float extent, float sideLength)
    {
        float units = extent / sideLength;
        Assert.Equal(MathF.Round(units), units, precision: 3);
    }

    [Fact]
    public void FractureCubeIntoPieces_IsDeterministicPerCell()
    {
        Map first = new Map(width: 8, height: 8, depth: 8, loadVisualAssets: false);
        first.AddCube(new Vector3(3f, 1f, 3f), withPhysics: false);
        first.FractureCubeIntoPieces(new GeometryCellKey(3, 1, 3));

        Map second = new Map(width: 8, height: 8, depth: 8, loadVisualAssets: false);
        second.AddCube(new Vector3(3f, 1f, 3f), withPhysics: false);
        second.FractureCubeIntoPieces(new GeometryCellKey(3, 1, 3));

        GeometryRenderExport firstExport = first.CaptureGeometryRenderExport(new Vector3(3.5f, 1.5f, 3.5f), 4f);
        GeometryRenderExport secondExport = second.CaptureGeometryRenderExport(new Vector3(3.5f, 1.5f, 3.5f), 4f);

        Assert.Equal(
            firstExport.Blocks
                .OrderBy(block => block.Center.X)
                .ThenBy(block => block.Center.Y)
                .ThenBy(block => block.Center.Z)
                .Select(block => (block.Center, block.SideLength))
                .ToArray(),
            secondExport.Blocks
                .OrderBy(block => block.Center.X)
                .ThenBy(block => block.Center.Y)
                .ThenBy(block => block.Center.Z)
                .Select(block => (block.Center, block.SideLength))
                .ToArray());
    }

    [Fact]
    public void FractureCubeIntoPieces_ExportsStaticCollisionPieces()
    {
        Map map = new Map(width: 8, height: 8, depth: 8, loadVisualAssets: false);
        map.AddCube(new Vector3(3f, 1f, 3f), withPhysics: false);

        map.FractureCubeIntoPieces(new GeometryCellKey(3, 1, 3));

        GeometryCollisionExport collisionExport = map.CaptureGeometryCollisionExport();

        Assert.All(
            collisionExport.Bodies.Where(body => body.CellKey == new GeometryCellKey(3, 1, 3)),
            body => Assert.True(body.IsStatic));
    }
}
