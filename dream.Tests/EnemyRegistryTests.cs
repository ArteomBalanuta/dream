using OpenTK.Mathematics;
using Xunit;

public class EnemyRegistryTests
{
    [Fact]
    public void TryResolveHit_Returns_Closest_Enemy_Along_Ray()
    {
        EnemyRegistry registry = new();
        EnemyActor far = CreateEnemy(new Vector3(0f, 0f, -4f), actorId: 2100);
        EnemyActor near = CreateEnemy(new Vector3(0f, 0f, -2f), actorId: 2101);
        registry.Add(far);
        registry.Add(near);

        EnemyActor? hit = registry.TryResolveHit(Vector3.Zero, -Vector3.UnitZ, maxHitDistance: 5f);

        Assert.Same(near, hit);
    }

    [Fact]
    public void PruneInactive_Removes_Dead_Enemies()
    {
        EnemyRegistry registry = new();
        EnemyActor actor = CreateEnemy(new Vector3(0f, 0f, -2f), actorId: 2102);
        registry.Add(actor);
        actor.Combatant.ApplyDamage(999);

        registry.PruneInactive();

        Assert.Empty(registry.ActiveActors);
    }

    [Fact]
    public void UpdateAll_Blocks_Chase_Movement_Through_Placed_Cubes_When_Map_Is_Provided()
    {
        Map map = new(width: 5, height: 4, depth: 4, loadVisualAssets: false);
        map.AddCube(new Vector3(1f, 1f, 0f), withPhysics: false);
        EnemyRegistry registry = new();
        EnemyActor actor = new(
            new EnemyCombatant(() => new Vector3(0.5f, 1f, 0.5f), actorId: 2103),
            new Vector3(0.5f, 1f, 0.5f),
            aggroRange: 5f,
            attackRange: 0.5f,
            moveSpeed: 4f,
            collisionRadius: 0.4f,
            collisionHeight: 1.2f);
        registry.Add(actor);

        registry.UpdateAll(
            deltaTime: 0.5f,
            playerPosition: new Vector3(3.5f, 1f, 0.5f),
            playerCombatant: new PlayerCombatant(() => new Vector3(3.5f, 1f, 0.5f)),
            events: new SceneEventHub(),
            map: map);

        Assert.True(actor.Position.X < 1f);
        Assert.Equal(1f, actor.Position.Y, 3);
        Assert.Equal(0.5f, actor.Position.Z, 3);
    }

    private static EnemyActor CreateEnemy(Vector3 position, int actorId)
    {
        EnemyCombatant combatant = new(() => position, actorId: actorId, maxHealth: 30, attackDamage: 6, attackIntervalSeconds: 1f);
        return new EnemyActor(combatant, position, aggroRange: 5f, attackRange: 1.25f, moveSpeed: 3f);
    }
}
