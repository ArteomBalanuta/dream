using System.Globalization;
using Dream.Simulation.Content;
using Dream.Simulation.Persistence;

namespace Dream.Simulation.Inventory;

// Quests (M6 workstream B): accept, abandon, kill progress and turn-in. The state lives here, next to the
// wallet, so it shares the failed-write gate (HasSeenFailedWrite), the eviction retention and the
// closure shape of the vendor: every accepted action changes memory and queues ONE closure.
//
// Kill counts are stored. Collect counts are never stored: they are read from the bag when asked for, and the
// items are consumed at turn-in. A reward is paid by the turn-in's own closure (the completion, the consumed
// items, the gold delta and the reward items together), never by the grant helpers that queue closures of
// their own, so a failed completion cannot leave the gold paid with the quest repeatable.
internal sealed partial class PlayerInventoryService
{
    internal const int QuestActive = 1;
    internal const int QuestCompleted = 2;

    // The most quest rows one player keeps (the log message holds as many).
    internal const int MaxQuestRecords = 32;

    private const int MaxStoredKillCount = ContentCatalog.MaxQuestCount;

    // One quest of one player. Kills holds one count per objective of the definition, in order (the slots of
    // Collect objectives stay 0).
    internal sealed class QuestRecord(string key, int state, int[] kills)
    {
        public string Key { get; } = key;

        public int State { get; set; } = state;

        public int[] Kills { get; } = kills;
    }

    private static readonly IReadOnlyCollection<QuestRecord> NoQuestRecords = [];

    // Bumped by every write queued for the player (accept, kill, turn-in, any bag change).
    internal long InventoryRevisionOf(string player) => _inventoryRevisions.GetValueOrDefault(player);

    // Bumped by every change to the player's quest records.
    internal long QuestRevisionOf(string player) => _questRevisions.GetValueOrDefault(player);

    internal IReadOnlyCollection<QuestRecord> QuestsOf(string player)
        => _questsByPlayer.TryGetValue(player, out Dictionary<string, QuestRecord>? records) ? records.Values : NoQuestRecords;

    internal bool TryGetQuestRecord(string player, string questKey, out QuestRecord record)
    {
        if (_questsByPlayer.TryGetValue(player, out Dictionary<string, QuestRecord>? records)
            && records.TryGetValue(questKey, out QuestRecord? found))
        {
            record = found;
            return true;
        }

        record = null!;
        return false;
    }

    // How many items of the kind the bag holds (anchors 0..63). Worn and held-back items do not count.
    internal int CountBagItems(string player, string itemKind)
    {
        if (!_slotsByPlayer.TryGetValue(player, out Dictionary<int, long>? slots))
        {
            return 0;
        }

        int count = 0;
        for (int anchor = 0; anchor < InventoryConstants.BackpackSlotCount; anchor++)
        {
            if (slots.TryGetValue(anchor, out long id)
                && _itemInstances.TryGetValue(id, out PersistedItemInstance item)
                && string.Equals(item.ItemKind, itemKind, StringComparison.OrdinalIgnoreCase))
            {
                count++;
            }
        }

        return count;
    }

    // Have of objective index: the stored kill count, or what the bag holds, never above Count.
    internal int ProgressOf(string player, QuestRecord record, QuestDefinition quest, int index)
    {
        QuestObjective objective = quest.Objectives[index];
        int have = record.State == QuestCompleted
            ? objective.Count
            : objective.Kind == QuestObjectiveKind.Kill
                ? (index < record.Kills.Length ? record.Kills[index] : 0)
                : CountBagItems(player, objective.Target);
        return Math.Clamp(have, 0, objective.Count);
    }

    internal bool ObjectivesMet(string player, QuestRecord record, QuestDefinition quest)
    {
        for (int i = 0; i < quest.Objectives.Count; i++)
        {
            if (ProgressOf(player, record, quest, i) < quest.Objectives[i].Count)
            {
                return false;
            }
        }

        return true;
    }

