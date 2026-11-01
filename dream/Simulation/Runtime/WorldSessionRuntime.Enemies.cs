using OpenTK.Mathematics;
using Dream.Multiplayer.Diagnostics;
using Dream.Simulation.Content;
using Dream.Simulation.Enemies;
using Dream.Simulation.Persistence;
using Dream.Simulation.Players;
using Dream.Simulation.Rules;
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
    // ids. A restore gives persisted enemies fresh ids from it, in ObjectId order.
    private int _nextEnemyActorId = FirstEnemyActorId;
    private bool _enemyIdsExhaustedLogged;

    // Active wolves the population step keeps: one by day, two at night (WorldDayPhase.Night).
    public const int DayWolfTarget = 1;
    public const int NightWolfTarget = 2;

    CombatFormulaService IRuntimePersistenceHost.CombatFormulas => _combatFormulaService;

    // The content the population step spawns from. The shipped catalog; a test seam for catalogs with
    // other templates (for example none with the NightExtra role).
    internal ContentCatalog EnemyContent { get; set; } = ContentCatalog.Shipped;

    // The first wolf of a world (restore with no persisted enemy), at the day spawn.
    private void SpawnInitialWolf()
    {
        SpawnEnemy(FindTemplate(EnemySpawnRole.Primary) ?? EnemyContent.Wolf, WolfSpawnPolicy.ResolveInitialSpawn(Map.Width, Map.Depth));
    }

    // The first template with the role, in the catalog's file order. Null when none has it.
    private EnemyTemplate? FindTemplate(EnemySpawnRole role)
    {
        IReadOnlyList<EnemyTemplate> templates = EnemyContent.Enemies;
        for (int i = 0; i < templates.Count; i++)
        {
            if (templates[i].SpawnRole == role)
            {
                return templates[i];
            }
        }

        return null;
    }

    private bool IsNightExtra(EnemyActor enemy)
    {
        return EnemyContent.TryGetEnemy(enemy.EnemyKind, out EnemyTemplate template)
            && template.SpawnRole == EnemySpawnRole.NightExtra;
    }

    // A new, persisted enemy of template at spawnPosition with the next enemy id. Allocates (the actor,
    // its closure, the queued row write), so it only runs on a spawn tick.
    private void SpawnEnemy(EnemyTemplate template, Vector3 spawnPosition)
    {
        if (!TryAllocateEnemyActorId(out int actorId))
        {
            return;
        }

        // The stats come from enemies.json (loaded at startup, never parsed here); EnemyFactory derives
        // damage, health and the attack interval through the enemy.* formulas.
        EnemyActor enemy = EnemyFactory.Spawn(template, spawnPosition, actorId, _events, _combatFormulaService);
        _enemies.Add(enemy);
        PersistEnemy(enemy);
    }

    // Monotonic and deterministic. Ids are never reused (a despawned or killed wolf's id stays
    // taken), so the id is never held by a live enemy. A persisted enemy's durable identity is its
    // ObjectId (ObjectIdentityMap), not this id.
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

    // The wolf population step: authoritative only, once per tick inside the gameplay block, after
    // WorldClock.Advance and after the enemies acted and the dead were pruned. The clock is a
    // gameplay input here, so it is a pure function of (persisted world ticks, TicksPerDay, tick
    // count), which lockstep servers must share. At most one change per tick:
    // - fewer active enemies than the target (2 at night, else 1): spawn one. With none active, the
    //   first Primary template at the day spawn. With one active at night, the role that is missing
    //   at the night spawn (a Primary when the active one is a NightExtra, otherwise a NightExtra;
    //   a catalog with no NightExtra spawns another Primary, as before the Dire Wolf);
    // - not night and more than one active: remove the owned NightExtra with the highest actor id,
    //   else the owned enemy with the highest actor id, and queue its row delete like a kill does
    //   (checkpoints never delete rows). Not night with a lone owned NightExtra active: remove it,
    //   and the spawn step above brings a Primary next tick.
    // The per-tick scan only counts; the templates are consulted on spawn and despawn ticks. An ordinary day
    // tick with one owned wolf also runs HighestOwnedNightExtraId (a scan of the active enemies, no
    // allocation).
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
            if (activeWolves == 0)
            {
                SpawnEnemy(FindTemplate(EnemySpawnRole.Primary) ?? EnemyContent.Wolf, WolfSpawnPolicy.ResolveInitialSpawn(Map.Width, Map.Depth));
            }
            else
            {
                SpawnEnemy(ChooseMissingRoleTemplate(), WolfSpawnPolicy.ResolveNightSpawn(Map.Width, Map.Depth));
            }

            return;
        }

        if (night || ownedWolves == 0)
        {
            return;
        }

        int removeId;
        if (ownedWolves > DayWolfTarget)
        {
            removeId = HighestOwnedNightExtraId();
            if (removeId == 0)
            {
                removeId = highestOwnedActorId;
            }
        }
        else if (activeWolves == ownedWolves)
        {
            // A lone NightExtra at dawn (its Primary died in the night) is removed; the next tick's
            // spawn step brings the day's Primary, so a Dire does not stay all day.
            removeId = HighestOwnedNightExtraId();
        }
        else
        {
            return;
        }

        if (removeId != 0 && _enemies.Remove(removeId))
        {
            DeletePersistedEnemy(removeId);
        }
    }

    // The template for the night slot: a NightExtra unless an active enemy already is one.
    private EnemyTemplate ChooseMissingRoleTemplate()
    {
        IReadOnlyList<EnemyActor> enemies = _enemies.ActiveActors;
        bool hasNightExtra = false;
        for (int i = 0; i < enemies.Count; i++)
        {
            if (enemies[i].IsActive && IsNightExtra(enemies[i]))
            {
                hasNightExtra = true;
                break;
            }
        }

        EnemyTemplate? template = FindTemplate(hasNightExtra ? EnemySpawnRole.Primary : EnemySpawnRole.NightExtra);
        return template ?? FindTemplate(EnemySpawnRole.Primary) ?? EnemyContent.Wolf;
    }

    // The highest actor id among active, runtime-owned NightExtra enemies; 0 when there is none.
    private int HighestOwnedNightExtraId()
    {
        int highest = 0;
        IReadOnlyList<EnemyActor> enemies = _enemies.ActiveActors;
        for (int i = 0; i < enemies.Count; i++)
        {
            EnemyActor enemy = enemies[i];
            int actorId = enemy.Combatant.Actor.Id;
            if (!enemy.IsActive
                || actorId < ReplicatedActorIdRanges.FirstEnemyId
                || actorId >= _nextEnemyActorId
                || actorId <= highest
                || !IsNightExtra(enemy))
            {
                continue;
            }

            highest = actorId;
        }

        return highest;
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
