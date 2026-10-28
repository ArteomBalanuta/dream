using OpenTK.Mathematics;
using Dream.Multiplayer.Diagnostics;
using Dream.Simulation.Enemies;
using Dream.Simulation.Players;
using Dream.Simulation.World;

namespace Dream.Simulation.Runtime;

// Enemies: enemy actor id allocation, the wolf population step (day and night targets) and
// player/enemy separation.
public sealed partial class WorldSessionRuntime
{
    // Actor id of the first enemy a fresh world spawns; each later spawn takes the next id. Enemy
    // ids stay inside their own range, disjoint from cube ids (ReplicatedActorIdRanges).
    public const int FirstEnemyActorId = ReplicatedActorIdRanges.FirstEnemyId;

    // Every spawned enemy gets a fresh actor id, so a respawn is a different actor on the wire
    // and a client drops the dead enemy's interpolation buffer instead of sliding it to the new
    // spawn. Part of simulation state: same-seed servers with the same history allocate the same
    // ids. Persisted enemies advance it past their ids when the world is restored.
    private int _nextEnemyActorId = FirstEnemyActorId;
    private bool _enemyIdsExhaustedLogged;

    // Active wolves the population step keeps: one by day, two at night (WorldDayPhase.Night).
    public const int DayWolfTarget = 1;
    public const int NightWolfTarget = 2;

    // The first wolf of a world (restore with no persisted enemy), at the day spawn.
    private void SpawnInitialWolf()
    {
        SpawnWolf(WolfSpawnPolicy.ResolveInitialSpawn(Map.Width, Map.Depth));
    }

    // A new, persisted wolf at spawnPosition with the next enemy id. Allocates (the actor, its
    // closure, the queued row write), so it only runs on a spawn tick.
    private void SpawnWolf(Vector3 spawnPosition)
    {
        if (!TryAllocateEnemyActorId(out int actorId))
        {
            return;
        }

        EnemyCombatant combatant = new(
            () => spawnPosition,
            _events,
            actorId: actorId,
            maxHealth: 100,
            attackDamage: 12,
            attackIntervalSeconds: 1.05f,
            anchorHeightOffset: WolfDefinition.CombatAnchorHeight);
        EnemyActor wolf = new(
            combatant,
            spawnPosition,
            aggroRange: 10f,
            attackRange: 1.35f,
            moveSpeed: 2.8f,
            preferredStandOffDistance: WolfDefinition.PreferredStandOffDistance,
            collisionRadius: WolfDefinition.CollisionRadius,
            collisionHeight: WolfDefinition.CollisionHeight,
            displayName: "Wolf",
            enemyKind: "Wolf",
            level: 1);
        _enemies.Add(wolf);
        PersistEnemy(wolf);
    }

    // Monotonic and deterministic. Ids are never reused (a despawned or killed wolf's id stays
    // taken), and a restore advances the counter past every persisted enemy, so the id is never
    // held by a live or persisted enemy.
    //
    // False once the enemy range (about a billion ids) is exhausted. Spawns run on the tick, where
    // a throw would stop the server, so the spawn is refused (logged once) instead: the world goes
    // on without that enemy.
    private bool TryAllocateEnemyActorId(out int actorId)
    {
        if (_nextEnemyActorId == ReplicatedActorIdRanges.ExhaustedCounter)
        {
            if (!_enemyIdsExhaustedLogged)
            {
                _enemyIdsExhaustedLogged = true;
                NetworkDiagnosticsLog.Write("server.ids.enemy-exhausted", "the replicated enemy id range is exhausted; enemy spawns are refused");
            }

            actorId = 0;
            return false;
        }

        actorId = ReplicatedActorIdRanges.TakeEnemyId(ref _nextEnemyActorId);
        return true;
    }

