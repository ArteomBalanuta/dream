using OpenTK.Mathematics;
using Xunit;

public class CombatUseResolverTests
{
    [Fact]
    public void TryResolveDestructibleCubeHit_Returns_Null_When_Ray_Hits_Nothing()
    {
        Map map = new(width: 8, height: 8, depth: 8, loadVisualAssets: false);
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
        Map map = new(width: 12, height: 8, depth: 12, loadVisualAssets: false);
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
}
