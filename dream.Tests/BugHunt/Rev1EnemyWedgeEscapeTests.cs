using OpenTK.Mathematics;
using Dream.Simulation.Enemies;
using Dream.Simulation.World;
using Xunit;

namespace Dream.Tests.BugHunt;

// BH-REV1. The un-wedging escape hatch (BH-AI-2) returned the enemy's asked-for delta, which is the chase
// vector: no direction of its own and no bound, so an enemy whose cell was built over -- or one a restore
// clamped into a wall -- kept taking its full requested step every tick while any part of it was still
// inside, and walked through a one- or two-cell wall into the room beyond. It now resolves OUT: the
// asked-for step is taken only when it ends outside geometry, and otherwise the enemy steps to the nearest
// free spot, preferring the direction away from the step that wedged it.
public sealed class Rev1EnemyWedgeEscapeTests
{
    private const float Radius = WolfDefinition.CollisionRadius;
    private const float Height = WolfDefinition.CollisionHeight;

    private static Map NewMap() =>
        new(physics: null, generateStartupTerrain: false, width: 16, height: 8, depth: 16, headless: true);

    // A wall at z = 8, with the enemy inside it and the player beyond: the chase vector points through the
    // wall. The enemy must come out on the side it was wedged on, never cross to z >= 9.
    [Fact]
    public void AnEnemyWedgedInAWall_ResolvesOutOnItsOwnSide_InsteadOfTraversingIt()
    {
        Map map = NewMap();
        for (int x = 2; x <= 12; x++)
        {
            Assert.NotNull(map.TryAddCube(new Vector3(x + 0.5f, 1f, 8.5f), isStatic: true, withPhysics: false));
        }

        Vector3 position = new(7.5f, 1f, 8.5f);
        Assert.False(EnemyMovementCollision.CanOccupy(map, position, Radius, Height), "the enemy starts inside the wall");

        Vector3 chase = new(0f, 0f, 0.08f); // one tick of chase, straight at a player past the wall
        for (int i = 0; i < 60; i++)
        {
            position += EnemyMovementCollision.ResolveStep(map, position, chase, Radius, Height);
            Assert.True(position.Z < 9f, $"the enemy traversed the wall, z = {position.Z} after {i + 1} ticks");
        }

        Assert.True(
            EnemyMovementCollision.CanOccupy(map, position, Radius, Height),
            $"the enemy is still inside geometry at {position}");
    }

    // And it does not stay wedged: it is out of the geometry within a tick or two, which is the whole point
    // of the escape hatch (a wedged enemy holds its camp slot for ever).
    [Fact]
    public void AnEnemyWedgedInAWall_IsFreeWithinAFewTicks()
    {
        Map map = NewMap();
        for (int x = 2; x <= 12; x++)
        {
            Assert.NotNull(map.TryAddCube(new Vector3(x + 0.5f, 1f, 8.5f), isStatic: true, withPhysics: false));
        }

        Vector3 position = new(7.5f, 1f, 8.5f);
        int ticks = 0;
        while (!EnemyMovementCollision.CanOccupy(map, position, Radius, Height) && ticks < 10)
        {
            position += EnemyMovementCollision.ResolveStep(map, position, new Vector3(0f, 0f, 0.08f), Radius, Height);
            ticks++;
        }

        Assert.True(EnemyMovementCollision.CanOccupy(map, position, Radius, Height), $"still wedged after {ticks} ticks");
    }

    // A cube built on the enemy's own cell with its path out also blocked: the old hatch handed it the
    // desired delta, which here simply moves it from one cube into the next. It must find the free side.
    [Fact]
    public void AnEnemyBuiltOver_WithItsDesiredDirectionAlsoBlocked_StillEndsUpOutsideGeometry()
    {
        Map map = NewMap();
        Vector3 from = new(5.5f, 1f, 5.5f);
        Assert.NotNull(map.TryAddCube(new Vector3(5.5f, 1f, 5.5f), isStatic: true, withPhysics: false));
        Assert.NotNull(map.TryAddCube(new Vector3(5.5f, 1f, 6.5f), isStatic: true, withPhysics: false));
        Assert.NotNull(map.TryAddCube(new Vector3(5.5f, 1f, 7.5f), isStatic: true, withPhysics: false));

        Vector3 resolved = EnemyMovementCollision.ResolveStep(map, from, new Vector3(0f, 0f, 1.2f), Radius, Height);

        Assert.NotEqual(Vector3.Zero, resolved);
        Assert.True(
            EnemyMovementCollision.CanOccupy(map, from + resolved, Radius, Height),
            $"the escape left the enemy inside geometry at {from + resolved}");
        Assert.True((from + resolved).Z < 6f, $"the escape went deeper into the cubes, z = {(from + resolved).Z}");
    }

    // The escape is a function of the map, the position and the asked-for step only: same inputs, same
    // result, every time (lockstep peers resolve identically).
    [Fact]
    public void TheEscape_IsDeterministic()
    {
        Map map = NewMap();
        Vector3 from = new(5.5f, 1f, 5.5f);
        Assert.NotNull(map.TryAddCube(new Vector3(5.5f, 1f, 5.5f), isStatic: true, withPhysics: false));
        Assert.NotNull(map.TryAddCube(new Vector3(5.5f, 1f, 6.5f), isStatic: true, withPhysics: false));
        Vector3 desired = new(0f, 0f, 0.08f);

        Vector3 first = EnemyMovementCollision.ResolveStep(map, from, desired, Radius, Height);
        for (int i = 0; i < 5; i++)
        {
            Assert.Equal(first, EnemyMovementCollision.ResolveStep(map, from, desired, Radius, Height));
        }
    }

    // Control: an enemy that is NOT wedged is untouched by any of this.
    [Fact]
    public void AnEnemyInFreeSpace_StillSweepsNormally()
    {
        Map map = NewMap();
        Vector3 from = new(5.5f, 1f, 5.5f);
        Assert.NotNull(map.TryAddCube(new Vector3(5.5f, 1f, 6.5f), isStatic: true, withPhysics: false));

        Vector3 resolved = EnemyMovementCollision.ResolveStep(map, from, new Vector3(0f, 0f, 1.5f), Radius, Height);

        Assert.True(resolved.Z < 1.5f, $"the wall must stop the sweep, got {resolved.Z}");
        Assert.True(EnemyMovementCollision.CanOccupy(map, from + resolved, Radius, Height));
    }
}
