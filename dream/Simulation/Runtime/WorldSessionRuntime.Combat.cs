using OpenTK.Mathematics;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Combat;
using Dream.Simulation.Determinism;
using Dream.Simulation.Enemies;
using Dream.Simulation.Players;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;

namespace Dream.Simulation.Runtime;

// Combat: combat actor resolution, seeded attack rolls, combat use actions and kill experience.
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

    // Counter-based: the rolls are a pure function of (simulation seed, tick, session, action
    // sequence, target), so they do not depend on the order actions are processed in a tick, and a
    // replay of the same actions reproduces them. The target is the hit actor's id: the enemy's, or
    // the cube's world-object actor id (stable for the cube's life, unlike its cell once it moves).
    private PlayerAttackResolution ResolveSeededAttack(
        PlayerCombatant combatant,
        PlayerActionMessage message,
        ulong targetSeed)
    {
        DeterministicRandom rng = new(Seed.Combine(
            SimulationSeed,
            CurrentServerTick,
            StableHash.Of(message.SessionId.Value),
            message.ActionSequence,
            targetSeed));
        return combatant.ResolveAttack(
            () => rng.NextInt(
                combatant.DerivedStats.MinDamage,
                combatant.DerivedStats.MaxDamage + 1),
            rng.NextDouble);
    }

    private AuthoritativeActionResult ProcessCombatUseAction(
        PlayerActionMessage message,
        CombatUseIntent intent,
        PlayerInteractionMode interactionMode,
        PlayerCombatant combatant)
    {
        if (interactionMode != PlayerInteractionMode.Combat)
        {
            return CreateRejectedActionResult(message, "Combat action rejected because the authoritative mode is not Combat.");
        }

        if (!combatant.TryStartAttack())
        {
            return CreateRejectedActionResult(message, "Combat action rejected because attack is on cooldown.");
        }

        EnemyActor? enemy = CombatUseResolver.TryResolveEnemyHit(
            _enemies,
            intent.RayOrigin,
            intent.RayDirection,
            CombatUseResolver.DefaultHitRange);
        if (enemy != null)
        {
            PlayerAttackResolution attack = ResolveSeededAttack(
                combatant,
                message,
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
                DeletePersistedEnemy(enemy.Combatant.Actor.Id);
                _enemies.PruneInactive();
            }

            return new AuthoritativeActionResult(
                message.SessionId.Value,
                message.ActionSequence,
                CurrentServerTick,
                Accepted: true,
                Payload: new CombatResolutionResult(
                    enemy.Combatant.Actor.Id,
                    enemy.Combatant.Actor.Kind,
                    receipt.AppliedAmount,
                    receipt.WasFatal,
                    enemy.Combatant.Health.CurrentHealth,
                    enemy.Combatant.Health.MaxHealth));
        }

        WorldObjectRecord? record = CombatUseResolver.TryResolveDestructibleCubeHit(
            Map,
            _worldObjects,
            intent.RayOrigin,
            intent.RayDirection,
            CombatUseResolver.DefaultHitRange);
        if (record == null)
        {
            return new AuthoritativeActionResult(
                message.SessionId.Value,
                message.ActionSequence,
                CurrentServerTick,
                Accepted: true,
                Payload: new CombatResolutionResult(
                    TargetActorId: null,
                    TargetKind: null,
                    DamageApplied: 0,
                    TargetDied: false,
                    CurrentHealth: 0,
                    MaxHealth: 0));
        }

        record = RepairCubeRecordFromPersistence(record);

        PlayerAttackResolution cubeAttack = ResolveSeededAttack(
            combatant,
            message,
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
            UpdatePersistedCubeHealth(record);
            return new AuthoritativeActionResult(
                message.SessionId.Value,
                message.ActionSequence,
                CurrentServerTick,
                Accepted: true,
                Payload: new CombatResolutionResult(
                    record.Actor.Id,
                    record.Actor.Kind,
                    cubeReceipt.AppliedAmount,
                    cubeReceipt.WasFatal,
                    record.Health.CurrentHealth,
                    record.Health.MaxHealth));
        }

        _events.Publish(new ActorDiedEvent(record.Actor));
        object sourceObject = record.SourceObject;
        if (sourceObject is Cube destroyedCube)
        {
            if (Map.TryGetCellForCube(destroyedCube, out GeometryCellKey cell) &&
                Map.FractureCubeIntoPieces(cell))
            {
                GeometryCellRecord? fracturedCell = Map.TryGetRubbleAtCell(cell);
                GeometryMaterialId materialId = fracturedCell?.Definition.MaterialId ?? GeometryMaterialId.Grass;
                GeometryFracturePiece[] fracturePieces = Map.CaptureFracturePieces()
                    .Where(piece => piece.Cell == cell)
                    .Select(piece => piece.Piece)
                    .Where(GeometryFracturePiece.IsValid)
                    .ToArray();
                DeletePersistedPlacedCube(record.Actor.Id);
                PersistFracturePieces(record, cell);
                _worldObjects.Unregister(sourceObject, preserveDetachedRecord: false);
                _events.Publish(new CubeFracturedEvent(
                    destroyedCube,
                    new Vector3(cell.X, cell.Y, cell.Z),
                    materialId,
                    fracturePieces));
            }
            else if (_worldObjects.TryRemove(record))
            {
                DeletePersistedPlacedCube(record.Actor.Id);
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
            DeletePersistedPlacedCube(record.Actor.Id);
            _worldObjects.TryRemove(record);
        }

        return new AuthoritativeActionResult(
            message.SessionId.Value,
            message.ActionSequence,
            CurrentServerTick,
            Accepted: true,
            Payload: new CombatResolutionResult(
                record.Actor.Id,
                record.Actor.Kind,
                cubeReceipt.AppliedAmount,
                cubeReceipt.WasFatal,
                record.Health.CurrentHealth,
                record.Health.MaxHealth));
    }

    private void AwardEnemyKillExperience(PlayerSessionId sessionId, int enemyLevel)
    {
        if (!TryResolveAuthoritativePlayerTarget(sessionId, out _, out IAuthoritativePlayerTarget? playerTarget)
            || playerTarget == null)
        {
            return;
        }

        PlayerCombatant combatant = playerTarget.Combatant;
        int reward = _combatFormulaService.EvaluateEnemyExperienceReward(enemyLevel, combatant.Level);
        if (reward <= 0)
        {
            return;
        }

        int level = combatant.Level;
        int experience = combatant.Experience + reward;
        int required = _combatFormulaService.EvaluateExperienceToNextLevel(level, experience);
        while (experience >= required)
        {
            experience -= required;
            level++;
            required = _combatFormulaService.EvaluateExperienceToNextLevel(level, experience);
        }

        combatant.ApplyProgression(level, experience);
        PersistPlayerProfile(sessionId, combatant);
    }

    private static CombatUseIntent ResolveAuthoritativeCombatIntent(
        AuthoritativeSessionActionContext context,
        CombatUseIntent clientIntent)
    {
        _ = clientIntent;
        return new CombatUseIntent(context.CombatRayOrigin, context.CombatRayDirection);
    }
}
