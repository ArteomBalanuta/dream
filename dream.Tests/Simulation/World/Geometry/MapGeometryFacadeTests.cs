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
        Map map = new Map(width: 4, height: 4, depth: 4, loadVisualAssets: false);

        Assert.Equal(16, map.GeometryCells.Count);
        GeometryCellRecord floorRecord = Assert.IsType<GeometryCellRecord>(map.TryGetGeometryCellAtCell(0, 0, 0));
        Assert.True(floorRecord.Definition.IsFloor);
        Assert.Equal(GeometryShapeKind.SolidCube, floorRecord.Definition.ShapeKind);
        Assert.Equal(GeometryMaterialId.Grass, floorRecord.Definition.MaterialId);
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
        Assert.Contains(export.CompatibilityCubes, cube => cube.Position == new Vector3(3.5f, 1.5f, 3.5f));
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
        Assert.Equal(108, export.Blocks.Count(block => block.CellKey == new GeometryCellKey(3, 1, 3)));
        Assert.Contains(export.Blocks, block => block.SubcellKey == new GeometrySubcellKey(0, 0, 0));
        Assert.DoesNotContain(export.Blocks, block => block.SubcellKey == new GeometrySubcellKey(4, 4, 4));
        Assert.Equal(108, collision.Bodies.Count(body => body.CellKey == new GeometryCellKey(3, 1, 3)));
    }
}