    // Keeps later enemy spawns past an id in the enemy range that is already taken: a persisted
    // enemy, or a legacy cube saved before the id ranges were partitioned. Reserving the last enemy
    // id saturates the counter instead of failing, so a restore never fails on a persisted id.
    private void ReserveEnemyActorId(int actorId)
    {
        _nextEnemyActorId = ReplicatedActorIdRanges.ReserveEnemyId(_nextEnemyActorId, actorId);
    }

    // The wolf population step: authoritative only, once per tick inside the gameplay block, after
    // WorldClock.Advance and after the enemies acted and the dead were pruned. The clock is a
    // gameplay input here, so it is a pure function of (persisted world ticks, TicksPerDay, tick
    // count), which lockstep servers must share. At most one change per tick:
    // - fewer active wolves than the target (2 at night, else 1): spawn one, at the day spawn when
    //   none is active, otherwise at the night spawn;
    // - not night and more than one active: remove the active wolf with the highest actor id, and
    //   queue its row delete like a kill does (checkpoints never delete rows).
    // A restore that already holds the night's two wolves therefore spawns no third. Allocates
    // nothing except on the spawn and despawn ticks.
    //
    // Only enemies this runtime owns an id for are removed: ones it spawned or restored, whose ids
    // are all below the enemy id counter. On a server that is every enemy. An enemy added from
    // outside with an id the runtime never handed out (a test's scripted target) still counts as
    // active, so it suppresses a spawn as before, but the dawn step never removes it.
    // Named for its call site in WorldSessionRuntime.Commands.cs, which this step keeps unchanged.
    private void EnsureWolfExists()
    {
        int activeWolves = 0;
        int ownedWolves = 0;
        int highestOwnedActorId = 0;
        IReadOnlyList<EnemyActor> enemies = _enemies.ActiveActors;
        for (int i = 0; i < enemies.Count; i++)
        {
            EnemyActor enemy = enemies[i];
            if (!enemy.IsActive)
            {
                continue;
            }

            activeWolves++;
            int actorId = enemy.Combatant.Actor.Id;
            if (actorId < ReplicatedActorIdRanges.FirstEnemyId || actorId >= _nextEnemyActorId)
            {
                continue;
            }

            if (ownedWolves == 0 || actorId > highestOwnedActorId)
            {
                highestOwnedActorId = actorId;
            }

            ownedWolves++;
        }

        bool night = WorldClock.Phase == WorldDayPhase.Night;
        int target = night ? NightWolfTarget : DayWolfTarget;
        if (activeWolves < target)
        {
            SpawnWolf(activeWolves == 0
                ? WolfSpawnPolicy.ResolveInitialSpawn(Map.Width, Map.Depth)
                : WolfSpawnPolicy.ResolveNightSpawn(Map.Width, Map.Depth));
            return;
        }

        if (!night && ownedWolves > DayWolfTarget && _enemies.Remove(highestOwnedActorId))
        {
            DeletePersistedEnemy(highestOwnedActorId);
        }
    }

    private void ResolvePlayerEnemySeparation(List<IAuthoritativePlayerTarget> playerTargets)
    {
        IReadOnlyList<EnemyActor> enemies = _enemies.ActiveActors;
        foreach (IAuthoritativePlayerTarget playerTarget in playerTargets)
        {
            if (playerTarget.ActorId == Player.Combatant.Actor.Id && Player.IsNoclipEnabled)
            {
                continue;
            }

            Vector3 resolvedPosition = playerTarget.Position;
            for (int i = 0; i < enemies.Count; i++)
            {
                EnemyActor actor = enemies[i];
                if (!actor.IsActive)
                {
                    continue;
                }

                resolvedPosition = EnemyPlayerCollisionResolver.ResolvePlayerPosition(
                    resolvedPosition,
                    actor.Position,
                    WolfDefinition.PlayerSeparationDistance,
                    -actor.Forward);
            }

            if ((resolvedPosition - playerTarget.Position).LengthSquared > 0.000001f)
            {
                playerTarget.SetWorldPosition(resolvedPosition);
            }
        }
    }
}
