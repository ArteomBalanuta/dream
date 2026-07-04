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
        var map = new Map(physics: null, loadVisualAssets: false, width: 100, depth: 100);
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
        var map = new Map(physics: null, loadVisualAssets: false, width: 20, depth: 20);

        List<Cube> candidates = map.GetRenderCandidateCubes(new Vector3(2f, 1.5f, 2f), 10f).ToList();
        int matchingFloorTiles = candidates.Count(cube => cube.Position == new Vector3(2.5f, 0.5f, 2.5f));

        Assert.Equal(1, matchingFloorTiles);
    }

    [Fact]
    public void CaptureGeometryRenderExport_Dirties_Old_And_New_Cells_When_Placed_Cube_Crosses_Cell_Boundary()
    {
        var map = new Map(physics: null, loadVisualAssets: false, width: 20, depth: 20);
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
    public void CaptureGeometryRenderExport_Splits_Static_And_Dynamic_Compatibility_Cubes()
    {
        var map = new Map(physics: null, loadVisualAssets: false, width: 20, depth: 20);
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
        var map = new Map(physics: null, loadVisualAssets: false, width: 20, depth: 20);

        GeometryRenderExport export = map.CaptureGeometryRenderExport(new Vector3(1.6f, 1.5f, 2.5f), 0.65f);

        Assert.Contains(export.Cells, cell => cell.Key == new GeometryCellKey(2, 0, 2));
        Assert.Contains(export.CompatibilityCubes, cube => cube.Position == new Vector3(2.5f, 0.5f, 2.5f));
    }
}
