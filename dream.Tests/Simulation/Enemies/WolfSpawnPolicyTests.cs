using OpenTK.Mathematics;
using Xunit;
using Dream.Simulation.Content;
using Dream.Simulation.Enemies;

namespace Dream.Tests.Simulation.Enemies;


public class WolfSpawnPolicyTests
{
    // The world spawn the clients and the host use.
    private static readonly Vector2 WorldSpawn = new(5f, 5f);

    [Fact]
    public void ResolveInitialSpawn_Uses_The_Far_Corner_Of_The_Map()
    {
        Vector3 spawn = WolfSpawnPolicy.ResolveInitialSpawn(width: 25, depth: 25);

        Assert.Equal(new Vector3(21.5f, 1f, 21.5f), spawn);
    }

    [Fact]
    public void ResolveInitialSpawn_Clamps_To_Stay_Inside_Small_Map_Bounds()
    {
        Vector3 spawn = WolfSpawnPolicy.ResolveInitialSpawn(width: 5, depth: 5);

        Assert.Equal(new Vector3(1.5f, 1f, 1.5f), spawn);
    }

    [Theory]
    [InlineData(30)]
    [InlineData(25)]
    [InlineData(20)]
    [InlineData(100)]
    public void TheDayAndNightSpawns_AreOutsideEveryTemplatesAggroRange_OfTheWorldSpawn_AndInsideTheMap(int size)
    {
        float maxAggro = ContentCatalog.Shipped.Enemies.Max(template => template.AggroRange);
        foreach (Vector3 spawn in new[] { WolfSpawnPolicy.ResolveInitialSpawn(size, size), WolfSpawnPolicy.ResolveNightSpawn(size, size) })
        {
            Assert.True((spawn.Xz - WorldSpawn).Length > maxAggro + 1f, $"{size}: {spawn}");
            Assert.InRange(spawn.X, 0.5f, size - 1.5f);
            Assert.InRange(spawn.Z, 0.5f, size - 1.5f);
        }
    }
}
