using OpenTK.Mathematics;
using Dream.Multiplayer.Diagnostics;
using Dream.Simulation.Enemies;
using Dream.Simulation.Players;
using Dream.Simulation.World;

namespace Dream.Simulation.Runtime;

// Enemies: enemy actor id allocation, initial wolf spawning and player/enemy separation.
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

    private void SpawnInitialWolf()
    {
        if (!TryAllocateEnemyActorId(out int actorId))
        {
            return;
        }

        Vector3 spawnPosition = WolfSpawnPolicy.ResolveInitialSpawn(Map.Width, Map.Depth);

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

    // Monotonic and deterministic. A wolf is only spawned when no enemy is active (startup or
    // EnsureWolfExists), and a restore advances the counter past every persisted enemy, so the
    // id is never held by a live or persisted enemy.
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

    private void EnsureWolfExists()
    {
        if (_enemies.HasActiveActors)
        {
            return;
        }

        SpawnInitialWolf();
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
