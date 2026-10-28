using Dream.Multiplayer.Diagnostics;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Persistence;
using Dream.Simulation.Inventory;

namespace Dream.Simulation.Runtime;

// Inventory: inventory snapshots, equip actions, starter inventory, slot persistence and
// equipped-tool combat stats.
public sealed partial class WorldSessionRuntime
{
    // Loaded inventories only: a player's inventory is loaded when it joins (with the join
    // prefetch, see RestorePersistedPlayerState) and evicted with its profile once it has departed
    // (EvictCachedProfile), so these hold the active players' and the recently departed players'
    // inventories, not every inventory in the database. A name is a key of
    // _inventorySlotsByPlayer exactly when its inventory is loaded (possibly empty).
    private readonly Dictionary<long, PersistedItemInstance> _itemInstances = [];
    private readonly Dictionary<string, Dictionary<int, long>> _inventorySlotsByPlayer = new(StringComparer.OrdinalIgnoreCase);

    private long _nextItemInstanceId = 1;

    // Inventory write outcomes, by player name. The write queue drops a write that fails (other than
    // on a lock) and only counts it in FailedWrites, so an inventory whose writes may have failed is
    // the only current copy and must not be replaced by its rows. An entry is made when a loaded
    // inventory queues its first write and lives while that inventory is loaded, or while an
    // evicted inventory waits for its writes to be confirmed (see EvictPlayerInventory).
    private readonly Dictionary<string, InventoryWriteTracker> _inventoryWrites = new(StringComparer.OrdinalIgnoreCase);

    public PlayerInventorySnapshot? TryCaptureInventorySnapshot(PlayerSessionId sessionId)
    {
        if (!_sessions.TryGet(sessionId, out ConnectedPlayer? player) || player == null)
        {
            return null;
        }

        if (!EnsurePlayerInventory(player.DisplayName))
        {
            return null;
        }

        RefreshAuthoritativePlayerCombatStats(sessionId);
        return BuildInventorySnapshot(player.DisplayName);
    }

    private AuthoritativeActionResult ProcessInventoryEquipAction(
        PlayerActionMessage message,
        ConnectedPlayer player,
        InventoryEquipIntent intent)
    {
        if (intent.SlotIndex < 0 || intent.SlotIndex >= InventoryConstants.BackpackSlotCount)
        {
            return CreateRejectedActionResult(message, "Inventory equip rejected because the slot index is out of range.");
        }

        if (!EnsurePlayerInventory(player.DisplayName))
        {
            return CreateRejectedActionResult(message, "Inventory equip rejected because the inventory is still loading.");
        }

        if (!_inventorySlotsByPlayer.TryGetValue(player.DisplayName, out Dictionary<int, long>? slots)
            || !slots.TryGetValue(intent.SlotIndex, out long itemInstanceId)
            || !_itemInstances.ContainsKey(itemInstanceId))
        {
            return CreateRejectedActionResult(message, "Inventory equip rejected because the slot is empty.");
        }

        slots[InventoryConstants.EquippedSlotIndex] = itemInstanceId;
        PersistInventorySlot(player.DisplayName, InventoryConstants.EquippedSlotIndex, itemInstanceId);
        RefreshAuthoritativePlayerCombatStats(message.SessionId);

        return new AuthoritativeActionResult(
            message.SessionId.Value,
            message.ActionSequence,
            CurrentServerTick,
            Accepted: true,
            Payload: new InventoryChangedResult(BuildInventorySnapshot(player.DisplayName)));
    }

    // Startup loads no inventory, only the item id counter: new ids start above every persisted id,
    // including those of inventories that are never loaded this run.
    private void RestorePersistedInventoryState()
    {
        if (_worldPersistence == null)
        {
            return;
        }

        _nextItemInstanceId = Math.Max(_nextItemInstanceId, _worldPersistence.GetMaxItemInstanceId() + 1);
    }

    // Installs a player's loaded inventory unless one is already loaded for the name: a loaded
    // inventory is at least as new as any row a load read, because every change to it queues its
    // write when it happens and an evicted inventory's loaded prefetch is dropped (EvictCachedProfile).
    // An inventory evicted before its writes were confirmed, whose writes then failed (or are still
    // unconfirmed), is restored instead of the rows (TryRestoreEvictedInventory).
    private void ApplyLoadedInventory(string playerName, PersistedPlayerInventory? inventory)
    {
        if (TryRestoreEvictedInventory(playerName))
        {
            return;
        }

        if (inventory == null || _inventorySlotsByPlayer.ContainsKey(playerName))
        {
            return;
        }

        Dictionary<int, long> slots = GetOrCreateInventorySlots(playerName);
        foreach (PersistedItemInstance item in inventory.Items)
        {
            _itemInstances[item.ItemInstanceId] = item;
            _nextItemInstanceId = Math.Max(_nextItemInstanceId, item.ItemInstanceId + 1);
        }

        foreach (PersistedInventorySlot slot in inventory.Slots)
        {
            slots[slot.SlotIndex] = slot.ItemInstanceId;
            _nextItemInstanceId = Math.Max(_nextItemInstanceId, slot.ItemInstanceId + 1);
        }
    }