    // Takes the quest. QuestUnavailable when the player already has a row for it (active or completed: a
    // quest is done once) or the log is full; WriteFailed after an earlier failed write, because the rows may
    // then lack the quest and a later completion would throw.
    internal InventoryEquipOutcome TryAcceptQuest(string playerName, QuestDefinition quest)
    {
        if (!Ensure(playerName))
        {
            return InventoryEquipOutcome.StillLoading;
        }

        if (HasSeenFailedWrite(playerName))
        {
            return InventoryEquipOutcome.WriteFailed;
        }

        if (!_questsByPlayer.TryGetValue(playerName, out Dictionary<string, QuestRecord>? records))
        {
            records = new Dictionary<string, QuestRecord>(StringComparer.Ordinal);
            _questsByPlayer[playerName] = records;
        }

        if (records.ContainsKey(quest.Key) || records.Count >= MaxQuestRecords)
        {
            return InventoryEquipOutcome.QuestUnavailable;
        }

        if (!TrySpendAction())
        {
            return InventoryEquipOutcome.Throttled;
        }

        QuestRecord record = new(quest.Key, QuestActive, new int[quest.Objectives.Count]);
        records[quest.Key] = record;
        BumpQuestRevision(playerName);
        PersistedQuest row = ToRow(playerName, record);
        NoteWriteQueued(playerName);
        EnqueueWorldWrite(world => world.SaveQuestProgress(row));
        return InventoryEquipOutcome.Equipped;
    }

    // Drops an active quest (its kill counts go with it). A completed quest cannot be dropped.
    internal InventoryEquipOutcome TryAbandonQuest(string playerName, string questKey)
    {
        if (!Ensure(playerName))
        {
            return InventoryEquipOutcome.StillLoading;
        }

        if (HasSeenFailedWrite(playerName))
        {
            return InventoryEquipOutcome.WriteFailed;
        }

        if (!TryGetQuestRecord(playerName, questKey, out QuestRecord record) || record.State != QuestActive)
        {
            return InventoryEquipOutcome.QuestUnavailable;
        }

        if (!TrySpendAction())
        {
            return InventoryEquipOutcome.Throttled;
        }

        _questsByPlayer[playerName].Remove(questKey);
        BumpQuestRevision(playerName);
        NoteWriteQueued(playerName);
        EnqueueWorldWrite(world => world.DeleteQuest(playerName, questKey));
        return InventoryEquipOutcome.Equipped;
    }

    // Counts one kill of the enemy kind for every active quest of the player that wants it. Not a client
    // action, so not budgeted. True when any count moved.
    internal bool RecordKill(string playerName, ContentCatalog content, string enemyKind)
    {
        if (!_questsByPlayer.TryGetValue(playerName, out Dictionary<string, QuestRecord>? records) || records.Count == 0)
        {
            return false;
        }

        bool any = false;
        foreach (QuestRecord record in records.Values)
        {
            if (record.State != QuestActive || !content.TryGetQuest(record.Key, out QuestDefinition? quest))
            {
                continue;
            }

            bool moved = false;
            for (int i = 0; i < quest.Objectives.Count && i < record.Kills.Length; i++)
            {
                QuestObjective objective = quest.Objectives[i];
                if (objective.Kind == QuestObjectiveKind.Kill
                    && record.Kills[i] < objective.Count
                    && string.Equals(objective.Target, enemyKind, StringComparison.OrdinalIgnoreCase))
                {
                    record.Kills[i]++;
                    moved = true;
                }
            }

            if (moved)
            {
                any = true;
                PersistedQuest row = ToRow(playerName, record);
                NoteWriteQueued(playerName);
                EnqueueWorldWrite(world => world.SaveQuestProgress(row));
            }
        }

        if (any)
        {
            BumpQuestRevision(playerName);
        }

        return any;
    }

