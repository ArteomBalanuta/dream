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

    private static EnemyActor CreateEnemy(Vector3 position, int actorId)
    {
        EnemyCombatant combatant = new(() => position, actorId: actorId, maxHealth: 30, attackDamage: 6, attackIntervalSeconds: 1f);
        return new EnemyActor(combatant, position, aggroRange: 5f, attackRange: 1.25f, moveSpeed: 3f);
    }
}
