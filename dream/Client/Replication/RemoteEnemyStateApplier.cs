using OpenTK.Mathematics;
using Dream.Simulation.Combat;
using Dream.Simulation.Content;
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

    // The client never attacks for an enemy; these were always placeholders here, not the server's
    // 12 and 1.05 s, and stay so to keep client behaviour identical.
    private const int ClientAttackDamagePlaceholder = 10;
    private const float ClientAttackIntervalPlaceholderSeconds = 1f;

    // Kept out of the loop so the position lambda's closure is allocated only when an enemy is
    // created, not on every iteration.
    private static EnemyActor CreateEnemy(ActorSnapshot actor, EnemyBehaviorState behaviorState)
    {
        Vector3 spawnPosition = actor.Position;
        // The snapshot carries no enemy kind yet, so every enemy is drawn from the wolf template.
        EnemyTemplate template = ContentCatalog.Shipped.Wolf;
        EnemyCombatant combatant = new(
            () => spawnPosition,
            actorId: actor.ActorId,
            maxHealth: actor.MaxHealth,
            attackDamage: ClientAttackDamagePlaceholder,
            attackIntervalSeconds: ClientAttackIntervalPlaceholderSeconds);
        combatant.Health.SetAuthoritativeState(actor.CurrentHealth, actor.MaxHealth);

        EnemyActor enemy = new(
            combatant,
            actor.Position,
            aggroRange: template.AggroRange,
            attackRange: template.AttackRange,
            moveSpeed: template.MoveSpeed,
            preferredStandOffDistance: WolfDefinition.PreferredStandOffDistance,
            collisionRadius: WolfDefinition.CollisionRadius,
            collisionHeight: WolfDefinition.CollisionHeight,
            displayName: string.IsNullOrWhiteSpace(actor.DisplayName) ? template.DisplayName : actor.DisplayName);
        enemy.SetAuthoritativeState(actor.Position, actor.Facing, behaviorState);
        return enemy;
    }
}
