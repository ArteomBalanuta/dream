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

    // Reused per snapshot on the same thread, like the set above.
    [ThreadStatic]
    private static HashSet<int>? t_withheldActorIds;

    public static void ApplyActorSnapshots(EnemyRegistry registry, IReadOnlyList<ActorSnapshot> actors)
    {
        AddOrUpdateEnemies(registry, actors);
        RemoveAbsentEnemies(registry, actors);
    }

    // First half of a snapshot: every live enemy in it exists and has its new state. Split from the
    // removal so damage can resolve both an enemy that is new in this snapshot and one this
    // snapshot kills.
    public static void AddOrUpdateEnemies(EnemyRegistry registry, IReadOnlyList<ActorSnapshot> actors)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(actors);

        for (int i = 0; i < actors.Count; i++)
        {
            ActorSnapshot actor = actors[i];
            if (!IsLiveEnemy(actor))
            {
                continue;
            }

            EnemyBehaviorState behaviorState = actor.EnemyBehaviorState ?? EnemyBehaviorState.Idle;
            if (registry.TryGetEnemyByActorId(actor.ActorId, out EnemyActor? existing) && existing != null)
            {
                existing.Combatant.Health.SetAuthoritativeState(actor.CurrentHealth, actor.MaxHealth);
                existing.SetAuthoritativeState(actor.Position, actor.Facing, behaviorState);
                continue;
            }

            registry.Add(CreateEnemy(actor, behaviorState));
        }
    }

    // Second half against the whole snapshot: enemies it no longer lists as live are dropped,
    // except the ones it withheld as stale (ClientSnapshotActorCompleteness), which are still
    // there and only missing their update. An enemy that really died or left is dropped even when
    // the same snapshot withholds others, so a coalesced death and respawn still snaps.
    public static void RemoveAbsentEnemies(EnemyRegistry registry, in ServerSnapshotEnvelope snapshot)
    {
        ArgumentNullException.ThrowIfNull(registry);

        HashSet<int> withheld = t_withheldActorIds ??= [];
        ClientSnapshotActorCompleteness.CollectWithheldActorIds(snapshot, withheld);
        if (withheld.Count == 0)
        {
            RemoveAbsentEnemies(registry, snapshot.Actors);
            return;
        }

        HashSet<int> activeEnemyActorIds = t_activeEnemyActorIds ??= [];
        activeEnemyActorIds.Clear();
        IReadOnlyList<ActorSnapshot> actors = snapshot.Actors;
        for (int i = 0; i < actors.Count; i++)
        {
            if (IsLiveEnemy(actors[i]))
            {
                activeEnemyActorIds.Add(actors[i].ActorId);
            }
        }

        // A withheld record carries no kind, so every withheld id is kept; only enemy ids can
        // match anything in this registry.
        foreach (int actorId in withheld)
        {
            activeEnemyActorIds.Add(actorId);
        }

        registry.RemoveAllExcept(activeEnemyActorIds);
    }

    // Second half: enemies the snapshot no longer lists as live are dropped.
    public static void RemoveAbsentEnemies(EnemyRegistry registry, IReadOnlyList<ActorSnapshot> actors)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(actors);

        HashSet<int> activeEnemyActorIds = t_activeEnemyActorIds ??= [];
        activeEnemyActorIds.Clear();
        for (int i = 0; i < actors.Count; i++)
        {
            if (IsLiveEnemy(actors[i]))
            {
                activeEnemyActorIds.Add(actors[i].ActorId);
            }
        }

        registry.RemoveAllExcept(activeEnemyActorIds);
    }

    private static bool IsLiveEnemy(ActorSnapshot actor) =>
        actor.Kind == CreatureKind.Enemy && actor.CurrentHealth > 0 && actor.IsAlive;

    // The client never attacks for an enemy; these were always placeholders here, not the server's
    // 12 and 1.05 s, and stay so to keep client behaviour identical.
    private const int ClientAttackDamagePlaceholder = 10;
    private const float ClientAttackIntervalPlaceholderSeconds = 1f;

    // The client never reads the content catalog. These ranges and these speeds only feed an actor the
    // client never simulates (nothing here ever calls EnemyActor.Update), and stay the wolf's shipped
    // values so nothing about it changes. They are kept in step with enemies.json anyway: a stale 2.8
    // here read like the client knowing the enemy's real speed, which it does not - the renderer
    // measures the gait from the drawn pose instead.
    private const float ClientAggroRangePlaceholder = 10f;
    private const float ClientAttackRangePlaceholder = 1.35f;
    private const float ClientMoveSpeedPlaceholder = 5.0f;
    private const float ClientPatrolSpeedPlaceholder = 1.4f;
    private const string ClientDisplayNameFallback = "Wolf";

    // Wire levels are hostile; 0 (absent) is level 1.
    public const int MaxDisplayLevel = Dream.Client.Presentation.ClientScenePresenter.MaxDisplayLevel;

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
            patrolSpeed: ClientPatrolSpeedPlaceholder,
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
