using OpenTK.Mathematics;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Combat;
using Dream.Simulation.Enemies;
using Dream.Simulation.Players;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;

namespace Dream.Simulation.Runtime;

// Replication exports: server snapshot envelopes, accepted command sequences, starved ticks,
// mutation coalescing, actor snapshots and the CubeIdentified / world mutation export subscriptions.
public sealed partial class WorldSessionRuntime
{
    private readonly List<WorldMutationDelta> _pendingWorldMutations = [];
    private readonly List<DamageEventDelta> _pendingDamageEvents = [];

    public const ulong WorldChecksumIntervalTicks = 64;
    private ulong _nextWorldChecksumTick;

    // SCALE-2 actor delta chain: every ExportServerSnapshotEnvelope is the next frame, diffed
    // against the previous export's actors (which the previous broadcast carried), with a keyframe
    // every ActorDeltaContext.KeyframeIntervalTicks. A connection that did not get the previous
    // broadcast is sent the frame's keyframe twin instead (ServerConnection), so the chain is
    // shared by every connection and the payload is still encoded once. Resync envelopes stay off
    // the chain (they carry the full actor list).
    private readonly ActorDeltaChain _actorDeltaChain = new();

    public ServerSnapshotEnvelope ExportServerSnapshotEnvelope(bool includeWorldSnapshot = false)
    {
        IReadOnlyList<ActorSnapshot> actors = CaptureActorSnapshots();
        WorldSnapshot? worldSnapshot = includeWorldSnapshot
            ? ExportWorldSnapshot()
            : null;
        WorldMutationDelta[] mutations = CoalesceSameCellMoves(
            _pendingWorldMutations,
            _coalescedMutations,
            _coalescedLaterActors);

        ulong? checksum = null;
        if (CurrentServerTick >= _nextWorldChecksumTick)
        {
            checksum = WorldStateChecksum.Compute(Map);
            _nextWorldChecksumTick = CurrentServerTick + WorldChecksumIntervalTicks;
        }

        ServerSnapshotEnvelope envelope = new(
            CurrentServerTick,
            CaptureAcceptedCommandSequences(),
            actors,
            GetSessionInteractionMode(ResolvePrimaryControlledSessionId()),
            worldSnapshot,
            mutations,
            // The envelope's collections are wire payloads and stay fresh per snapshot.
            _pendingDamageEvents.Count == 0 ? Array.Empty<DamageEventDelta>() : _pendingDamageEvents.ToArray(),
            checksum,
            checksum.HasValue ? LastWorldMutationSequence : 0)
        {
            ActorDelta = _actorDeltaChain.Next(CurrentServerTick, actors)
        };

        _pendingWorldMutations.Clear();
        _pendingDamageEvents.Clear();
        return envelope;
    }

    // Full world for a client that reported a checksum mismatch. Pending deltas stay queued for
    // everyone; the world carries the sequence they are already reflected up to.
    public ServerSnapshotEnvelope ExportWorldResyncEnvelope()
    {
        return new ServerSnapshotEnvelope(
            CurrentServerTick,
            CaptureAcceptedCommandSequences(),
            CaptureActorSnapshots(),
            GetSessionInteractionMode(ResolvePrimaryControlledSessionId()),
            ExportWorldSnapshot(),
            Array.Empty<WorldMutationDelta>(),
            Array.Empty<DamageEventDelta>());
    }

    private Dictionary<string, ulong> CaptureAcceptedCommandSequences()
    {
        // Part of the envelope, so it stays a fresh dictionary, sized exactly so it never regrows.
        int acceptedCount = 0;
        foreach (ConnectedPlayer player in _sessions.Players)
        {
            if (player.LastAcceptedCommandSequence.HasValue)
            {
                acceptedCount++;
            }
        }

        Dictionary<string, ulong> acceptedSequences = new(acceptedCount);
        foreach (ConnectedPlayer player in _sessions.Players)
        {
            if (player.LastAcceptedCommandSequence.HasValue)
            {
                acceptedSequences[player.SessionId.Value] = player.LastAcceptedCommandSequence.Value;
            }
        }

        return acceptedSequences;
    }

    // The repeat debt a session's command inbox carries from before its current starved run
    // (BufferedPlayerCommandInbox.GetCarriedRepeatDebtTicks), set once by the host that drains
    // that inbox into this runtime; null (no inbox debt) for hosts that feed commands directly.
    public Func<PlayerSessionId, int>? CarriedRepeatDebtTicks { get; set; }

