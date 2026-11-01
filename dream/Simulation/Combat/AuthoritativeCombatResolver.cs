using OpenTK.Mathematics;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Determinism;
using Dream.Simulation.Enemies;
using Dream.Simulation.Players;
using Dream.Simulation.Rules;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;

namespace Dream.Simulation.Combat;

// What the combat resolver needs from its host, beyond the world it hits: the attacker's combatant
// for kill experience, and the persistence writes a hit causes. WorldSessionRuntime implements it
// over its persistence bridge; a test can record the calls.
internal interface IAuthoritativeCombatHost
{
    // The combatant of a spawned (or local) session's player, for kill experience; false when the
    // session has none.
    bool TryGetSessionCombatant(PlayerSessionId sessionId, out PlayerCombatant? combatant);

    void PersistPlayerProgression(PlayerSessionId sessionId, PlayerCombatant combatant);

    void DeletePersistedEnemy(int enemyId);

    // The killer's loot for an enemy killed at the tick. Called after DeletePersistedEnemy, so a crash
    // between the two can lose a drop but never duplicate one by restoring the enemy. Must not block the
    // tick (see WorldSessionRuntime.GrantLoot).
    void GrantLoot(PlayerSessionId killer, string enemyKind, int enemyActorId, ulong tick);

    // Returns the record to damage, with its health restored from persistence when it drifted.
    WorldObjectRecord RepairCubeRecord(WorldObjectRecord record);

    void UpdatePersistedCubeHealth(WorldObjectRecord record);

    void DeletePersistedPlacedCube(int cubeId);

    void PersistFracturePieces(WorldObjectRecord record, GeometryCellKey cell);

    // A cell of the map's base terrain (not a placed cube) was destroyed. Called after
    // DeletePersistedPlacedCube and immediately before PersistFracturePieces for the same cell.
    void PersistTerrainCellRemoved(WorldObjectRecord record, GeometryCellKey cell);
}

// Resolves an authoritative combat use: the seeded attack roll, the enemy or destructible cube it
// hits, the damage and death events, cube fracture and kill experience. It holds no runtime
// reference; the tick is passed per call, the map is read through a delegate (the runtime rebuilds
// it on a floor-size change), and persistence and player lookups go through the host.
internal sealed class AuthoritativeCombatResolver
{
    private readonly ulong _simulationSeed;
    private readonly Func<Map> _map;
    private readonly EnemyRegistry _enemies;
    private readonly WorldObjectRegistry _worldObjects;
    private readonly SceneEventHub _events;
    private readonly CombatFormulaService _formulas;
    private readonly IAuthoritativeCombatHost _host;
    // Reused scratch for one fractured cell's pieces; never handed out.
    private readonly List<(GeometryCellKey Cell, int PieceIndex, GeometryFracturePiece Piece, GeometryMaterialId MaterialId)>
        _fracturePieceScratch = [];

