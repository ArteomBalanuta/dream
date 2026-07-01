using OpenTK.Mathematics;
using Xunit;
using Dream.Simulation.World;

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
}
