using OpenTK.Mathematics;
using Xunit;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;

namespace Dream.Tests.Simulation.World;


public class MapRenderCandidateTests
{
    [Fact]
    public void GetRenderCandidateCubes_Returns_Local_Floor_And_Nearby_Placed_Cubes()
    {
        var map = new Map(physics: null, width: 100, depth: 100);
        map.AddCube(new Vector3(10, 1, 10), withPhysics: false);
        map.AddCube(new Vector3(80, 1, 80), withPhysics: false);

        List<Cube> candidates = map.GetRenderCandidateCubes(new Vector3(8f, 1.5f, 8f), 20f).ToList();

        Assert.Contains(candidates, cube => cube.Position == new Vector3(0.5f, 0.5f, 0.5f));
        Assert.Contains(candidates, cube => cube.Position == new Vector3(10.5f, 1.5f, 10.5f));
        Assert.DoesNotContain(candidates, cube => cube.Position == new Vector3(80.5f, 1.5f, 80.5f));
        Assert.DoesNotContain(candidates, cube => cube.Position == new Vector3(99.5f, 0.5f, 99.5f));
    }

    [Fact]
    public void GetRenderCandidateCubes_Does_Not_Duplicate_Placed_Cube_On_Floor_Level()
    {
        var map = new Map(physics: null, width: 20, depth: 20);

        List<Cube> candidates = map.GetRenderCandidateCubes(new Vector3(2f, 1.5f, 2f), 10f).ToList();
        int matchingFloorTiles = candidates.Count(cube => cube.Position == new Vector3(2.5f, 0.5f, 2.5f));

        Assert.Equal(1, matchingFloorTiles);
    }

    [Fact]
    public void CaptureGeometryRenderExport_Dirties_Old_And_New_Cells_When_Placed_Cube_Crosses_Cell_Boundary()
    {
        var map = new Map(physics: null, width: 20, depth: 20);
        map.AddCube(new Vector3(3f, 1f, 3f), withPhysics: false);
        Cube cube = Assert.IsType<Cube>(map.Cubes[3, 1, 3]);
        map.UpdateCubePosition(cube, new Vector3(4.1f, 1.5f, 3.5f));

        GeometryRenderExport export = map.CaptureGeometryRenderExport(new Vector3(4.1f, 1.5f, 3.5f), 8f);

        GeometryRenderCell renderCell = Assert.Single(export.Cells, cell => ReferenceEquals(cell.CompatibilityCube, cube));
        Assert.Equal(new GeometryCellKey(4, 1, 3), renderCell.Key);
        Assert.Same(cube, renderCell.CompatibilityCube);
        Assert.Contains(cube, export.DynamicCompatibilityCubes);
        Assert.DoesNotContain(cube, export.CompatibilityCubes);
        Assert.Null(map.TryGetCubeAtCell(3, 1, 3));
        Assert.Same(cube, map.TryGetCubeAtCell(4, 1, 3));
        Assert.Contains(new GeometryCellKey(3, 1, 3), export.DirtyCells);
        Assert.Contains(new GeometryCellKey(4, 1, 3), export.DirtyCells);
    }

    [Fact]
    public void UpdateCubePosition_RejectedOccupiedTarget_LeavesCubeAndSpatialIndexAtAcceptedPosition()
    {
        var map = new Map(physics: null, width: 20, depth: 20);
        map.AddCube(new Vector3(3f, 1f, 3f), withPhysics: false);
        map.AddCube(new Vector3(4f, 1f, 3f), withPhysics: false);
        Cube movingCube = Assert.IsType<Cube>(map.Cubes[3, 1, 3]);
        Vector3 acceptedCenter = movingCube.Position;

        map.UpdateCubePosition(movingCube, new Vector3(4.2f, 1.5f, 3.5f), syncPhysics: false);

        Assert.Equal(acceptedCenter, movingCube.Position);
        Assert.Same(movingCube, map.TryGetCubeAtCell(3, 1, 3));
        Assert.NotSame(movingCube, map.TryGetCubeAtCell(4, 1, 3));
        GeometryRenderExport oldAreaExport = map.CaptureGeometryRenderExport(acceptedCenter, 1f);
        Assert.Contains(movingCube, oldAreaExport.DynamicCompatibilityCubes);
    }