    // The ticks this seat simulated after the tick that consumed its acknowledged command (W9-2):
    // starved ticks that repeated that command, then idled (MaxCommandRepeatTicks). The seat's
    // counter is reset to 0 by the accepting tick and counts that tick too, hence the - 1. Added to
    // them, the inbox's carried repeat debt: after a starved run the backlog's newest tick is never
    // skipped, so the seat stays that many repeated ticks ahead of its ack until later frames are
    // skipped. A session with no accepted command has no ack to measure from, so it reports 0.
    private int CaptureStarvedTicks(PlayerSessionId sessionId)
    {
        if (!_sessions.TryGet(sessionId, out ConnectedPlayer? player)
            || player?.LastAcceptedCommandSequence is null)
        {
            return 0;
        }

        int ticksSinceCommand = _secondaryRemotePlayers.TryGetValue(sessionId, out RemoteSessionPlayerState? secondary)
            ? secondary.TicksSinceLastCommand
            : _primaryTicksSinceCommand;
        int carriedDebt = CarriedRepeatDebtTicks?.Invoke(sessionId) ?? 0;
        return Math.Max(0, ticksSinceCommand - 1) + Math.Max(0, carriedDebt);
    }

    // A resting or jostled cube emits a same-cell CubeMoved every tick. Such a move only changes
    // the exact center, so it can be dropped whenever a later mutation for the same cube follows
    // in the batch. Cross-cell moves are kept in order because they change cell occupancy.
    public static WorldMutationDelta[] CoalesceSameCellMoves(IReadOnlyList<WorldMutationDelta> mutations)
    {
        return CoalesceSameCellMoves(mutations, new List<WorldMutationDelta>(mutations.Count), []);
    }

    // The scratch collections are cleared and reused; only the returned array (the wire payload)
    // is new, and an empty batch returns Array.Empty.
    private static WorldMutationDelta[] CoalesceSameCellMoves(
        IReadOnlyList<WorldMutationDelta> mutations,
        List<WorldMutationDelta> kept,
        HashSet<int> laterActors)
    {
        if (mutations.Count == 0)
        {
            return Array.Empty<WorldMutationDelta>();
        }

        kept.Clear();
        laterActors.Clear();
        for (int index = mutations.Count - 1; index >= 0; index--)
        {
            WorldMutationDelta mutation = mutations[index];
            // A CubeIdentified carries no position, so it never supersedes a move.
            if (mutation.ActorId is int actorId && mutation.Kind != WorldMutationKind.CubeIdentified)
            {
                bool sameCellMove = mutation.Kind == WorldMutationKind.CubeMoved &&
                    mutation.TargetCellPosition == mutation.CellPosition;
                if (sameCellMove && laterActors.Contains(actorId))
                {
                    continue;
                }

                laterActors.Add(actorId);
            }

            kept.Add(mutation);
        }

        // kept is newest first; the batch keeps the original order.
        WorldMutationDelta[] coalesced = new WorldMutationDelta[kept.Count];
        for (int i = 0; i < coalesced.Length; i++)
        {
            coalesced[i] = kept[kept.Count - 1 - i];
        }

        kept.Clear();
        laterActors.Clear();
        return coalesced;
    }

    public IReadOnlyList<ActorSnapshot> CaptureActorSnapshots()
    {
        FillAuthoritativePlayerTargets(_snapshotPlayerTargets, spawnedAndAliveOnly: false);
        IReadOnlyList<EnemyActor> enemies = _enemies.ActiveActors;
        // The list is the snapshot's wire payload, so it is new each time, sized exactly.
        List<ActorSnapshot> actors = new(_snapshotPlayerTargets.Count + enemies.Count);

        foreach (IAuthoritativePlayerTarget playerTarget in _snapshotPlayerTargets)
        {
            string displayName = TryResolveDisplayName(playerTarget.SessionId);
            // The inventory is keyed by the character key, which differs from the display name for a
            // renamed or migrated character.
            string characterKey = TryResolveCharacterKey(playerTarget.SessionId);
            actors.Add(new ActorSnapshot(
                playerTarget.ActorId,
                CreatureKind.Player,
                playerTarget.SessionId.Value,
                playerTarget.Position,
                playerTarget.Facing,
                playerTarget.Combatant.Health.CurrentHealth,
                playerTarget.Combatant.Health.MaxHealth,
                playerTarget.IsAlive,
                playerTarget.InteractionMode,
                PlayerCombatStatsSnapshot.From(playerTarget.Combatant.BaseStats),
                displayName,
                new PlayerProgressionSnapshot(
                    playerTarget.Combatant.Level,
                    playerTarget.Combatant.Experience,
                    EvaluateSnapshotExperienceToNextLevel(playerTarget.Combatant)),
                StarvedTicks: CaptureStarvedTicks(playerTarget.SessionId),
                // T2f: derived from the inventory (cached, allocation-free), so not in the checksum.
                HeldLight: _inventory.ResolveEquippedHeldLight(characterKey),
                ModifierDigest: _inventory.ResolveModifierDigest(characterKey)));
        }

        _snapshotPlayerTargets.Clear();
        for (int i = 0; i < enemies.Count; i++)
        {
            EnemyActor actor = enemies[i];
            actors.Add(new ActorSnapshot(
                actor.Combatant.Actor.Id,
                CreatureKind.Enemy,
                null,
                actor.Position,
                actor.Forward,
                actor.Combatant.Health.CurrentHealth,
                actor.Combatant.Health.MaxHealth,
                actor.Combatant.Health.IsAlive,
                null,
                null,
                actor.DisplayName,
                null,
                actor.BehaviorState));
        }

        return actors;
    }

