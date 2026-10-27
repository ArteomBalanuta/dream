using OpenTK.Mathematics;
using Xunit;
using Dream.Simulation.Combat;
using Dream.Simulation.Enemies;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;

namespace Dream.Tests.Simulation.Combat;


public class CombatUseResolverTests
{
    [Fact]
    public void TryResolveDestructibleCubeHit_Returns_Null_When_Ray_Hits_Nothing()
    {
        Map map = new(width: 8, height: 8, depth: 8);
        WorldObjectRegistry registry = new();

        WorldObjectRecord? record = CombatUseResolver.TryResolveDestructibleCubeHit(
            map,
            registry,
            new Vector3(3.5f, 1.5f, 5.5f),
            Vector3.UnitX,
            CombatUseResolver.DefaultHitRange);

        Assert.Null(record);
    }

    [Fact]
    public void TryResolveDestructibleCubeHit_Respects_Max_Hit_Distance()
    {
        Map map = new(width: 12, height: 8, depth: 12);
        map.AddCube(new Vector3(3f, 1f, 3f), withPhysics: false);
        Cube cube = Assert.IsType<Cube>(map.Cubes[3, 1, 3]);

        WorldObjectRegistry registry = new();
        registry.RegisterCube(cube, map);

        WorldObjectRecord? record = CombatUseResolver.TryResolveDestructibleCubeHit(
            map,
            registry,
            new Vector3(3.5f, 1.5f, 8.5f),
            -Vector3.UnitZ,
            maxHitDistance: 3f);

        Assert.Null(record);
    }

    [Fact]
    public void TryResolveEnemyHit_Returns_Enemy_Along_Ray()
    {
        EnemyRegistry enemies = new();
        EnemyCombatant combatant = new(() => new Vector3(3.5f, 0f, 3.5f), actorId: 2500);
        EnemyActor actor = new(combatant, new Vector3(3.5f, 0f, 3.5f), aggroRange: 5f, attackRange: 1f, moveSpeed: 2f);
        enemies.Add(actor);

        EnemyActor? hit = CombatUseResolver.TryResolveEnemyHit(
            enemies,
            new Vector3(3.5f, 1.0f, 6.5f),
            -Vector3.UnitZ,
            maxHitDistance: 4f);

        Assert.Same(actor, hit);
    }

    [Fact]
    public void TryResolveDestructibleCubeHit_Rebinds_Visible_Moved_Cube_When_Registry_Identity_Drifts()
    {
        Map map = new(width: 12, height: 8, depth: 12);
        map.AddCube(new Vector3(3f, 1f, 3f), withPhysics: false);
        Cube registeredCube = Assert.IsType<Cube>(map.Cubes[3, 1, 3]);

        WorldObjectRegistry registry = new();
        WorldObjectRecord originalRecord = registry.RegisterCube(
            registeredCube,
            map,
            maxHealth: 50,
            currentHealth: 30,
            actorId: 3210,
            ownerPlayerName: "Player A");

        map.UpdateCubePosition(registeredCube, new Vector3(4.2f, 1.5f, 3.5f), syncPhysics: false);
        Assert.True(map.RemoveCube(registeredCube));

        Cube replacementCube = Assert.IsType<Cube>(map.TryAddCube(new Vector3(4f, 1f, 3f), withPhysics: false));

        WorldObjectRecord? resolved = CombatUseResolver.TryResolveDestructibleCubeHit(
            map,
            registry,
            new Vector3(4.5f, 1.5f, 6.5f),
            -Vector3.UnitZ,
            maxHitDistance: 4f);

        WorldObjectRecord rebound = Assert.IsType<WorldObjectRecord>(resolved);
        Assert.NotSame(originalRecord.SourceObject, rebound.SourceObject);
        Assert.Same(replacementCube, rebound.SourceObject);
        Assert.Equal(3210, rebound.Actor.Id);
        Assert.Equal(30, rebound.Health.CurrentHealth);
        Assert.Equal(50, rebound.Health.MaxHealth);
        Assert.Equal("Player A", rebound.OwnerPlayerName);
        Assert.Null(registry.TryGet(registeredCube));
        Assert.Same(rebound, registry.TryGet(replacementCube));
    }

    [Fact]
    public void TryResolveDestructibleCubeHit_Skips_Fractured_Parent_Cell()
    {
        Map map = new(width: 12, height: 8, depth: 12);
        map.AddCube(new Vector3(3f, 1f, 3f), withPhysics: false);
        map.AddCube(new Vector3(3f, 1f, 2f), withPhysics: false);
        Assert.True(map.FractureCubeIntoPieces(new GeometryCellKey(3, 1, 3)));
        Cube solidCubeBehindRubble = Assert.IsType<Cube>(map.TryGetCubeAtCell(3, 1, 2));

        WorldObjectRegistry registry = new();
        WorldObjectRecord expectedRecord = registry.RegisterCube(
            solidCubeBehindRubble,
            map,
            actorId: 4444);

        WorldObjectRecord? resolved = CombatUseResolver.TryResolveDestructibleCubeHit(
            map,
            registry,
            new Vector3(3.5f, 1.5f, 6.5f),
            -Vector3.UnitZ,
            maxHitDistance: 6f);

        Assert.Same(expectedRecord, resolved);
    }
}
