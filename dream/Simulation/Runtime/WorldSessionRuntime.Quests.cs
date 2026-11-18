using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Dream.Multiplayer.Diagnostics;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Combat;
using Dream.Simulation.Content;
using Dream.Simulation.Enemies;
using Dream.Simulation.Inventory;
using Dream.Simulation.Players;
using OpenTK.Mathematics;

namespace Dream.Simulation.Runtime;

// Quests (M6 workstream B). The quest state lives in PlayerInventoryService (see
// PlayerInventoryService.Quests.cs); this side resolves the giver and the range, turns kills into progress,
// grants the XP of a turn-in and keeps each owner's ServerQuestLog current.
//
// The server keeps no dialogue session: accept and turn-in name the NPC and the quest, and each checks the
// range, the giver and the quest again. The definitions are read through EnemyContent, never
// ContentCatalog.Shipped, so a runtime with its own content has its own quests.
public sealed partial class WorldSessionRuntime
{
    // What the quest log message of a session was last built from. The player reference is compared too, so
    // a session id that comes back with a new seat starts over and is sent its log again.
    private sealed class QuestLogSync(ConnectedPlayer player)
    {
        public ConnectedPlayer Player { get; } = player;

        public long QuestRevision { get; set; } = -1;

        public long InventoryRevision { get; set; } = -1;
    }

    private readonly Dictionary<PlayerSessionId, QuestLogSync> _questLogSync = [];

    private const int MaxQuestTextChars = 128;

    private AuthoritativeActionResult ProcessQuestAcceptAction(
        PlayerActionMessage message,
        ConnectedPlayer player,
        QuestAcceptIntent intent)
    {
        InventoryEquipOutcome outcome = ResolveQuestGiver(player, intent.NpcActorId, intent.QuestKey, out QuestDefinition? quest);
        if (outcome == InventoryEquipOutcome.Equipped)
        {
            outcome = RunInventoryAction(
                player,
                (service, key, definition) => service.TryAcceptQuest(key, definition, EnemyContent),
                quest!);
        }

        return CompleteInventoryAction(message, player, outcome, "quest accept");
    }

    private AuthoritativeActionResult ProcessQuestTurnInAction(
        PlayerActionMessage message,
        ConnectedPlayer player,
        QuestTurnInIntent intent)
    {
        InventoryEquipOutcome outcome = ResolveQuestGiver(player, intent.NpcActorId, intent.QuestKey, out QuestDefinition? quest);
        if (outcome == InventoryEquipOutcome.Equipped)
        {
            // BH-QUEST-2: the XP is part of the reward, so its profile write goes INSIDE the turn-in's
            // own closure. The turn-in asks for it only once every guard has passed, so it is still
            // granted only for an accepted turn-in, and never for a refused or failed one.
            PlayerSessionId session = message.SessionId;
            _pendingQuestXpGrant = null;
            outcome = RunInventoryAction(
                player,
                (service, key, definition) => service.TryTurnInQuest(
                    key,
                    definition,
                    out _,
                    () => GrantQuestExperience(session, definition.Rewards.Xp),
                    EnemyContent),
                quest!);
            QuestXpGrant? grant = _pendingQuestXpGrant;
            _pendingQuestXpGrant = null;
            if (outcome == InventoryEquipOutcome.Equipped)
            {
                MarkInventoryPush(message.SessionId);
                if (grant != null)
                {
                    WatchQuestExperience(grant);
                }
            }
            else if (grant != null)
            {
                // The turn-in asks for the reward write only after its guards have passed, so this is
                // belt and braces: a refusal that still took the XP puts it straight back, here on the
                // tick thread.
                _questXpRollbacks.Enqueue(grant);
            }
        }

        return CompleteInventoryAction(message, player, outcome, "quest turn-in");
    }

    private AuthoritativeActionResult ProcessQuestAbandonAction(
        PlayerActionMessage message,
        ConnectedPlayer player,
        QuestAbandonIntent intent)
    {
        InventoryEquipOutcome outcome = RunInventoryAction(
            player,
            static (service, key, questKey) => service.TryAbandonQuest(key, questKey),
            intent.QuestKey ?? string.Empty);
        return CompleteInventoryAction(message, player, outcome, "quest abandon");
    }

