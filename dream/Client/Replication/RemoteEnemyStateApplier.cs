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

    // The client never attacks for an enemy; these were always placeholders here, not the server's
    // 12 and 1.05 s, and stay so to keep client behaviour identical.
    private const int ClientAttackDamagePlaceholder = 10;
    private const float ClientAttackIntervalPlaceholderSeconds = 1f;

    // The client never reads the content catalog. These ranges and this speed only feed an actor the
    // client never simulates, and stay the wolf's shipped values so nothing about it changes.
    private const float ClientAggroRangePlaceholder = 10f;
    private const float ClientAttackRangePlaceholder = 1.35f;
    private const float ClientMoveSpeedPlaceholder = 2.8f;
    private const string ClientDisplayNameFallback = "Wolf";

    // Wire levels are hostile; 0 (absent) is level 1.
    public const int MaxDisplayLevel = 9999;

    // A longer visual key is not any known key; it draws the default wolf and is not kept.
    public const int MaxVisualKeyLength = 64;

    // Kept out of the loop so the position lambda's closure is allocated only when an enemy is
    // created, not on every iteration.
    private static EnemyActor CreateEnemy(ActorSnapshot actor, EnemyBehaviorState behaviorState)
    {
        Vector3 spawnPosition = actor.Position;
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
            aggroRange: ClientAggroRangePlaceholder,
            attackRange: ClientAttackRangePlaceholder,
            moveSpeed: ClientMoveSpeedPlaceholder,
            preferredStandOffDistance: WolfDefinition.PreferredStandOffDistance,
            collisionRadius: WolfDefinition.CollisionRadius,
            collisionHeight: WolfDefinition.CollisionHeight,
            displayName: string.IsNullOrWhiteSpace(actor.DisplayName) ? ClientDisplayNameFallback : actor.DisplayName,
            level: Math.Clamp(actor.EnemyLevel, 1, MaxDisplayLevel),
            visualKey: actor.VisualKey is { Length: <= MaxVisualKeyLength } ? actor.VisualKey : null);
        enemy.SetAuthoritativeState(actor.Position, actor.Facing, behaviorState);
        return enemy;
    }
}