    public AuthoritativeCombatResolver(
        ulong simulationSeed,
        Func<Map> map,
        EnemyRegistry enemies,
        WorldObjectRegistry worldObjects,
        SceneEventHub events,
        CombatFormulaService formulas,
        IAuthoritativeCombatHost host)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(enemies);
        ArgumentNullException.ThrowIfNull(worldObjects);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(formulas);
        ArgumentNullException.ThrowIfNull(host);
        _simulationSeed = simulationSeed;
        _map = map;
        _enemies = enemies;
        _worldObjects = worldObjects;
        _events = events;
        _formulas = formulas;
        _host = host;
    }

    public ulong SimulationSeed => _simulationSeed;

    // Counter-based: the rolls are a pure function of (simulation seed, tick, session, action
    // sequence, target), so they do not depend on the order actions are processed in a tick, and a
    // replay of the same actions reproduces them. The target is the hit actor's id: the enemy's, or
    // the cube's world-object actor id (stable for the cube's life, unlike its cell once it moves).
    public PlayerAttackResolution ResolveSeededAttack(
        PlayerCombatant combatant,
        PlayerActionMessage message,
        ulong tick,
        ulong targetSeed)
    {
        return ResolveSeededAttack(_simulationSeed, combatant, message, tick, targetSeed);
    }

    public static PlayerAttackResolution ResolveSeededAttack(
        ulong simulationSeed,
        PlayerCombatant combatant,
        PlayerActionMessage message,
        ulong tick,
        ulong targetSeed)
    {
        DeterministicRandom rng = new(Seed.Combine(
            simulationSeed,
            tick,
            StableHash.Of(message.SessionId.Value),
            message.ActionSequence,
            targetSeed));
        return combatant.ResolveAttack(
            () => rng.NextInt(
                combatant.DerivedStats.MinDamage,
                combatant.DerivedStats.MaxDamage + 1),
            rng.NextDouble);
    }

    // The server resolves combat from its own view ray; the client's intent only says "attack".
    public static CombatUseIntent ResolveAuthoritativeCombatIntent(
        Vector3 serverRayOrigin,
        Vector3 serverRayDirection,
        CombatUseIntent clientIntent)
    {
        _ = clientIntent;
        return new CombatUseIntent(serverRayOrigin, serverRayDirection);
    }

    public AuthoritativeActionResult ProcessCombatUse(
        PlayerActionMessage message,
        CombatUseIntent intent,
        PlayerInteractionMode interactionMode,
        PlayerCombatant combatant,
        ulong tick)
    {
        if (interactionMode != PlayerInteractionMode.Combat)
        {
            return CreateRejectedResult(message, tick, "Combat action rejected because the authoritative mode is not Combat.");
        }

        if (!combatant.TryStartAttack())
        {
            return CreateRejectedResult(message, tick, "Combat action rejected because attack is on cooldown.");
        }

        EnemyActor? enemy = CombatUseResolver.TryResolveEnemyHit(
            _enemies,
            intent.RayOrigin,
            intent.RayDirection,
            CombatUseResolver.DefaultHitRange);
        if (enemy != null)
        {
            return ResolveEnemyHit(message, combatant, tick, enemy);
        }

        Map map = _map();
        WorldObjectRecord? record = CombatUseResolver.TryResolveDestructibleCubeHit(
            map,
            _worldObjects,
            intent.RayOrigin,
            intent.RayDirection,
            CombatUseResolver.DefaultHitRange);
        if (record == null)
        {
            return new AuthoritativeActionResult(
                message.SessionId.Value,
                message.ActionSequence,
                tick,
                Accepted: true,
                Payload: new CombatResolutionResult(
                    TargetActorId: null,
                    TargetKind: null,
                    DamageApplied: 0,
                    TargetDied: false,
                    CurrentHealth: 0,
                    MaxHealth: 0));
        }

        return ResolveCubeHit(message, combatant, tick, map, record);
    }

    // Awards the killer's session experience for an enemy of enemyLevel, levelling up as far as it
    // reaches, and persists the progression. No-op when the session has no player or the reward is 0.
    public void AwardEnemyKillExperience(PlayerSessionId sessionId, int enemyLevel)
    {
        if (!_host.TryGetSessionCombatant(sessionId, out PlayerCombatant? combatant) || combatant == null)
        {
            return;
        }

        int reward = _formulas.EvaluateEnemyExperienceReward(enemyLevel, combatant.Level);
        if (reward <= 0)
        {
            return;
        }

        int level = combatant.Level;
        int experience = combatant.Experience + reward;
        int required = _formulas.EvaluateExperienceToNextLevel(level, experience);
        while (experience >= required)
        {
            experience -= required;
            level++;
            required = _formulas.EvaluateExperienceToNextLevel(level, experience);
        }

        combatant.ApplyProgression(level, experience);
        _host.PersistPlayerProgression(sessionId, combatant);
    }

    // The valid fracture pieces of one rubble cell, in piece-index order. Reads only that cell
    // (Map.CaptureFracturePiecesInto(cell, ...)) instead of capturing every rubble cell and
    // filtering, which was O(all rubble) per fracture; the result is the same pieces in the same
    // order, since the map holds one rubble record per cell.
    internal static GeometryFracturePiece[] CaptureCellFracturePieces(
        Map map,
        GeometryCellKey cell,
        List<(GeometryCellKey Cell, int PieceIndex, GeometryFracturePiece Piece, GeometryMaterialId MaterialId)> scratch)
    {
        scratch.Clear();
        map.CaptureFracturePiecesInto(cell, scratch);
        int count = 0;
        for (int index = 0; index < scratch.Count; index++)
        {
            if (scratch[index].Cell == cell && scratch[index].Piece.IsValid())
            {
                count++;
            }
        }

        if (count == 0)
        {
            scratch.Clear();
            return [];
        }

        GeometryFracturePiece[] pieces = new GeometryFracturePiece[count];
        int written = 0;
        for (int index = 0; index < scratch.Count; index++)
        {
            if (scratch[index].Cell == cell && scratch[index].Piece.IsValid())
            {
                pieces[written++] = scratch[index].Piece;
            }
        }

        scratch.Clear();
        return pieces;
    }

    private AuthoritativeActionResult ResolveEnemyHit(
        PlayerActionMessage message,
        PlayerCombatant combatant,
        ulong tick,
        EnemyActor enemy)
    {
        PlayerAttackResolution attack = ResolveSeededAttack(
            combatant,
            message,
            tick,
            Seed.FromInt(enemy.Combatant.Actor.Id));
        DamageReceipt receipt = enemy.Combatant.ApplyDamage(attack.Damage);
        if (receipt.AppliedAmount > 0)
        {
            _events.Publish(new DamageAppliedEvent(
                enemy.Combatant.Actor,
                receipt.AppliedAmount,
                attack.FloatingTextKind));
        }

        if (receipt.WasFatal)
        {
            AwardEnemyKillExperience(message.SessionId, enemy.Level);
            _host.DeletePersistedEnemy(enemy.Combatant.Actor.Id);
            _enemies.PruneInactive();
            _host.GrantLoot(message.SessionId, enemy.EnemyKind, enemy.Combatant.Actor.Id, tick);
        }

        return new AuthoritativeActionResult(
            message.SessionId.Value,
            message.ActionSequence,
            tick,
            Accepted: true,
            Payload: new CombatResolutionResult(
                enemy.Combatant.Actor.Id,
                enemy.Combatant.Actor.Kind,
                receipt.AppliedAmount,
                receipt.WasFatal,
                enemy.Combatant.Health.CurrentHealth,
                enemy.Combatant.Health.MaxHealth));
    }

    private AuthoritativeActionResult ResolveCubeHit(
        PlayerActionMessage message,
        PlayerCombatant combatant,
        ulong tick,
        Map map,
        WorldObjectRecord record)
    {
        record = _host.RepairCubeRecord(record);

        PlayerAttackResolution cubeAttack = ResolveSeededAttack(
            combatant,
            message,
            tick,
            Seed.FromInt(record.Actor.Id));
        DamageReceipt cubeReceipt = record.ApplyDamage(cubeAttack.Damage);
        if (cubeReceipt.AppliedAmount > 0)
        {
            _events.Publish(new DamageAppliedEvent(
                record.Actor,
                cubeReceipt.AppliedAmount,
                cubeAttack.FloatingTextKind));
        }

        if (!cubeReceipt.WasFatal)
        {
            _host.UpdatePersistedCubeHealth(record);
            return CreateCubeResult(message, tick, record, cubeReceipt);
        }

        _events.Publish(new ActorDiedEvent(record.Actor));
        object sourceObject = record.SourceObject;
        if (sourceObject is Cube destroyedCube)
        {
            bool hasCell = map.TryGetCellForCube(destroyedCube, out GeometryCellKey cell);
            // Before the fracture removes the cell: afterwards it no longer holds its source record.
            bool sourceCell = hasCell && map.IsSourceCell(cell);
            if (hasCell && map.FractureCubeIntoPieces(cell))
            {
                GeometryCellRecord? fracturedCell = map.TryGetRubbleAtCell(cell);
                GeometryMaterialId materialId = fracturedCell?.Definition.MaterialId ?? GeometryMaterialId.Grass;
                GeometryFracturePiece[] fracturePieces = CaptureCellFracturePieces(map, cell, _fracturePieceScratch);
                _host.DeletePersistedPlacedCube(record.Actor.Id);
                if (sourceCell)
                {
                    _host.PersistTerrainCellRemoved(record, cell);
                }

                _host.PersistFracturePieces(record, cell);
                _worldObjects.Unregister(sourceObject, preserveDetachedRecord: false);
                _events.Publish(new CubeFracturedEvent(
                    destroyedCube,
                    new Vector3(cell.X, cell.Y, cell.Z),
                    materialId,
                    fracturePieces));
            }
            else if (_worldObjects.TryRemove(record))
            {
                _host.DeletePersistedPlacedCube(record.Actor.Id);
                _events.Publish(new CubeDestroyedEvent(
                    destroyedCube,
                    new Vector3(
                        MathF.Floor(destroyedCube.Position.X + 1e-6f),
                        MathF.Floor(destroyedCube.Position.Y + 1e-6f),
                        MathF.Floor(destroyedCube.Position.Z + 1e-6f)),
                    record.Actor.Id));
            }
        }
        else
        {
            _host.DeletePersistedPlacedCube(record.Actor.Id);
            _worldObjects.TryRemove(record);
        }

        return CreateCubeResult(message, tick, record, cubeReceipt);
    }

    private static AuthoritativeActionResult CreateCubeResult(
        PlayerActionMessage message,
        ulong tick,
        WorldObjectRecord record,
        DamageReceipt receipt)
    {
        return new AuthoritativeActionResult(
            message.SessionId.Value,
            message.ActionSequence,
            tick,
            Accepted: true,
            Payload: new CombatResolutionResult(
                record.Actor.Id,
                record.Actor.Kind,
                receipt.AppliedAmount,
                receipt.WasFatal,
                record.Health.CurrentHealth,
                record.Health.MaxHealth));
    }

    private static AuthoritativeActionResult CreateRejectedResult(PlayerActionMessage message, ulong tick, string reason)
    {
        return new AuthoritativeActionResult(
            message.SessionId.Value,
            message.ActionSequence,
            tick,
            Accepted: false,
            Payload: null,
            Rejection: new RejectedUseDelta(reason));
    }
}