    // The NPC must be known and in range, and the quest must exist and be given by this NPC.
    private InventoryEquipOutcome ResolveQuestGiver(ConnectedPlayer player, int npcActorId, string? questKey, out QuestDefinition? quest)
    {
        quest = null;
        InventoryEquipOutcome outcome = TryResolveVendor(player, npcActorId, out SpawnedNpc npc);
        if (outcome != InventoryEquipOutcome.Equipped)
        {
            return outcome;
        }

        if (questKey == null
            || !EnemyContent.TryGetQuest(questKey, out QuestDefinition? found)
            || !string.Equals(found.Giver, npc.Definition.Key, StringComparison.OrdinalIgnoreCase)
            || !found.ExistsOn(Map.Width))
        {
            return InventoryEquipOutcome.QuestUnavailable;
        }

        quest = found;
        return InventoryEquipOutcome.Equipped;
    }

    // Whether the NPC gives quests; then NpcInteractIntent is answered with BuildNpcDialogue.
    private bool HasQuestsFor(SpawnedNpc npc) => EnemyContent.QuestsFor(npc.Definition.Key).Count > 0;

    private AuthoritativeActionResult BuildNpcDialogue(PlayerActionMessage message, ConnectedPlayer player, SpawnedNpc npc)
    {
        string key = player.CharacterKey.Value;
        if (!_inventory.Ensure(key))
        {
            return CompleteInventoryAction(message, player, InventoryEquipOutcome.StillLoading, "interact");
        }

        IReadOnlyList<QuestDefinition> quests = EnemyContent.QuestsFor(npc.Definition.Key);
        List<QuestOfferLine> lines = new(quests.Count);
        foreach (QuestDefinition quest in quests)
        {
            if (!quest.ExistsOn(Map.Width))
            {
                continue;
            }

            QuestOfferState state = QuestOfferState.Available;
            if (_inventory.TryGetQuestRecord(key, quest.Key, out PlayerInventoryService.QuestRecord? record))
            {
                state = record.State == PlayerInventoryService.QuestCompleted
                    ? QuestOfferState.Completed
                    : _inventory.ObjectivesMet(key, record, quest)
                        ? QuestOfferState.ReadyToTurnIn
                        : QuestOfferState.InProgress;
            }

            lines.Add(new QuestOfferLine(
                quest.Key,
                Clip(quest.Title, MaxQuestTextChars),
                Clip(quest.Description, ContentCatalog.MaxDescriptionLength),
                Clip(DescribeRewards(quest.Rewards), MaxQuestTextChars),
                state));
        }

        return new AuthoritativeActionResult(
            message.SessionId.Value,
            message.ActionSequence,
            CurrentServerTick,
            Accepted: true,
            Payload: new NpcDialogueResult(npc.ActorId, npc.Definition.DisplayName, npc.Definition.Greeting, lines));
    }

    // Called once per kill, before the loot, with everyone who damaged the enemy. Credit goes to every
    // contributor still present, and to their party mates near the death (the same people who share the XP).
    private void RecordEnemyKill(ReadOnlySpan<EnemyDamageContribution> contributors, string enemyKind, Vector3 at)
    {
        if (!HasActiveQuestAnywhere())
        {
            return;
        }

        List<PlayerSessionId> credited = [];
        List<PlayerSessionId> mates = [];
        for (int i = 0; i < contributors.Length; i++)
        {
            PlayerSessionId session = contributors[i].SessionId;
            if (!TryResolveAuthoritativePlayerTarget(session, out _, out _))
            {
                continue;
            }

            if (!credited.Contains(session))
            {
                credited.Add(session);
            }

            mates.Clear();
            if (TryGetNearbyPartyMembers(session, at, mates))
            {
                foreach (PlayerSessionId mate in mates)
                {
                    if (!credited.Contains(mate))
                    {
                        credited.Add(mate);
                    }
                }
            }
        }

        // BH-DEATH-1: the list above dedupes by SESSION, but the credit below is by CHARACTER key - the
        // key RecordKill's own records are held under (PlayerInventoryService._questsByPlayer, ordinal).
        // Two live seats of one character (a returning session can be seated beside a second session that
        // took its name while it was away) therefore counted one death twice. Dedupe on the key that is
        // actually credited, so one character takes at most one kill from one death however many seats it
        // holds. Ordinal and in contributor order, so the credit stays deterministic.
        List<string> creditedKeys = [];
        foreach (PlayerSessionId session in credited)
        {
            if (TryResolveAuthoritativePlayerTarget(session, out ConnectedPlayer? who, out _) && who != null)
            {
                string key = who.CharacterKey.Value;
                if (creditedKeys.Contains(key, StringComparer.Ordinal))
                {
                    continue;
                }

                creditedKeys.Add(key);
                _inventory.RecordKill(key, EnemyContent, enemyKind);
            }
        }
    }