    // Hands the quest in: consumes the collect items, pays the gold and the reward items, and completes the
    // quest, all in ONE queued closure. The checks run in a fixed order and a rejection changes and queues
    // nothing: loading, the failed-write gate, the quest active with its objectives met (else
    // QuestUnavailable), the wallet room, the room for the reward items once the collected ones are gone
    // (BackpackFull), then the budget last. rewardedXp is the XP the caller grants, only on Equipped.
    //
    // The closure order is the guard order: CompleteQuest first (it throws unless the quest is still
    // active, so a second turn-in that raced a failed first one stops there), then the consumed items (the
    // held destroy throws on an item that was never persisted), the credit, then the reward rows.
    //
    // rewardWrite (BH-QUEST-2) is the caller's own durable part of the reward: the XP's profile write. It
    // is asked for only once every guard has passed, and the write it returns runs LAST inside this one
    // closure, so it commits with the completion or rolls back with it. Queued after the closure it was a
    // savepoint of its own, so a failed completion paid the XP and left the quest active in the rows -
    // and a restart allowed the same turn-in, and the same XP, again.
    internal InventoryEquipOutcome TryTurnInQuest(
        string playerName,
        QuestDefinition quest,
        out long rewardedXp,
        Func<Action?>? rewardWrite = null,
        ContentCatalog? content = null)
    {
        rewardedXp = 0;
        if (!Ensure(playerName))
        {
            return InventoryEquipOutcome.StillLoading;
        }

        if (HasSeenFailedWrite(playerName))
        {
            return InventoryEquipOutcome.WriteFailed;
        }

        if (!TryGetQuestRecord(playerName, quest.Key, out QuestRecord record)
            || record.State != QuestActive
            || !_slotsByPlayer.TryGetValue(playerName, out Dictionary<int, long>? slots))
        {
            return InventoryEquipOutcome.QuestUnavailable;
        }

        List<(int Anchor, long ItemInstanceId)> consumed = [];
        for (int i = 0; i < quest.Objectives.Count; i++)
        {
            QuestObjective objective = quest.Objectives[i];
            if (objective.Kind == QuestObjectiveKind.Kill)
            {
                if (i >= record.Kills.Length || record.Kills[i] < objective.Count)
                {
                    return InventoryEquipOutcome.QuestUnavailable;
                }
            }
            else if (!PickCollectItems(slots, objective, consumed))
            {
                return InventoryEquipOutcome.QuestUnavailable;
            }
        }

        QuestRewards rewards = quest.Rewards;
        long gold = GoldOf(playerName);
        if (rewards.Gold > InventoryConstants.MaxGold - gold)
        {
            return InventoryEquipOutcome.WalletFull;
        }

        // The room is checked on the bag as it will be: the consumed items are gone.
        Dictionary<int, long> remaining = new(slots);
        foreach ((int anchor, _) in consumed)
        {
            remaining.Remove(anchor);
        }

        BagBoard board = BuildBoard(remaining);
        List<(ItemDefinition Definition, int Slot)> placements = [];

        // BH-QUEST-5: through the catalog the quest came from, not ContentCatalog.Shipped. The offer
        // side reads the definitions through the runtime's own EnemyContent, so a reward item that only
        // the runtime's items.json has would never resolve here and every turn-in returned
        // QuestUnavailable with no diagnostic - the quest could never be handed in. The load-time check
        // (ParseQuestRewards) already validates the reward against that same file's items.
        ContentCatalog rewardSource = content ?? ContentCatalog.Shipped;
        foreach (QuestRewardItem reward in rewards.Items)
        {
            if (!rewardSource.TryGetItem(reward.ItemKind, out ItemDefinition? definition) || definition.Rolls != null)
            {
                return InventoryEquipOutcome.QuestUnavailable;
            }

            for (int n = 0; n < reward.Count; n++)
            {
                int fit = board.FirstFit(definition.Footprint);
                if (fit < 0)
                {
                    return InventoryEquipOutcome.BackpackFull;
                }

                board.TryPlace(fit, definition.Footprint);
                placements.Add((definition, fit));
            }
        }

        if (!TrySpendAction())
        {
            return InventoryEquipOutcome.Throttled;
        }

        // ---- memory ----
        record.State = QuestCompleted;
        BumpQuestRevision(playerName);
        foreach ((int anchor, long itemInstanceId) in consumed)
        {
            slots.Remove(anchor);
            if (!IsReferencedByOtherInventory(playerName, itemInstanceId))
            {
                _itemInstances.Remove(itemInstanceId);
            }
        }

        if (rewards.Gold > 0)
        {
            _goldByPlayer[playerName] = gold + rewards.Gold;
        }

        DateTime now = DateTime.UtcNow;
        List<PersistedItemInstance> newItems = new(placements.Count);
        List<PersistedInventorySlot> newSlots = new(placements.Count);
        foreach ((ItemDefinition definition, int slotIndex) in placements)
        {
            // The resolved definition's own fields, not ToolCatalog's: ToolCatalog is built from the
            // shipped items alone and Resolve THROWS on a kind it does not know, so an alternate
            // catalog's reward item would crash the turn-in. For a shipped kind the two are the same
            // projection (ToolCatalog.BuildDefinitions copies Key, DisplayName and VisualId).
            long itemInstanceId = _objectIds.Take().Value;
            PersistedItemInstance item = new(
                itemInstanceId,
                definition.Key,
                definition.DisplayName,
                definition.VisualId,
                playerName,
                WorldPosition: null,
                State: "Inventory",
                StackCount: 1,
                CreatedUtc: now,
                UpdatedUtc: now,
                Roll: null);
            _itemInstances[itemInstanceId] = item;
            slots[slotIndex] = itemInstanceId;
            newItems.Add(item);
            newSlots.Add(new PersistedInventorySlot(playerName, slotIndex, itemInstanceId, now));
        }

        // ---- the one closure ----
        string questKey = quest.Key;
        long credit = rewards.Gold;
        long[] consumedIds = [.. consumed.Select(entry => entry.ItemInstanceId)];
        Action? paidReward = rewardWrite?.Invoke();
        NoteWriteQueued(playerName);
        EnqueueWorldWrite(world =>
        {
            world.CompleteQuest(playerName, questKey, now);
            foreach (long itemInstanceId in consumedIds)
            {
                world.DestroyHeldInventoryItem(playerName, itemInstanceId);
            }

            if (credit > 0)
            {
                world.ApplyWalletDelta(playerName, credit);
            }

            foreach (PersistedItemInstance item in newItems)
            {
                world.SaveItemInstance(item);
            }

            foreach (PersistedInventorySlot slot in newSlots)
            {
                world.SaveInventorySlot(slot);
            }

            // Last, and inside this savepoint: the XP cannot land without everything above it.
            paidReward?.Invoke();
        });
        RefreshEquippedSummary(playerName);
        rewardedXp = rewards.Xp;
        return InventoryEquipOutcome.Equipped;
    }

