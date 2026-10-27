using OpenTK.Mathematics;
using Xunit;
using Dream.Simulation.Enemies;
using Dream.Simulation.World;

namespace Dream.Tests.Simulation.Enemies;


public class EnemyMovementCollisionTests
{
    [Fact]
    public void CanOccupy_Allows_Enemy_To_Stand_On_Base_Floor()
    {
        Map map = new(width: 4, height: 4, depth: 4);

        bool canOccupy = EnemyMovementCollision.CanOccupy(
            map,
            new Vector3(0.5f, 1f, 0.5f),
            radius: 0.4f,
            height: 1.2f);

        Assert.True(canOccupy);
    }

    [Fact]
    public void CanOccupy_Blocks_Position_Overlapping_Placed_Cube()
    {
        Map map = new(width: 4, height: 4, depth: 4);
        map.AddCube(new Vector3(1f, 1f, 0f), withPhysics: false);

        bool canOccupy = EnemyMovementCollision.CanOccupy(
            map,
            new Vector3(1.5f, 1f, 0.5f),
            radius: 0.4f,
            height: 1.2f);

        Assert.False(canOccupy);
    }

    [Fact]
    public void ResolveStep_Stops_Before_Entering_Placed_Cube()
    {
        Map map = new(width: 5, height: 4, depth: 4);
        map.AddCube(new Vector3(1f, 1f, 0f), withPhysics: false);

        Vector3 resolved = EnemyMovementCollision.ResolveStep(
            map,
            from: new Vector3(0.5f, 1f, 0.5f),
            desiredDelta: new Vector3(2f, 0f, 0f),
            radius: 0.4f,
            height: 1.2f);

        Assert.True(resolved.X < 0.5f);
        Assert.Equal(0f, resolved.Y, 3);
        Assert.Equal(0f, resolved.Z, 3);
    }
}