    // Loads a player's inventory that no join loaded (a direct caller of a runtime without profiles,
    // such as a tool or test): the queued writes are flushed first so the read is current. False
    // when they did not drain in time; the inventory then stays unloaded, so no starter inventory
    // is seeded over rows still being written.
    private bool TryLoadInventorySynchronously(string playerName)
    {
        if (_inventorySlotsByPlayer.ContainsKey(playerName) || TryRestoreEvictedInventory(playerName))
        {
            return true;
        }

        if (_worldPersistence == null)
        {
            return true;
        }

        NetworkDiagnosticsLog.Write("server.persistence.inventory-sync-read", $"player={playerName}");
        if (_writes != null && !_writes.Flush(TimeSpan.FromSeconds(5)))
        {
            NetworkDiagnosticsLog.Write("server.persistence.inventory-sync-read-timeout", $"player={playerName}");
            return false;
        }

        ApplyLoadedInventory(playerName, _worldPersistence.LoadPlayerInventory(playerName));
        return true;
    }

    // Drops a departed player's loaded inventory (EvictCachedProfile). Its changes were queued as
    // they happened, but the queue drops a write that fails and only counts it in FailedWrites, so:
    // - When a write failed since the inventory's first queued write (FailedWrites rose past the
    //   tracker's baseline; any failed write counts, as the queue does not say which one failed),
    //   the inventory stays loaded for the rest of the run and is logged: its rows may be stale and
    //   the in-memory copy is the only current one.
    // - When its writes may still be queued, it is moved out of the loaded maps into its tracker,
    //   with a read queued behind them that records whether any failed. A later load of the name
    //   runs after that read (the join prefetch is queued behind it; the synchronous load flushes
    //   the queue first) and restores the moved inventory unless the read found no failure.
    // - With no write queued since it was loaded, it is simply dropped.
    private void EvictPlayerInventory(string playerName)
    {
        ForgetConfirmedInventoryEvictions();
        if (!_inventorySlotsByPlayer.TryGetValue(playerName, out Dictionary<int, long>? slots))
        {
            return;
        }

        if (_writes != null && _inventoryWrites.TryGetValue(playerName, out InventoryWriteTracker? tracker))
        {
            PersistenceWriteQueue writes = _writes;
            long failedWrites = writes.FailedWrites;
            long baseline = tracker.FailedWritesBaseline;
            if (tracker.Retained || failedWrites > baseline)
            {
                tracker.Retained = true;
                NetworkDiagnosticsLog.Write(
                    "server.persistence.inventory-retained",
                    $"player={playerName} failedWrites={failedWrites} baseline={baseline}");
                return;
            }

            List<PersistedItemInstance> items = new(slots.Count);
            foreach (long itemInstanceId in slots.Values)
            {
                if (_itemInstances.Remove(itemInstanceId, out PersistedItemInstance item))
                {
                    items.Add(item);
                }
            }

            _inventorySlotsByPlayer.Remove(playerName);
            tracker.EvictedSlots = slots;
            tracker.EvictedItems = items;
            if (!writes.EnqueueRead(() => tracker.Settle(failed: writes.FailedWrites > baseline)))
            {
                tracker.Settle(failed: true);
            }

            if (tracker.Verdict == InventoryWriteVerdict.Clean)
            {
                // The inline queue ran the read already.
                _inventoryWrites.Remove(playerName);
            }

            return;
        }

        _inventoryWrites.Remove(playerName);
        _inventorySlotsByPlayer.Remove(playerName);
        foreach (long itemInstanceId in slots.Values)
        {
            _itemInstances.Remove(itemInstanceId);
        }
    }