    [Fact]
    public void CaptureGeometryRenderExport_Splits_Static_And_Dynamic_Compatibility_Cubes()
    {
        var map = new Map(physics: null, width: 20, depth: 20);
        map.AddCube(new Vector3(5f, 1f, 5f), isStatic: true, withPhysics: false);
        map.AddCube(new Vector3(6f, 1f, 6f), isStatic: false, withPhysics: false);
        Cube staticCube = Assert.IsType<Cube>(map.Cubes[5, 1, 5]);
        Cube dynamicCube = Assert.IsType<Cube>(map.Cubes[6, 1, 6]);

        GeometryRenderExport export = map.CaptureGeometryRenderExport(new Vector3(6f, 1.5f, 6f), 10f);

        Assert.Contains(staticCube, export.CompatibilityCubes);
        Assert.DoesNotContain(staticCube, export.DynamicCompatibilityCubes);
        Assert.Contains(dynamicCube, export.DynamicCompatibilityCubes);
        Assert.DoesNotContain(dynamicCube, export.CompatibilityCubes);
    }

    [Fact]
    public void CaptureGeometryRenderExport_Keeps_Floor_Cell_When_Radius_Intersects_Its_Bounds()
    {
        var map = new Map(physics: null, width: 20, depth: 20);

        GeometryRenderExport export = map.CaptureGeometryRenderExport(new Vector3(1.6f, 1.5f, 2.5f), 0.65f);

        Assert.Contains(export.Cells, cell => cell.Key == new GeometryCellKey(2, 0, 2));
        Assert.Contains(export.CompatibilityCubes, cube => cube.Position == new Vector3(2.5f, 0.5f, 2.5f));
    }

    [Fact]
    public void CaptureFullGeometryRenderExport_Includes_Far_Cells_That_Radius_Export_Excludes()
    {
        var map = new Map(physics: null, width: 20, depth: 20);

        GeometryRenderExport localExport = map.CaptureGeometryRenderExport(new Vector3(1.5f, 1.5f, 1.5f), 2f);
        GeometryRenderExport fullExport = map.CaptureFullGeometryRenderExport();

        Assert.DoesNotContain(localExport.Cells, cell => cell.Key == new GeometryCellKey(19, 0, 19));
        Assert.Contains(fullExport.Cells, cell => cell.Key == new GeometryCellKey(19, 0, 19));
        Assert.Contains(fullExport.CompatibilityCubes, cube => cube.Position == new Vector3(19.5f, 0.5f, 19.5f));
    }

    [Fact]
    public void CaptureFullGeometryRenderExport_Is_Stable_Across_Camera_Positions()
    {
        var map = new Map(physics: null, width: 20, depth: 20);
        map.AddCube(new Vector3(10f, 1f, 10f), isStatic: true, withPhysics: false);

        GeometryRenderExport nearExport = map.CaptureFullGeometryRenderExport();
        GeometryRenderExport farExport = map.CaptureFullGeometryRenderExport();

        Assert.Equal(
            nearExport.Cells.Select(cell => cell.Key).OrderBy(key => key.Y).ThenBy(key => key.Z).ThenBy(key => key.X),
            farExport.Cells.Select(cell => cell.Key).OrderBy(key => key.Y).ThenBy(key => key.Z).ThenBy(key => key.X));
        Assert.Equal(nearExport.CompatibilityCubes.Count, farExport.CompatibilityCubes.Count);
        Assert.Equal(nearExport.Blocks.Count, farExport.Blocks.Count);
    }
}