    // False when the content has no quests at all, so a world without them does no work per kill.
    private bool HasActiveQuestAnywhere() => EnemyContent.Quests.Count > 0;

    // XP of a quest through the combatant, like a kill's: level up while the bar is full, then save the
    // profile. Called from the turn-in while it builds its one closure (BH-QUEST-2): the progression is
    // applied in memory here and the profile write is RETURNED, so the turn-in runs it inside that
    // closure. It used to be queued after the closure, as a savepoint of its own, which let a failed
    // completion pay the XP durably and leave the quest turn-in-able again after a restart. Null when
    // there is nothing to grant or no profile to save.
    private Action? GrantQuestExperience(PlayerSessionId session, long xp)
    {
        if (xp <= 0
            || !TryResolveAuthoritativePlayerTarget(session, out ConnectedPlayer? who, out IAuthoritativePlayerTarget? target)
            || target == null
            || who == null)
        {
            return null;
        }

        PlayerCombatant combatant = target.Combatant;
        int beforeLevel = combatant.Level;
        int beforeExperience = combatant.Experience;
        int level = combatant.Level;
        long experience = combatant.Experience + xp;
        int required = _combatFormulaService.EvaluateExperienceToNextLevel(level, (int)Math.Min(experience, int.MaxValue));
        while (experience >= required && required > 0)
        {
            experience -= required;
            level++;
            required = _combatFormulaService.EvaluateExperienceToNextLevel(level, (int)Math.Min(experience, int.MaxValue));
        }

        int grantedExperience = (int)Math.Min(experience, int.MaxValue);
        combatant.ApplyProgression(level, grantedExperience);
        Action? write = PrepareProfileWrite(session, combatant);
        if (write == null)
        {
            // No profile to save (a local or unrestored seat): nothing durable rides with the closure,
            // so there is nothing to roll back either, and the behaviour is what it has always been.
            return null;
        }

        // BH-REV2-3: the progression above and the profile PrepareProfileWrite cached are in memory NOW,
        // while the durable write only runs if the turn-in's closure commits. Remember enough to put both
        // back, and mark the grant written from inside the closure, after the repository call returned.
        QuestXpGrant grant = new(session, who.CharacterKey.Value, beforeLevel, beforeExperience, level, grantedExperience);
        _pendingQuestXpGrant = grant;
        return () =>
        {
            write();
            grant.Written = true;
        };
    }

    // What one turn-in's XP changed, so it can be undone when the turn-in's closure does not commit.
    private sealed class QuestXpGrant(
        PlayerSessionId session,
        string characterKey,
        int beforeLevel,
        int beforeExperience,
        int grantedLevel,
        int grantedExperience)
    {
        public PlayerSessionId Session { get; } = session;

        public string CharacterKey { get; } = characterKey;

        public int BeforeLevel { get; } = beforeLevel;

        public int BeforeExperience { get; } = beforeExperience;

        public int GrantedLevel { get; } = grantedLevel;

        public int GrantedExperience { get; } = grantedExperience;

        // Set on the persistence worker inside the turn-in's closure; read there too (the check read runs
        // on the same worker, after it), so the volatile write is all the ordering it needs.
        public volatile bool Written;
    }

    // The grant of the turn-in being processed: GrantQuestExperience is called from inside TryTurnInQuest,
    // which has no way to hand it back, and the action path is serial on the tick thread.
    private QuestXpGrant? _pendingQuestXpGrant;

