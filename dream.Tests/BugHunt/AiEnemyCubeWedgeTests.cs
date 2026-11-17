using OpenTK.Mathematics;
using Dream.Simulation.Enemies;
using Dream.Simulation.World;
using Xunit;

namespace Dream.Tests.BugHunt;

// BH-AI-2: a cube built on the cell an enemy stands in (Map.TryAddCube refuses only an occupied cell,
// never an occupied actor) left the movement sweep with no free candidate: it broke on the first 0.1 m
// step and returned Vector3.Zero every tick after, so the enemy was wedged for good while still holding
// its camp slot. An enemy that already overlaps geometry may now take the step it asked for and walk out.
public sealed class AiEnemyCubeWedgeTests
{
    private const float Radius = WolfDefinition.CollisionRadius;
    private const float Height = WolfDefinition.CollisionHeight;

    private static Map NewMap() =>
        new(physics: null, generateStartupTerrain: false, width: 12, height: 8, depth: 12, headless: true);

    [Fact]
    public void AnEnemyWithACubeBuiltOnItsOwnCell_CanStillStepOut()
    {
        Map map = NewMap();
        Vector3 from = new(5.5f, 1f, 5.5f);
        Assert.True(EnemyMovementCollision.CanOccupy(map, from, Radius, Height), "the cell starts free");
        Assert.NotNull(map.TryAddCube(new Vector3(5.5f, 1f, 5.5f), isStatic: true, withPhysics: false));
        Assert.False(EnemyMovementCollision.CanOccupy(map, from, Radius, Height), "the enemy is now inside the cube");

        Vector3 desired = new(0f, 0f, 1.2f);
        Vector3 resolved = EnemyMovementCollision.ResolveStep(map, from, desired, Radius, Height);

        Assert.Equal(desired, resolved);
        Assert.True(
            EnemyMovementCollision.CanOccupy(map, from + resolved, Radius, Height),
            "one step along its own path puts the enemy back outside the cube");
    }

    // The sweep itself is unchanged for an enemy that starts in free space: it still stops at the wall.
    [Fact]
    public void AnEnemyInFreeSpace_StillStopsAtAWall()
    {
        Map map = NewMap();
        Vector3 from = new(5.5f, 1f, 5.5f);
        Assert.NotNull(map.TryAddCube(new Vector3(5.5f, 1f, 6.5f), isStatic: true, withPhysics: false));

        Vector3 resolved = EnemyMovementCollision.ResolveStep(map, from, new Vector3(0f, 0f, 1.5f), Radius, Height);

        Assert.True(resolved.Z < 1.5f, $"the wall must stop the sweep, got {resolved.Z}");
        Assert.True(EnemyMovementCollision.CanOccupy(map, from + resolved, Radius, Height));
    }

    [Fact]
    public void AnEnemyInFreeSpace_WithNoObstacle_TakesTheWholeStep()
    {
        Map map = NewMap();
        Vector3 from = new(5.5f, 1f, 5.5f);

        Vector3 resolved = EnemyMovementCollision.ResolveStep(map, from, new Vector3(0f, 0f, 0.8f), Radius, Height);

        Assert.True((resolved - new Vector3(0f, 0f, 0.8f)).Length < 0.11f, resolved.ToString());
    }
}
