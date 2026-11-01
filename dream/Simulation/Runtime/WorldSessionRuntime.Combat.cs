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

        public void GrantLoot(PlayerSessionId killer, string enemyKind, int enemyActorId, ulong tick)
            => runtime.GrantLoot(killer, enemyKind, enemyActorId, tick);

        public WorldObjectRecord RepairCubeRecord(WorldObjectRecord record)
            => runtime.RepairCubeRecordFromPersistence(record);

        public void UpdatePersistedCubeHealth(WorldObjectRecord record) => runtime.UpdatePersistedCubeHealth(record);

        public void DeletePersistedPlacedCube(int cubeId) => runtime.DeletePersistedPlacedCube(cubeId);

        public void PersistFracturePieces(WorldObjectRecord record, GeometryCellKey cell)
            => runtime.PersistFracturePieces(record, cell);

        public void PersistTerrainCellRemoved(WorldObjectRecord record, GeometryCellKey cell)
            => runtime.PersistTerrainCellRemoved(record, cell);
    }
}