    // Grants whose closure did not commit, applied on the tick thread by StepQuestLogs. FIFO, so the
    // order never depends on a dictionary's.
    private readonly ConcurrentQueue<QuestXpGrant> _questXpRollbacks = new();

    // The XP's profile write rides inside the turn-in's one closure (BH-QUEST-2), so a rolled-back
    // savepoint leaves the quest row active while the combatant and the cached profile already carry the
    // XP -- and the next unrelated profile save (DetachRemoteSession -> PersistPlayerProfile, which reads
    // GetCachedProfile) would make it durable, so a rejoin could hand the same quest in again for the same
    // XP (BH-REV2-3). This read is queued AFTER the closure: it runs on the persistence worker once the
    // closure has run or failed, reads no row and no clock, and only ever hands the tick thread a rollback.
    // Bounded: one read per paid turn-in, and a queue that refuses it rolls the grant back at once.
    private void WatchQuestExperience(QuestXpGrant grant)
    {
        if (PersistenceWrites is not { } writes || !writes.EnqueueRead(() =>
            {
                if (!grant.Written)
                {
                    _questXpRollbacks.Enqueue(grant);
                }
            }))
        {
            if (!grant.Written)
            {
                _questXpRollbacks.Enqueue(grant);
            }
        }
    }

    // Puts back the XP of a turn-in whose closure did not commit. Under the tick lock (StepQuestLogs).
    private void ApplyQuestExperienceRollbacks()
    {
        while (_questXpRollbacks.TryDequeue(out QuestXpGrant? grant))
        {
            // Only while the progression is still exactly what the grant set: XP earned between the
            // turn-in and this rollback is never erased.
            if (TryResolveAuthoritativePlayerTarget(grant.Session, out _, out IAuthoritativePlayerTarget? target)
                && target != null
                && target.Combatant.Level == grant.GrantedLevel
                && target.Combatant.Experience == grant.GrantedExperience)
            {
                target.Combatant.ApplyProgression(grant.BeforeLevel, grant.BeforeExperience);
                // Re-caches the profile without the XP and saves it, so neither the cache nor the row
                // can carry XP for a quest that stayed active.
                PersistPlayerProfile(grant.Session, target.Combatant);
                NetworkDiagnosticsLog.Write(
                    "server.quest.xp-rolled-back",
                    $"character={grant.CharacterKey} level={grant.BeforeLevel} experience={grant.BeforeExperience}");
                continue;
            }

            // The seat has gone (or moved on): the cached profile is what a later save would write, so
            // the XP comes out of there instead, when it is still exactly what the grant put in.
            bool reverted = _persistence.TryRollBackProfileExperience(
                grant.CharacterKey, grant.GrantedLevel, grant.GrantedExperience, grant.BeforeLevel, grant.BeforeExperience);
            NetworkDiagnosticsLog.Write(
                "server.quest.xp-rolled-back",
                $"character={grant.CharacterKey} seat=gone cache={(reverted ? "reverted" : "moved-on")}");
        }
    }

    // Once a tick (StepMultiplayerSystems): sends the quest log of a player when it was never sent for this
    // seat or when the quests or the inventory changed since (a collect count moves with the bag). Two
    // revision reads per spawned remote player when nothing changed.
    private void StepQuestLogs(ulong tick)
    {
        if (!_questXpRollbacks.IsEmpty)
        {
            ApplyQuestExperienceRollbacks();
        }

        if (EnemyContent.Quests.Count == 0 && _questLogSync.Count == 0)
        {
            return;
        }

        foreach (ConnectedPlayer player in _sessions.Players)
        {
            if (player.IsLocal || !player.IsSpawned)
            {
                continue;
            }

            string key = player.CharacterKey.Value;
            if (!_inventory.IsLoaded(key))
            {
                continue;
            }

            if (!_questLogSync.TryGetValue(player.SessionId, out QuestLogSync? sync) || !ReferenceEquals(sync.Player, player))
            {
                sync = new QuestLogSync(player);
                _questLogSync[player.SessionId] = sync;
            }

            long questRevision = _inventory.QuestRevisionOf(key);
            long inventoryRevision = _inventory.InventoryRevisionOf(key);
            if (questRevision == sync.QuestRevision && inventoryRevision == sync.InventoryRevision)
            {
                continue;
            }

            sync.QuestRevision = questRevision;
            sync.InventoryRevision = inventoryRevision;
            QueueSessionMessage(player.SessionId, BuildQuestLog(key));
        }

        if ((tick & 0xFF) == 0 && _questLogSync.Count > 0)
        {
            PruneQuestLogSync();
        }
    }

