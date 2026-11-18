using OpenTK.Mathematics;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Content;
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
    void GrantLoot(PlayerSessionId killer, string enemyKind, int enemyActorId, ulong tick, Vector3 deathPosition);

    // Returns the record to damage, with its health restored from persistence when it drifted.
    WorldObjectRecord RepairCubeRecord(WorldObjectRecord record);

    void UpdatePersistedCubeHealth(WorldObjectRecord record);

    void DeletePersistedPlacedCube(int cubeId);

    void PersistFracturePieces(WorldObjectRecord record, GeometryCellKey cell);

    // A cell of the map's base terrain (not a placed cube) was destroyed. Called after
    // DeletePersistedPlacedCube and immediately before PersistFracturePieces for the same cell.
    void PersistTerrainCellRemoved(WorldObjectRecord record, GeometryCellKey cell);

    // M4 build protection: whether the attacker may damage this cube. The default allows everything, so a
    // host that does not model ownership (a test double) behaves as before.
    bool CanDamageCube(PlayerSessionId attacker, WorldObjectRecord record) => true;

    // M5 parties: appends the other party members of the session that are spawned, alive and within the
    // sharing range of the position. False (and nothing appended) for a host without parties or a session
    // in none; the kill experience then goes to the member alone.
    bool TryGetNearbyPartyMembers(PlayerSessionId member, Vector3 at, List<PlayerSessionId> into) => false;

    // M6 quests: called once per kill with everyone who damaged the enemy, before the loot is granted.
    void RecordEnemyKill(ReadOnlySpan<EnemyDamageContribution> contributors, string enemyKind, Vector3 at)
    {
    }

    // M4 PvP: read per use. The default is PvE only, so no code path changes unless a host enables it.
    CombatTargetPolicy TargetPolicy => CombatTargetPolicy.DefaultPve;

    // Appends the players the attacker could hit: spawned, alive, not the attacker, not under spawn
    // protection. Only called when TargetPolicy enables player-versus-player.
    void FillPvpTargets(PlayerSessionId attacker, List<CombatTargetCandidate> into)
    {
    }

    // The combatant behind a PvP candidate's actor id. A player's actor handle holds only its health, so
    // damage must go through the combatant to be mitigated by armor and to publish the death event.
    PlayerCombatant? ResolvePvpVictim(int actorId) => null;
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
    // Reused scratch for skills: the enemies of one arc (collected before any damage, because a kill prunes
    // the registry mid-loop), the player candidates of PvP, and the players one arc reaches.
    private readonly List<EnemyActor> _arcScratch = [];
    private readonly List<CombatTargetCandidate> _pvpCandidates = [];
    private readonly List<PlayerSessionId> _partyMateScratch = [];
    private readonly List<(float DistanceSquared, CombatTargetCandidate Candidate)> _pvpArcScratch = [];

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
        // A wall between the ray origin and the enemy's combat anchor blocks the hit, as for bolts, the
        // melee arc and PvP - and as the enemy's own bite line does in the other direction. When it is
        // blocked, fall through to the PvP and cube branches rather than resolving the enemy.
        if (enemy != null && !IsBlockedByWall(_map(), intent.RayOrigin, enemy.Combatant.GetCombatAnchorWorldPosition()))
        {
            return ResolveEnemyHit(message, combatant, tick, enemy);
        }

        // Enemies come first on the ray; with PvP enabled a player on the ray is hit before any cube.
        if (_host.TargetPolicy.PlayerVsPlayerEnabled)
        {
            CombatActorHandle? player = TryResolvePvpTarget(
                message.SessionId,
                intent.RayOrigin,
                intent.RayDirection,
                CombatUseResolver.DefaultHitRange);
            // A wall between the ray origin and the player's body centre blocks the hit, as for bolts.
            if (player.HasValue && !IsBlockedByWall(_map(), intent.RayOrigin, player.Value.GetWorldAnchor()))
            {
                return ResolvePlayerHit(message, combatant, tick, player.Value, 100);
            }
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

        // The identity repair has to run BEFORE the ownership test: a record whose registry link was lost
        // carries no owner, and only the repair puts the persisted owner back, so testing the unrepaired
        // record would hand someone else's wall to the attacker. Build protection then damages nothing
        // for a cube that is someone else's, and gives the swing back (CancelStartedAttack) so that
        // standing in front of a protected wall does not eat the player's attack cadence.
        record = _host.RepairCubeRecord(record);
        if (!_host.CanDamageCube(message.SessionId, record))
        {
            combatant.CancelStartedAttack();
            return CreateRejectedResult(message, tick, "That belongs to someone else.");
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

        GrantExperience(sessionId, combatant, _formulas.EvaluateEnemyExperienceReward(enemyLevel, combatant.Level));
    }

    // Fair kills: the experience reward (at the top present dealer's level) is split equally among the
    // contributors still in the world, in session-id order, the first ones taking the remainder points.
    // Returns who gets the loot: the top damage dealer still present (ties: lowest session id), or the
    // finishing session when nobody recorded is present (a scripted kill). Allocation-free.
    private PlayerSessionId AwardKillRewards(EnemyActor enemy, PlayerSessionId finisher, Vector3 deathPosition)
    {
        ReadOnlySpan<EnemyDamageContribution> contributors = enemy.Contributors;
        Span<int> present = stackalloc int[EnemyActor.MaxContributors];
        int count = 0;
        int top = -1;
        for (int i = 0; i < contributors.Length; i++)
        {
            if (!_host.TryGetSessionCombatant(contributors[i].SessionId, out PlayerCombatant? c) || c == null)
            {
                continue;
            }

            // Insertion by ordinal session id.
            int at = count++;
            while (at > 0 && string.CompareOrdinal(contributors[present[at - 1]].SessionId.Value, contributors[i].SessionId.Value) > 0)
            {
                present[at] = present[at - 1];
                at--;
            }

            present[at] = i;
        }

        for (int k = 0; k < count; k++)
        {
            // Session-id order, so a strict > keeps the lowest id on a tie.
            if (top < 0 || contributors[present[k]].Damage > contributors[top].Damage)
            {
                top = present[k];
            }
        }

        if (top < 0)
        {
            AwardEnemyKillExperience(finisher, enemy.Level);
            return finisher;
        }

        _host.TryGetSessionCombatant(contributors[top].SessionId, out PlayerCombatant? topCombatant);
        int pool = _formulas.EvaluateEnemyExperienceReward(enemy.Level, topCombatant?.Level ?? 1);
        PlayerSessionId winner = contributors[top].SessionId;
        for (int k = 0; k < count; k++)
        {
            EnemyDamageContribution entry = contributors[present[k]];
            if (_host.TryGetSessionCombatant(entry.SessionId, out PlayerCombatant? combatant) && combatant != null)
            {
                GrantSharedExperience(entry.SessionId, combatant, pool / count + (k < pool % count ? 1 : 0), deathPosition);
            }
        }

        return winner;
    }

    // M5: one contributor's share, split evenly with the party mates near the death, in ordinal session
    // order, the first ones taking the remainder. Without mates it is the plain grant. A mate's part is
    // not split again, so shares do not chain.
    private void GrantSharedExperience(PlayerSessionId sessionId, PlayerCombatant combatant, int share, Vector3 at)
    {
        _partyMateScratch.Clear();
        if (share <= 0 || !_host.TryGetNearbyPartyMembers(sessionId, at, _partyMateScratch) || _partyMateScratch.Count == 0)
        {
            _partyMateScratch.Clear();
            GrantExperience(sessionId, combatant, share);
            return;
        }

        _partyMateScratch.Add(sessionId);
        _partyMateScratch.Sort(static (left, right) => string.CompareOrdinal(left.Value, right.Value));
        int people = _partyMateScratch.Count;
        for (int k = 0; k < people; k++)
        {
            PlayerSessionId who = _partyMateScratch[k];
            if (_host.TryGetSessionCombatant(who, out PlayerCombatant? whoCombatant) && whoCombatant != null)
            {
                GrantExperience(who, whoCombatant, share / people + (k < share % people ? 1 : 0));
            }
        }

        _partyMateScratch.Clear();
    }

    private void GrantExperience(PlayerSessionId sessionId, PlayerCombatant combatant, int reward)
    {
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
        EnemyActor enemy,
        int damagePercent = 100)
    {
        DamageReceipt receipt = ApplyEnemyHit(message, combatant, tick, enemy, damagePercent);
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

    // The damage of a seeded attack scaled by a skill's percent. At 100 it is exactly attack.Damage, which
    // keeps the primary attack (and every golden) unchanged.
    private static int ScaleDamage(int damage, int damagePercent)
    {
        return damagePercent == 100 ? damage : Math.Max(1, damage * damagePercent / 100);
    }

    // The one path every enemy hit takes (primary attack and skills): seeded roll, damage, contributor
    // record, damage event, and on a kill the experience, persistence delete and loot.
    private DamageReceipt ApplyEnemyHit(
        PlayerActionMessage message,
        PlayerCombatant combatant,
        ulong tick,
        EnemyActor enemy,
        int damagePercent)
    {
        PlayerAttackResolution attack = ResolveSeededAttack(
            combatant,
            message,
            tick,
            Seed.FromInt(enemy.Combatant.Actor.Id));
        DamageReceipt receipt = enemy.Combatant.ApplyDamage(ScaleDamage(attack.Damage, damagePercent));
        if (receipt.AppliedAmount > 0)
        {
            enemy.RecordDamage(message.SessionId, receipt.AppliedAmount);
            _events.Publish(new DamageAppliedEvent(
                enemy.Combatant.Actor,
                receipt.AppliedAmount,
                attack.FloatingTextKind));
        }

        if (receipt.WasFatal)
        {
            Vector3 deathPosition = enemy.Position;
            PlayerSessionId lootWinner = AwardKillRewards(enemy, message.SessionId, deathPosition);
            _host.RecordEnemyKill(enemy.Contributors, enemy.EnemyKind, deathPosition);
            _host.DeletePersistedEnemy(enemy.Combatant.Actor.Id);
            _enemies.PruneInactive();
            _host.GrantLoot(lootWinner, enemy.EnemyKind, enemy.Combatant.Actor.Id, tick, deathPosition);
        }

        return receipt;
    }

    // PvP: the first player on the ray, within maxDistance, among the host's candidates.
    private CombatActorHandle? TryResolvePvpTarget(
        PlayerSessionId attacker,
        Vector3 origin,
        Vector3 direction,
        float maxDistance)
    {
        _pvpCandidates.Clear();
        _host.FillPvpTargets(attacker, _pvpCandidates);
        if (_pvpCandidates.Count == 0)
        {
            return null;
        }

        return AuthoritativeCombatTargetResolver.TryResolve(origin, direction, maxDistance, _pvpCandidates);
    }

    private DamageReceipt ApplyPlayerHit(
        PlayerActionMessage message,
        PlayerCombatant attacker,
        ulong tick,
        CombatActorHandle victim,
        int damagePercent)
    {
        PlayerAttackResolution attack = ResolveSeededAttack(
            attacker,
            message,
            tick,
            Seed.FromInt(victim.Id));
        // The victim's own ApplyDamage mitigates by armor and publishes ActorDiedEvent on death; the
        // respawn scan and replication take it from there. No experience, loot or gold for a player kill.
        int damage = ScaleDamage(attack.Damage, damagePercent);
        PlayerCombatant? victimCombatant = _host.ResolvePvpVictim(victim.Id);
        DamageReceipt receipt = victimCombatant != null
            ? victimCombatant.ApplyDamage(damage)
            : victim.Damageable.ApplyDamage(damage);
        if (receipt.AppliedAmount > 0)
        {
            _events.Publish(new DamageAppliedEvent(victim, receipt.AppliedAmount, attack.FloatingTextKind));
        }

        return receipt;
    }

    private AuthoritativeActionResult ResolvePlayerHit(
        PlayerActionMessage message,
        PlayerCombatant attacker,
        ulong tick,
        CombatActorHandle victim,
        int damagePercent)
    {
        DamageReceipt receipt = ApplyPlayerHit(message, attacker, tick, victim, damagePercent);
        return new AuthoritativeActionResult(
            message.SessionId.Value,
            message.ActionSequence,
            tick,
            Accepted: true,
            Payload: new CombatResolutionResult(
                victim.Id,
                victim.Kind,
                receipt.AppliedAmount,
                receipt.WasFatal,
                victim.Damageable.Health.CurrentHealth,
                victim.Damageable.Health.MaxHealth));
    }

    // M4 skills. The caller (the session runtime) has already validated the cast and spent mana and
    // cooldown; a cast that hits nothing is still accepted. Skills never damage cubes.
    public AuthoritativeActionResult ProcessSkillUse(
        PlayerActionMessage message,
        SkillDefinition skill,
        Vector3 rayOrigin,
        Vector3 rayDirection,
        PlayerCombatant combatant,
        ulong tick)
    {
        ArgumentNullException.ThrowIfNull(skill);
        int targetsHit = 0;
        int damageApplied = 0;
        int healthRestored = 0;
        switch (skill.Kind)
        {
            case SkillKind.SelfHeal:
                healthRestored = combatant.RestoreHealth(skill.HealAmount);
                break;
            case SkillKind.RangedBolt:
                ResolveBolt(message, skill, rayOrigin, rayDirection, combatant, tick, ref targetsHit, ref damageApplied);
                break;
            case SkillKind.MeleeArc:
                ResolveArc(message, skill, rayOrigin, rayDirection, combatant, tick, ref targetsHit, ref damageApplied);
                break;
        }

        return new AuthoritativeActionResult(
            message.SessionId.Value,
            message.ActionSequence,
            tick,
            Accepted: true,
            Payload: new AbilityUsedResult(skill.Key, targetsHit, damageApplied, healthRestored));
    }

    // True when a solid cube lies between the origin and the anchor (the last half cell excluded, so a
    // target standing against a wall is not hidden by it).
    private static bool IsBlockedByWall(Map map, Vector3 origin, Vector3 anchor)
    {
        Vector3 toTarget = anchor - origin;
        float distance = toTarget.Length;
        return map.TryGetFirstSolidCubeAlongRay(origin, toTarget, distance - 0.5f) != null;
    }

    private void ResolveBolt(
        PlayerActionMessage message,
        SkillDefinition skill,
        Vector3 origin,
        Vector3 direction,
        PlayerCombatant combatant,
        ulong tick,
        ref int targetsHit,
        ref int damageApplied)
    {
        Map map = _map();
        EnemyActor? enemy = _enemies.TryResolveHit(origin, direction, skill.Range);
        if (enemy != null && IsBlockedByWall(map, origin, enemy.Combatant.GetCombatAnchorWorldPosition()))
        {
            enemy = null;
        }

        if (enemy != null)
        {
            DamageReceipt receipt = ApplyEnemyHit(message, combatant, tick, enemy, skill.DamagePercent);
            targetsHit++;
            damageApplied += receipt.AppliedAmount;
            return;
        }

        if (!_host.TargetPolicy.PlayerVsPlayerEnabled)
        {
            return;
        }

        CombatActorHandle? player = TryResolvePvpTarget(message.SessionId, origin, direction, skill.Range);
        if (player.HasValue && !IsBlockedByWall(map, origin, player.Value.GetWorldAnchor()))
        {
            DamageReceipt receipt = ApplyPlayerHit(message, combatant, tick, player.Value, skill.DamagePercent);
            targetsHit++;
            damageApplied += receipt.AppliedAmount;
        }
    }

    private void ResolveArc(
        PlayerActionMessage message,
        SkillDefinition skill,
        Vector3 origin,
        Vector3 direction,
        PlayerCombatant combatant,
        ulong tick,
        ref int targetsHit,
        ref int damageApplied)
    {
        Vector3 horizontal = new(direction.X, 0f, direction.Z);
        if (horizontal.LengthSquared <= 0.0001f)
        {
            return;
        }

        horizontal.Normalize();
        float minDot = Math.Clamp(MathF.Cos(MathHelper.DegreesToRadians(skill.ArcDegrees * 0.5f)), -1f, 1f);
        Map map = _map();

        // Collect first: a kill prunes the registry. CollectInArc runs no occlusion test, so the cap has
        // to leave room for every blocked candidate ahead of a reachable one -- otherwise enemies behind a
        // wall use up the skill's targets, which is exactly what the cap exists to prevent. BH-XFER-5: a
        // fixed 16 was not that room. A crowd whose nearest 16 stood behind a cube hid every reachable
        // enemy, and the cast hit nothing with its mana and its 96-tick cooldown already spent. The
        // registry's own size is the only honest ceiling; the arc and range tests have already thrown out
        // everything else, and the PvP half below walks every candidate for the same reason.
        _arcScratch.Clear();
        _enemies.CollectInArc(
            origin,
            horizontal,
            skill.Range,
            minDot,
            Math.Max(skill.MaxTargets, _enemies.ActiveActors.Count),
            _arcScratch);
        for (int i = 0; i < _arcScratch.Count && targetsHit < skill.MaxTargets; i++)
        {
            EnemyActor enemy = _arcScratch[i];
            if (!enemy.IsActive || IsBlockedByWall(map, origin, enemy.Combatant.GetCombatAnchorWorldPosition()))
            {
                continue;
            }

            DamageReceipt receipt = ApplyEnemyHit(message, combatant, tick, enemy, skill.DamagePercent);
            targetsHit++;
            damageApplied += receipt.AppliedAmount;
        }

        _arcScratch.Clear();
        if (!_host.TargetPolicy.PlayerVsPlayerEnabled || targetsHit >= skill.MaxTargets)
        {
            return;
        }

        // Players in the same arc, nearest first, actor id breaking ties.
        _pvpCandidates.Clear();
        _host.FillPvpTargets(message.SessionId, _pvpCandidates);
        _pvpArcScratch.Clear();
        for (int i = 0; i < _pvpCandidates.Count; i++)
        {
            CombatTargetCandidate candidate = _pvpCandidates[i];
            if (!candidate.Actor.Damageable.Health.IsAlive
                || !EnemyRegistry.TryMeasureArc(origin, horizontal, skill.Range, minDot, candidate.Anchor, out float distanceSquared)
                || IsBlockedByWall(map, origin, candidate.Anchor))
            {
                continue;
            }

            int at = _pvpArcScratch.Count;
            _pvpArcScratch.Add((distanceSquared, candidate));
            while (at > 0 && ArcOrder(_pvpArcScratch[at], _pvpArcScratch[at - 1]) < 0)
            {
                (_pvpArcScratch[at], _pvpArcScratch[at - 1]) = (_pvpArcScratch[at - 1], _pvpArcScratch[at]);
                at--;
            }
        }

        for (int i = 0; i < _pvpArcScratch.Count && targetsHit < skill.MaxTargets; i++)
        {
            DamageReceipt receipt = ApplyPlayerHit(message, combatant, tick, _pvpArcScratch[i].Candidate.Actor, skill.DamagePercent);
            targetsHit++;
            damageApplied += receipt.AppliedAmount;
        }

        _pvpArcScratch.Clear();
        _pvpCandidates.Clear();
    }

    private static int ArcOrder(
        (float DistanceSquared, CombatTargetCandidate Candidate) a,
        (float DistanceSquared, CombatTargetCandidate Candidate) b)
    {
        int byDistance = a.DistanceSquared.CompareTo(b.DistanceSquared);
        return byDistance != 0 ? byDistance : a.Candidate.Actor.Id.CompareTo(b.Candidate.Actor.Id);
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
