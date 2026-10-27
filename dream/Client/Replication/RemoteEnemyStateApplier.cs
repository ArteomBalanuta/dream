using OpenTK.Mathematics;
using Dream.Simulation.Combat;
using Dream.Simulation.Enemies;
using Dream.Simulation.World;

namespace Dream.Client.Replication;


public static class RemoteEnemyStateApplier
{
    // Reused for every snapshot on the thread that applies it. The registry reads the set only
    // inside RemoveAllExcept and keeps no reference to it.
    [ThreadStatic]
    private static HashSet<int>? t_activeEnemyActorIds;

    public static void ApplyActorSnapshots(EnemyRegistry registry, IReadOnlyList<ActorSnapshot> actors)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(actors);

        HashSet<int> activeEnemyActorIds = t_activeEnemyActorIds ??= [];
        activeEnemyActorIds.Clear();
        for (int i = 0; i < actors.Count; i++)
        {
            ActorSnapshot actor = actors[i];
            if (actor.Kind != CreatureKind.Enemy || actor.CurrentHealth <= 0 || !actor.IsAlive)
            {
                continue;
            }

            activeEnemyActorIds.Add(actor.ActorId);
            EnemyBehaviorState behaviorState = actor.EnemyBehaviorState ?? EnemyBehaviorState.Idle;
            if (registry.TryGetEnemyByActorId(actor.ActorId, out EnemyActor? existing) && existing != null)
            {
                existing.Combatant.Health.SetAuthoritativeState(actor.CurrentHealth, actor.MaxHealth);
                existing.SetAuthoritativeState(actor.Position, actor.Facing, behaviorState);
                continue;
            }

            registry.Add(CreateEnemy(actor, behaviorState));
        }

        registry.RemoveAllExcept(activeEnemyActorIds);
    }

    // Kept out of the loop so the position lambda's closure is allocated only when an enemy is
    // created, not on every iteration.
    private static EnemyActor CreateEnemy(ActorSnapshot actor, EnemyBehaviorState behaviorState)
    {
        Vector3 spawnPosition = actor.Position;
        EnemyCombatant combatant = new(
            () => spawnPosition,
            actorId: actor.ActorId,
            maxHealth: actor.MaxHealth,
            attackDamage: 10,
            attackIntervalSeconds: 1f);
        combatant.Health.SetAuthoritativeState(actor.CurrentHealth, actor.MaxHealth);

        EnemyActor enemy = new(
            combatant,
            actor.Position,
            aggroRange: 10f,
            attackRange: 1.35f,
            moveSpeed: 2.8f,
            preferredStandOffDistance: WolfDefinition.PreferredStandOffDistance,
            collisionRadius: WolfDefinition.CollisionRadius,
            collisionHeight: WolfDefinition.CollisionHeight,
            displayName: string.IsNullOrWhiteSpace(actor.DisplayName) ? "Wolf" : actor.DisplayName);
        enemy.SetAuthoritativeState(actor.Position, actor.Facing, behaviorState);
        return enemy;
    }
}