    private void PruneQuestLogSync()
    {
        List<PlayerSessionId>? gone = null;
        foreach (KeyValuePair<PlayerSessionId, QuestLogSync> entry in _questLogSync)
        {
            if (!_sessions.TryGet(entry.Key, out ConnectedPlayer? current) || !ReferenceEquals(current, entry.Value.Player))
            {
                (gone ??= []).Add(entry.Key);
            }
        }

        if (gone != null)
        {
            foreach (PlayerSessionId session in gone)
            {
                _questLogSync.Remove(session);
            }
        }
    }

    private ServerQuestLog BuildQuestLog(string characterKey)
    {
        List<PlayerInventoryService.QuestRecord> records = [.. _inventory.QuestsOf(characterKey)];
        records.Sort(static (left, right) => string.CompareOrdinal(left.Key, right.Key));
        List<QuestLogEntry> entries = new(Math.Min(records.Count, ServerQuestLog.MaxEntries));
        foreach (PlayerInventoryService.QuestRecord record in records)
        {
            if (entries.Count >= ServerQuestLog.MaxEntries)
            {
                break;
            }

            // A quest the content no longer has stays in the rows, unseen.
            if (!EnemyContent.TryGetQuest(record.Key, out QuestDefinition? quest))
            {
                continue;
            }

            List<QuestObjectiveLine> objectives = new(quest.Objectives.Count);
            for (int i = 0; i < quest.Objectives.Count; i++)
            {
                objectives.Add(new QuestObjectiveLine(
                    DescribeObjective(quest.Objectives[i]),
                    _inventory.ProgressOf(characterKey, record, quest, i),
                    quest.Objectives[i].Count));
            }

            entries.Add(new QuestLogEntry(
                quest.Key,
                Clip(quest.Title, MaxQuestTextChars),
                record.State == PlayerInventoryService.QuestCompleted ? QuestState.Completed : QuestState.Active,
                objectives));
        }

        return new ServerQuestLog(entries);
    }

    private string DescribeObjective(QuestObjective objective)
    {
        string name = objective.Target;
        if (objective.Kind == QuestObjectiveKind.Kill)
        {
            if (EnemyContent.TryGetEnemy(objective.Target, out EnemyTemplate? template))
            {
                name = template.DisplayName;
            }

            return Clip("Slay " + name, MaxQuestTextChars);
        }

        if (ContentCatalog.Shipped.TryGetItem(objective.Target, out ItemDefinition? item))
        {
            name = item.DisplayName;
        }

        return Clip("Bring " + name, MaxQuestTextChars);
    }

    private static string DescribeRewards(QuestRewards rewards)
    {
        CultureInfo c = CultureInfo.InvariantCulture;
        StringBuilder text = new();
        if (rewards.Xp > 0)
        {
            text.Append(rewards.Xp.ToString(c)).Append(" XP");
        }

        if (rewards.Gold > 0)
        {
            AppendSeparator(text);
            text.Append(rewards.Gold.ToString(c)).Append(" gold");
        }

        foreach (QuestRewardItem reward in rewards.Items)
        {
            AppendSeparator(text);
            string name = ContentCatalog.Shipped.TryGetItem(reward.ItemKind, out ItemDefinition? item) ? item.DisplayName : reward.ItemKind;
            text.Append(name);
            if (reward.Count > 1)
            {
                text.Append(" x").Append(reward.Count.ToString(c));
            }
        }

        return text.ToString();
    }

    private static void AppendSeparator(StringBuilder text)
    {
        if (text.Length > 0)
        {
            text.Append(", ");
        }
    }

    private static string Clip(string value, int maxChars)
        => value.Length <= maxChars ? value : value[..maxChars];
}
