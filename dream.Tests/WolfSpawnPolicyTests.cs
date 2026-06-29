using OpenTK.Mathematics;
using Xunit;

public class WolfSpawnPolicyTests
{
    [Fact]
    public void ResolveInitialSpawn_Uses_Known_Default_Map_Position_When_Available()
    {
        Vector3 spawn = WolfSpawnPolicy.ResolveInitialSpawn(width: 25, depth: 25);

        Assert.Equal(new Vector3(5.5f, 1f, 2.5f), spawn);
    }

    [Fact]
    public void ResolveInitialSpawn_Clamps_To_Stay_Inside_Small_Map_Bounds()
    {
        Vector3 spawn = WolfSpawnPolicy.ResolveInitialSpawn(width: 5, depth: 5);

        Assert.Equal(new Vector3(3.5f, 1f, 2.5f), spawn);
    }
}