    // The formula service builds a variables dictionary per evaluation, and every snapshot asks
    // again for every player. The service is immutable, so the answer for an actor's unchanged
    // level and experience is reused.
    private int EvaluateSnapshotExperienceToNextLevel(PlayerCombatant combatant)
    {
        int actorId = combatant.Actor.Id;
        int level = combatant.Level;
        int experience = combatant.Experience;
        if (_experienceToNextLevelByActor.TryGetValue(actorId, out (int Level, int Experience, int Required) cached)
            && cached.Level == level
            && cached.Experience == experience)
        {
            return cached.Required;
        }

        int required = _combatFormulaService.EvaluateExperienceToNextLevel(level, experience);
        _experienceToNextLevelByActor[actorId] = (level, experience, required);
        return required;
    }

    // A cube got a replicated id outside a placement: most often a generated terrain cube on its
    // first hit, which clients only knew by cell. The delta carries the id in the same sequenced
    // stream as the hit's other world deltas (and before a fracture or destroy of that cube), so
    // the client keys its cube by the id before a later delta names the cube by it (CubeMoved,
    // CubeDestroyed).
    // The wire WorldStateChecksum has no ids (cells and materials only); ids are covered only by
    // the server-side SimulationStateChecksum, which clients never compute.
    private void OnCubeIdAssigned(Map map, Cube cube, int actorId)
    {
        if (_suppressCubeIdExports || !ReferenceEquals(map, Map))
        {
            return;
        }

        Vector3 cellPosition = map.TryGetCellForCube(cube, out GeometryCellKey cell)
            ? new Vector3(cell.X, cell.Y, cell.Z)
            : ToCellPosition(cube.Position);
        QueueWorldMutation(WorldMutationDelta.Identified(cellPosition, actorId));
    }

    // Sequences let clients skip deltas already folded into the join-time world snapshot.
    private void QueueWorldMutation(WorldMutationDelta mutation)
    {
        LastWorldMutationSequence++;
        _pendingWorldMutations.Add(mutation with { Sequence = LastWorldMutationSequence });
    }

    private void SubscribeReplicationExports()
    {
        _subscriptions.Add(_events.Subscribe<CubePlacedEvent>(evt =>
        {
            QueueWorldMutation(new WorldMutationDelta(
                WorldMutationKind.CubePlaced,
                evt.CellPosition,
                MaterialId: evt.MaterialId,
                ActorId: _worldObjects.TryGet(evt.Cube)?.Actor.Id));
        }));

        _subscriptions.Add(_events.Subscribe<CubeDestroyedEvent>(evt =>
        {
            QueueWorldMutation(new WorldMutationDelta(
                WorldMutationKind.CubeDestroyed,
                evt.CellPosition,
                ActorId: evt.ActorId));
        }));

        _subscriptions.Add(_events.Subscribe<CubeFracturedEvent>(evt =>
        {
            QueueWorldMutation(WorldMutationDelta.Fractured(
                evt.CellPosition,
                evt.MaterialId,
                evt.Pieces));
        }));

        _subscriptions.Add(_events.Subscribe<DamageAppliedEvent>(evt =>
        {
            _pendingDamageEvents.Add(new DamageEventDelta(
                evt.Actor.Id,
                evt.Actor.Kind,
                evt.Amount,
                ActorDied: !evt.Actor.Damageable.Health.IsAlive,
                evt.Actor.Damageable.Health.CurrentHealth,
                evt.Actor.Damageable.Health.MaxHealth));
        }));

        _subscriptions.Add(_events.Subscribe<ActorDiedEvent>(evt =>
        {
            _pendingDamageEvents.Add(new DamageEventDelta(
                evt.Actor.Id,
                evt.Actor.Kind,
                Amount: 0,
                ActorDied: true,
                evt.Actor.Damageable.Health.CurrentHealth,
                evt.Actor.Damageable.Health.MaxHealth));
        }));
    }

    private static Vector3 ToCellPosition(Vector3 position)
    {
        return new Vector3(
            MathF.Floor(position.X + 1e-6f),
            MathF.Floor(position.Y + 1e-6f),
            MathF.Floor(position.Z + 1e-6f));
    }
}