    // Puts back an inventory that EvictPlayerInventory moved out before its writes were confirmed,
    // unless the confirming read found no failed write (the rows are then current, and the moved
    // copy is dropped). An unconfirmed verdict counts as a failure. A restored inventory stays
    // loaded for the rest of the run.
    private bool TryRestoreEvictedInventory(string playerName)
    {
        if (!_inventoryWrites.TryGetValue(playerName, out InventoryWriteTracker? tracker)
            || tracker.EvictedSlots == null)
        {
            return false;
        }

        if (tracker.Verdict == InventoryWriteVerdict.Clean || _inventorySlotsByPlayer.ContainsKey(playerName))
        {
            _inventoryWrites.Remove(playerName);
            return false;
        }

        _inventorySlotsByPlayer[playerName] = tracker.EvictedSlots;
        foreach (PersistedItemInstance item in tracker.EvictedItems!)
        {
            _itemInstances[item.ItemInstanceId] = item;
        }

        tracker.EvictedSlots = null;
        tracker.EvictedItems = null;
        tracker.Retained = true;
        NetworkDiagnosticsLog.Write(
            "server.persistence.inventory-restored",
            $"player={playerName} verdict={tracker.Verdict}");
        return true;
    }

    // Drops the moved-out inventories whose writes were all confirmed.
    private void ForgetConfirmedInventoryEvictions()
    {
        foreach (KeyValuePair<string, InventoryWriteTracker> entry in _inventoryWrites)
        {
            if (entry.Value.EvictedSlots != null && entry.Value.Verdict == InventoryWriteVerdict.Clean)
            {
                _inventoryWrites.Remove(entry.Key);
            }
        }
    }

    // Evicted inventories still held until their writes are confirmed.
    internal int UnconfirmedInventoryEvictionCount
    {
        get
        {
            int count = 0;
            foreach (InventoryWriteTracker tracker in _inventoryWrites.Values)
            {
                if (tracker.EvictedSlots != null)
                {
                    count++;
                }
            }

            return count;
        }
    }

    internal int LoadedInventoryCount => _inventorySlotsByPlayer.Count;

    internal int LoadedItemInstanceCount => _itemInstances.Count;

    internal long NextItemInstanceId => _nextItemInstanceId;

    // False when the inventory could not be loaded yet (see TryLoadInventorySynchronously).
    private bool EnsurePlayerInventory(string playerName)
    {
        if (!TryLoadInventorySynchronously(playerName))
        {
            return false;
        }

        Dictionary<int, long> slots = GetOrCreateInventorySlots(playerName);
        bool hasBackpackContent = slots.Keys.Any(slotIndex => slotIndex >= 0 && slotIndex < InventoryConstants.BackpackSlotCount);
        if (hasBackpackContent)
        {
            if (!slots.ContainsKey(InventoryConstants.EquippedSlotIndex))
            {
                KeyValuePair<int, long> firstOccupiedSlot = slots
                    .Where(entry => entry.Key >= 0 && entry.Key < InventoryConstants.BackpackSlotCount)
                    .OrderBy(entry => entry.Key)
                    .First();
                slots[InventoryConstants.EquippedSlotIndex] = firstOccupiedSlot.Value;
                PersistInventorySlot(playerName, InventoryConstants.EquippedSlotIndex, firstOccupiedSlot.Value);
            }

            return true;
        }

        SeedStarterInventory(playerName, slots);
        return true;
    }

    private void RefreshAuthoritativePlayerCombatStats(PlayerSessionId sessionId)
    {
        if (!TryResolveAuthoritativePlayerTarget(sessionId, out ConnectedPlayer? player, out IAuthoritativePlayerTarget? target)
            || player == null
            || target == null)
        {
            return;
        }

        target.Combatant.ApplyEquippedToolModifier(ResolveEquippedToolModifier(player.DisplayName));
    }

    private void SeedStarterInventory(string playerName, Dictionary<int, long> slots)
    {
        int slotIndex = 0;
        long? firstItemInstanceId = null;
        foreach (string itemKind in ToolCatalog.StarterItemKinds)
        {
            ToolDefinition definition = ToolCatalog.Resolve(itemKind);
            long itemInstanceId = _nextItemInstanceId++;
            firstItemInstanceId ??= itemInstanceId;
            PersistedItemInstance item = new(
                itemInstanceId,
                definition.ItemKind,
                definition.DisplayName,
                definition.VisualId,
                playerName,
                WorldPosition: null,
                State: "Inventory",
                StackCount: 1,
                CreatedUtc: DateTime.UtcNow,
                UpdatedUtc: DateTime.UtcNow);
            _itemInstances[item.ItemInstanceId] = item;
            slots[slotIndex] = itemInstanceId;
            NoteInventoryWriteQueued(playerName);
            EnqueueWorldWrite(world => world.SaveItemInstance(item));
            PersistInventorySlot(playerName, slotIndex, itemInstanceId);
            slotIndex++;
        }

        if (firstItemInstanceId.HasValue)
        {
            slots[InventoryConstants.EquippedSlotIndex] = firstItemInstanceId.Value;
            PersistInventorySlot(playerName, InventoryConstants.EquippedSlotIndex, firstItemInstanceId.Value);
        }
    }

