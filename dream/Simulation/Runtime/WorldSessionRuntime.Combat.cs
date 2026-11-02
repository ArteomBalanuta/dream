using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Combat;
using Dream.Simulation.Players;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;

namespace Dream.Simulation.Runtime;

// Combat: combat actor lookup, and the bridge to AuthoritativeCombatResolver (seeded attack rolls,
// combat use actions and kill experience).
public sealed partial class WorldSessionRuntime
{
    public bool TryResolveCombatActor(int actorId, out CombatActorHandle actor)
    {
        // Scans the listed players directly rather than filling a shared target list, so a call
        // from an event raised mid-tick cannot disturb the tick's list.
        if (TryFindListedPlayerTarget(actorId, out IAuthoritativePlayerTarget? playerTarget))
        {
            actor = playerTarget!.Combatant.Actor;
            return true;
        }

        if (_enemies.TryGetActorById(actorId, out actor))
        {
            return true;
        }

        if (_worldObjects.TryGetByActorId(actorId, out WorldObjectRecord record))
        {
            actor = record.Actor;
            return true;
        }

        actor = default;
        return false;
    }

    // Combat resolution lives in AuthoritativeCombatResolver; the runtime supplies the tick, the
    // session's mode and combatant, and implements IAuthoritativeCombatHost over its persistence.
    private AuthoritativeActionResult ProcessCombatUseAction(
        PlayerActionMessage message,
        CombatUseIntent intent,
        PlayerInteractionMode interactionMode,
        PlayerCombatant combatant)
    {
        return _combatResolver.ProcessCombatUse(message, intent, interactionMode, combatant, CurrentServerTick);
    }

    private static CombatUseIntent ResolveAuthoritativeCombatIntent(
        AuthoritativeSessionActionContext context,
        CombatUseIntent clientIntent)
    {
        return AuthoritativeCombatResolver.ResolveAuthoritativeCombatIntent(
            context.CombatRayOrigin,
            context.CombatRayDirection,
            clientIntent);
    }

    private AuthoritativeCombatResolver CreateCombatResolver()
    {
        return new AuthoritativeCombatResolver(
            SimulationSeed,
            () => Map,
            _enemies,
            _worldObjects,
            _events,
            _combatFormulaService,
            new CombatHost(this));
    }

    // The runtime side of IAuthoritativeCombatHost: player lookups and the persistence writes.
    private sealed class CombatHost(WorldSessionRuntime runtime) : IAuthoritativeCombatHost
    {
        public bool TryGetSessionCombatant(PlayerSessionId sessionId, out PlayerCombatant? combatant)
        {
            if (!runtime.TryResolveAuthoritativePlayerTarget(sessionId, out _, out IAuthoritativePlayerTarget? playerTarget)
                || playerTarget == null)
            {
                combatant = null;
                return false;
            }

            combatant = playerTarget.Combatant;
            return true;
        }

        public void PersistPlayerProgression(PlayerSessionId sessionId, PlayerCombatant combatant)
            => runtime.PersistPlayerProfile(sessionId, combatant);

        public void DeletePersistedEnemy(int enemyId) => runtime.DeletePersistedEnemy(enemyId);

        public void GrantLoot(PlayerSessionId killer, string enemyKind, int enemyActorId, ulong tick, OpenTK.Mathematics.Vector3 deathPosition)
            => runtime.GrantLoot(killer, enemyKind, enemyActorId, tick, deathPosition);

        public WorldObjectRecord RepairCubeRecord(WorldObjectRecord record)
            => runtime.RepairCubeRecordFromPersistence(record);

        public void UpdatePersistedCubeHealth(WorldObjectRecord record) => runtime.UpdatePersistedCubeHealth(record);

        public void DeletePersistedPlacedCube(int cubeId) => runtime.DeletePersistedPlacedCube(cubeId);

        public void PersistFracturePieces(WorldObjectRecord record, GeometryCellKey cell)
            => runtime.PersistFracturePieces(record, cell);

        public void PersistTerrainCellRemoved(WorldObjectRecord record, GeometryCellKey cell)
            => runtime.PersistTerrainCellRemoved(record, cell);

        public bool CanDamageCube(PlayerSessionId attacker, WorldObjectRecord record)
            => runtime.CanDamageCube(attacker, record);

        public CombatTargetPolicy TargetPolicy => new(runtime.PvpEnabled);

        public void FillPvpTargets(PlayerSessionId attacker, List<CombatTargetCandidate> into)
            => runtime.FillPvpTargets(attacker, into);

        public PlayerCombatant? ResolvePvpVictim(int actorId)
            => runtime.TryFindListedPlayerTarget(actorId, out IAuthoritativePlayerTarget? target) ? target!.Combatant : null;
    }

    // Build protection (M4). A cube with no owner (terrain, unowned) can be damaged by anyone; an owned
    // cube only by its owner. The owner is stored as the attacker's character key (see ProcessBuildUseAction),
    // compared ordinally. The seam M5 parties extend: allow a party member here.
    private bool CanDamageCube(PlayerSessionId attacker, WorldObjectRecord record)
    {
        string? owner = record.OwnerPlayerName;
        return string.IsNullOrWhiteSpace(owner)
            || string.Equals(owner, TryResolveCharacterKey(attacker), StringComparison.Ordinal);
    }

    private readonly List<IAuthoritativePlayerTarget> _pvpPlayerScratch = [];

    // PvP candidates: spawned, alive players other than the attacker and not under spawn protection (the
    // same test enemies use in FillEnemyTargets). Reused scratch, no allocation.
    private void FillPvpTargets(PlayerSessionId attacker, List<CombatTargetCandidate> into)
    {
        FillAuthoritativePlayerTargets(_pvpPlayerScratch, spawnedAndAliveOnly: true);
        ulong tick = CurrentServerTick;
        for (int i = 0; i < _pvpPlayerScratch.Count; i++)
        {
            IAuthoritativePlayerTarget target = _pvpPlayerScratch[i];
            if (target.SessionId == attacker
                || (_sessions.TryGet(target.SessionId, out ConnectedPlayer? player)
                    && player != null
                    && player.ProtectedThroughTick != 0
                    && player.ProtectedThroughTick >= tick))
            {
                continue;
            }

            // The body centre, not the combatant's combat anchor: that one is 1.2 above the position, which
            // puts it above the head of a player whose eye is 0.35 above the position (an eye-height ray
            // would miss by more than the hit radius). Floating text still uses the actor's own anchor.
            into.Add(new CombatTargetCandidate(
                target.Combatant.Actor,
                target.Position,
                Priority: 20));
        }

        _pvpPlayerScratch.Clear();
    }
}
