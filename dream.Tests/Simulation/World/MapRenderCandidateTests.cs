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
    public void CaptureGeometryRenderExport_Uses_Authoritative_Cell_For_Moved_Placed_Cube()
    {
        var map = new Map(physics: null, loadVisualAssets: false, width: 20, depth: 20);
        map.AddCube(new Vector3(3f, 1f, 3f), withPhysics: false);
        Cube cube = Assert.IsType<Cube>(map.Cubes[3, 1, 3]);
        map.UpdateCubePosition(cube, new Vector3(4.1f, 1.5f, 3.5f));

        GeometryRenderExport export = map.CaptureGeometryRenderExport(new Vector3(4.1f, 1.5f, 3.5f), 8f);

        GeometryRenderCell renderCell = Assert.Single(export.Cells, cell => ReferenceEquals(cell.CompatibilityCube, cube));
        Assert.Equal(new GeometryCellKey(3, 1, 3), renderCell.Key);
        Assert.Same(cube, renderCell.CompatibilityCube);
        Assert.Contains(cube, export.CompatibilityCubes);
    }
}