    // Chooses Count bag items of the objective's kind (anchors only, never an item already chosen), plain
    // items before rolled ones, in anchor order. False when the bag holds fewer.
    private bool PickCollectItems(Dictionary<int, long> slots, QuestObjective objective, List<(int Anchor, long ItemInstanceId)> chosen)
    {
        int needed = objective.Count;
        for (int pass = 0; pass < 2 && needed > 0; pass++)
        {
            for (int anchor = 0; anchor < InventoryConstants.BackpackSlotCount && needed > 0; anchor++)
            {
                if (!slots.TryGetValue(anchor, out long id)
                    || !_itemInstances.TryGetValue(id, out PersistedItemInstance item)
                    || !string.Equals(item.ItemKind, objective.Target, StringComparison.OrdinalIgnoreCase)
                    || (item.Roll == null) != (pass == 0)
                    || chosen.Exists(entry => entry.ItemInstanceId == id))
                {
                    continue;
                }

                chosen.Add((anchor, id));
                needed--;
            }
        }

        return needed == 0;
    }

    private void BumpQuestRevision(string playerName)
        => _questRevisions[playerName] = _questRevisions.GetValueOrDefault(playerName) + 1;

    private static PersistedQuest ToRow(string playerName, QuestRecord record)
        => new(playerName, record.Key, record.State, FormatProgress(record.Kills), DateTime.UtcNow);

    private static string FormatProgress(int[] kills)
    {
        if (kills.Length == 0)
        {
            return string.Empty;
        }

        return string.Join(',', kills.Select(count => count.ToString(CultureInfo.InvariantCulture)));
    }

    // Reads the stored quest rows of a player's loaded inventory. Hostile rows are tolerated: an unknown state
    // is dropped, a duplicate key keeps the first, progress is parsed leniently and clamped to 0..99 per
    // entry and to the longest objective list. A key the catalog no longer knows is kept (inert).
    private void LoadQuests(string playerName, IReadOnlyList<PersistedQuest>? rows)
    {
        if (rows == null || rows.Count == 0)
        {
            return;
        }

        Dictionary<string, QuestRecord> records = new(StringComparer.Ordinal);
        foreach (PersistedQuest row in rows)
        {
            if ((row.State != QuestActive && row.State != QuestCompleted)
                || string.IsNullOrWhiteSpace(row.QuestKey)
                || records.Count >= MaxQuestRecords
                || records.ContainsKey(row.QuestKey))
            {
                continue;
            }

            int[] kills = new int[ContentCatalog.MaxQuestObjectives];
            string[] parts = (row.Progress ?? string.Empty).Split(',', ContentCatalog.MaxQuestObjectives + 1);
            for (int i = 0; i < parts.Length && i < kills.Length; i++)
            {
                if (int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out int value))
                {
                    kills[i] = Math.Clamp(value, 0, MaxStoredKillCount);
                }
            }

            records[row.QuestKey] = new QuestRecord(row.QuestKey, row.State, kills);
        }

        if (records.Count > 0)
        {
            _questsByPlayer[playerName] = records;
        }
    }
}
