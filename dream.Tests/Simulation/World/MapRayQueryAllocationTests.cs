using Dream.Simulation.World;
using Dream.Tests.Utilities;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Simulation.World;

// The click-to-attack wall test runs Map.TryGetFirstSolidCubeAlongRay every 64 Hz tick, so it must not
// allocate (the old GetNearby was a yield iterator).
public class MapRayQueryAllocationTests
{
    private static Map BuildMap()
    {
        Map map = new(width: 40, height: 8, depth: 40);
        map.AddCube(new Vector3(3f, 1f, 3f), withPhysics: false);
        map.AddCube(new Vector3(20f, 1f, 3f), withPhysics: false);
        map.AddCube(new Vector3(17f, 2f, 18f), withPhysics: false);
        return map;
    }

    [Fact]
    public void TryGetFirstSolidCubeAlongRay_AllocatesNothing()
    {
        Map map = BuildMap();
        Vector3 origin = new(3.5f, 1.5f, 8.5f);
        Assert.NotNull(map.TryGetFirstSolidCubeAlongRay(origin, -Vector3.UnitZ, 8f));

        long bytes = AllocationAssert.AtMost(0, () => map.TryGetFirstSolidCubeAlongRay(origin, -Vector3.UnitZ, 8f));

        Assert.Equal(0, bytes);
    }

    [Fact]
    public void EnumerateNearby_MatchesGetNearby_InCubesAndOrder()
    {
        Map map = BuildMap();
        WorldSpatialIndex index = new();
        int added = 0;
        foreach (Cube cube in map.SolidCubes)
        {
            if (cube.Position.Y > 1f)
            {
                index.Add(cube);
                added++;
            }
        }

        Assert.True(added > 0);
        (Vector3 At, float Radius)[] queries =
        [
            (new Vector3(3.5f, 1.5f, 3.5f), 1f),
            (new Vector3(18f, 2f, 18f), 20f),
            (new Vector3(-30f, 0f, -30f), 5f),
            (new Vector3(20f, 1f, 3f), 40f),
        ];
        foreach ((Vector3 at, float radius) in queries)
        {
            List<Cube> viaEnumerable = [.. index.GetNearby(at, radius)];
            List<Cube> viaStruct = [];
            foreach (Cube cube in index.EnumerateNearby(at, radius))
            {
                viaStruct.Add(cube);
            }

            Assert.Equal(viaEnumerable, viaStruct);
        }
    }

    [Fact]
    public void TryGetFirstSolidCubeAlongRay_StillFindsTheNearestCube_AndMissesWhenBlockedAway()
    {
        Map map = BuildMap();

        Cube? hit = map.TryGetFirstSolidCubeAlongRay(new Vector3(3.5f, 1.5f, 8.5f), -Vector3.UnitZ, 8f);
        Cube? miss = map.TryGetFirstSolidCubeAlongRay(new Vector3(10.5f, 1.5f, 8.5f), -Vector3.UnitZ, 8f);
        Cube? far = map.TryGetFirstSolidCubeAlongRay(new Vector3(17.5f, 2.5f, 25.5f), -Vector3.UnitZ, 10f);

        Assert.Equal(new Vector3(3.5f, 1.5f, 3.5f), hit!.Position);
        Assert.Null(miss);
        Assert.Equal(new Vector3(17.5f, 2.5f, 18.5f), far!.Position);
    }
}