    private PlayerInventorySnapshot BuildInventorySnapshot(string playerName)
    {
        Dictionary<int, long> slots = GetOrCreateInventorySlots(playerName);
        List<InventorySlotSnapshot> backpack = [];
        for (int slotIndex = 0; slotIndex < InventoryConstants.BackpackSlotCount; slotIndex++)
        {
            InventoryItemSnapshot? item = TryResolveInventoryItem(slots, slotIndex);
            bool equipped = slots.TryGetValue(InventoryConstants.EquippedSlotIndex, out long equippedItemId)
                && item.HasValue
                && item.Value.ItemInstanceId == equippedItemId;
            backpack.Add(new InventorySlotSnapshot(slotIndex, item, equipped));
        }

        InventoryItemSnapshot? equippedItem = TryResolveInventoryItem(slots, InventoryConstants.EquippedSlotIndex);
        return new PlayerInventorySnapshot(
            playerName,
            InventoryConstants.GridColumns,
            InventoryConstants.GridRows,
            backpack,
            new InventorySlotSnapshot(InventoryConstants.EquippedSlotIndex, equippedItem, equippedItem.HasValue));
    }

    private InventoryItemSnapshot? TryResolveInventoryItem(Dictionary<int, long> slots, int slotIndex)
    {
        if (!slots.TryGetValue(slotIndex, out long itemInstanceId)
            || !_itemInstances.TryGetValue(itemInstanceId, out PersistedItemInstance item))
        {
            return null;
        }

        ToolDefinition definition = ToolCatalog.Resolve(item.ItemKind);
        return new InventoryItemSnapshot(
            item.ItemInstanceId,
            item.ItemKind,
            item.DisplayName,
            item.VisualId,
            item.StackCount,
            definition.Description,
            definition.DetailLines);
    }

    private ToolStatModifier ResolveEquippedToolModifier(string playerName)
    {
        if (!_inventorySlotsByPlayer.TryGetValue(playerName, out Dictionary<int, long>? slots)
            || !slots.TryGetValue(InventoryConstants.EquippedSlotIndex, out long itemInstanceId)
            || !_itemInstances.TryGetValue(itemInstanceId, out PersistedItemInstance item))
        {
            return default;
        }

        return ToolCatalog.Resolve(item.ItemKind).StatModifier;
    }

    private Dictionary<int, long> GetOrCreateInventorySlots(string playerName)
    {
        if (!_inventorySlotsByPlayer.TryGetValue(playerName, out Dictionary<int, long>? slots))
        {
            slots = [];
            _inventorySlotsByPlayer[playerName] = slots;
        }

        return slots;
    }

    private void PersistInventorySlot(string playerName, int slotIndex, long itemInstanceId)
    {
        PersistedInventorySlot slot = new(
            playerName,
            slotIndex,
            itemInstanceId,
            DateTime.UtcNow);
        NoteInventoryWriteQueued(playerName);
        EnqueueWorldWrite(world => world.SaveInventorySlot(slot));
    }

    // Called before each inventory write is queued. The baseline is the queue's FailedWrites when
    // the first write since the inventory was loaded is queued, so every later failure counts.
    private void NoteInventoryWriteQueued(string playerName)
    {
        if (_writes == null || _worldPersistence == null || _inventoryWrites.ContainsKey(playerName))
        {
            return;
        }

        _inventoryWrites[playerName] = new InventoryWriteTracker(_writes.FailedWrites);
    }

    private enum InventoryWriteVerdict
    {
        Unknown = 0,
        Clean = 1,
        Failed = 2,
    }

    // Main-thread state, except the verdict: the confirming read sets it on the write worker.
    private sealed class InventoryWriteTracker(long failedWritesBaseline)
    {
        private int _verdict;

        public long FailedWritesBaseline { get; } = failedWritesBaseline;

        // Kept loaded through evictions for the rest of the run: a write may have failed.
        public bool Retained { get; set; }

        // An inventory moved out by EvictPlayerInventory while its writes are unconfirmed.
        public Dictionary<int, long>? EvictedSlots { get; set; }

        public List<PersistedItemInstance>? EvictedItems { get; set; }

        public InventoryWriteVerdict Verdict => (InventoryWriteVerdict)Volatile.Read(ref _verdict);

        public void Settle(bool failed)
        {
            Volatile.Write(ref _verdict, (int)(failed ? InventoryWriteVerdict.Failed : InventoryWriteVerdict.Clean));
        }
    }
}
