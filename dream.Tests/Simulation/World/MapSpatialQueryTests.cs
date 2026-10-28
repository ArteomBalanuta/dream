using OpenTK.Mathematics;
using Xunit;
using Dream.Simulation.World;

namespace Dream.Tests.Simulation.World;


public class MapSpatialQueryTests
{
    [Fact]
    public void GetRenderCandidateCubes_Returns_Local_Floor_Sector_Without_Far_Floor()
    {
        var map = new Map(physics: null, width: 100, depth: 100);

        List<Cube> nearby = map.GetRenderCandidateCubes(new Vector3(5f, 1.5f, 5f), 20f).ToList();

        Assert.Contains(nearby, cube => cube.Position == new Vector3(0.5f, 0.5f, 0.5f));
        Assert.DoesNotContain(nearby, cube => cube.Position == new Vector3(99.5f, 0.5f, 99.5f));
    }

    [Fact]
    public void GetRenderCandidateCubes_Includes_Added_Cubes_In_Relevant_Sector()
    {
        var map = new Map(physics: null, width: 100, depth: 100);
        Vector3 placedCell = new Vector3(18f, 2f, 18f);

        map.AddCube(placedCell, withPhysics: false);

        List<Cube> nearby = map.GetRenderCandidateCubes(new Vector3(16f, 1.5f, 16f), 20f).ToList();

        Assert.Contains(nearby, cube => cube.Position == new Vector3(18.5f, 2.5f, 18.5f));
    }

    [Fact]
    public void GetRenderCandidateCubes_Covers_Multiple_Sectors_When_Radius_Spans_Boundary()
    {
        var map = new Map(physics: null, width: 100, depth: 100);

        List<Cube> nearby = map.GetRenderCandidateCubes(new Vector3(15f, 1.5f, 15f), 20f).ToList();

        Assert.Contains(nearby, cube => cube.Position == new Vector3(8.5f, 0.5f, 8.5f));
        Assert.Contains(nearby, cube => cube.Position == new Vector3(24.5f, 0.5f, 24.5f));
    }

    [Fact]
    public void GetRenderCandidateCubes_Finds_Moved_Placed_Cube_After_Sector_Crossing()
    {
        var map = new Map(physics: null, width: 100, depth: 100);
        map.AddCube(new Vector3(15f, 1f, 15f), withPhysics: false);
        Cube cube = Assert.IsType<Cube>(map.Cubes[15, 1, 15]);
        map.UpdateCubePosition(cube, new Vector3(40.5f, 1.5f, 40.5f));

        List<Cube> nearby = map.GetRenderCandidateCubes(new Vector3(40.5f, 1.5f, 40.5f), 5f).ToList();

        Assert.Contains(cube, nearby);
    }
}
