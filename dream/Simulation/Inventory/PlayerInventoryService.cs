using Dream.Multiplayer.Diagnostics;
using Dream.Simulation.Persistence;
using Dream.Simulation.World;

namespace Dream.Simulation.Inventory;

// Outcome of PlayerInventoryService.TryEquip; every value but Equipped is a rejection.
public enum InventoryEquipOutcome
{
    Equipped = 0,
    SlotOutOfRange = 1,
    StillLoading = 2,
    SlotEmpty = 3,
}

// The authoritative players' inventories: slots and item instances by player name, item-id
// allocation, equip, starter seeding, per-player load and eviction, the failed-write retention and
// the snapshot and equipped-tool stat queries. Main-thread state (the simulation tick), except the
// write trackers' verdicts, which the write worker settles. It knows players only by name; the
// runtime maps sessions to names and refreshes combat stats after a change.
//
// Its only dependencies are the world repository (reads) and the ordered write queue (writes and
// the confirming reads); both are null when the runtime has no persistence.
internal sealed class PlayerInventoryService
{
    private readonly IWorldPersistenceRepository? _worldPersistence;
    // Shared with cubes, rubble and enemies; the persistence bridge seeds it from the world's stored counter.
    private readonly ObjectIdAllocator _objectIds;
    private readonly PersistenceWriteQueue? _writes;

    // Loaded inventories only: a player's inventory is loaded when it joins (with the join
    // prefetch, see ApplyLoaded) and evicted with its profile once it has departed (Evict), so these
    // hold the active players' and the recently departed players' inventories, not every inventory
    // in the database. A name is a key of _slotsByPlayer exactly when its inventory is loaded
    // (possibly empty).
    private readonly Dictionary<long, PersistedItemInstance> _itemInstances = [];
    private readonly Dictionary<string, Dictionary<int, long>> _slotsByPlayer = new(StringComparer.OrdinalIgnoreCase);

    // The starter kit version each loaded inventory has received (see GrantStarterKit). A loaded
    // inventory without an entry has no recorded grant yet.
    private readonly Dictionary<string, int> _starterKitByPlayer = new(StringComparer.OrdinalIgnoreCase);

    // Inventory write outcomes, by player name. The write queue drops a write that fails (other than
    // on a lock) and only counts it in FailedWrites, so an inventory whose writes may have failed is
    // the only current copy and must not be replaced by its rows. An entry is made when a loaded
    // inventory queues its first write and lives while that inventory is loaded, or while an
    // evicted inventory waits for its writes to be confirmed (see Evict).
    private readonly Dictionary<string, InventoryWriteTracker> _writeTrackers = new(StringComparer.OrdinalIgnoreCase);

    public PlayerInventoryService(IWorldPersistenceRepository? worldPersistence, PersistenceWriteQueue? writes, ObjectIdAllocator objectIds)
    {
        _objectIds = objectIds ?? throw new ArgumentNullException(nameof(objectIds));
        _worldPersistence = worldPersistence;
        _writes = writes;
    }

    // The loaded inventories. slots by player name (item instance id by slot index; tests).
    internal IReadOnlyDictionary<string, Dictionary<int, long>> LoadedSlotsByPlayer => _slotsByPlayer;

    public int LoadedInventoryCount => _slotsByPlayer.Count;

    public int LoadedItemInstanceCount => _itemInstances.Count;

    public long NextItemInstanceId => _objectIds.Next;

    // Evicted inventories still held until their writes are confirmed.
    public int UnconfirmedEvictionCount
    {
        get
        {
            int count = 0;
            foreach (InventoryWriteTracker tracker in _writeTrackers.Values)
            {
                if (tracker.EvictedSlots != null)
                {
                    count++;
                }
            }

            return count;
        }
    }

    public bool IsLoaded(string playerName) => _slotsByPlayer.ContainsKey(playerName);

    // Installs a player's loaded inventory unless one is already loaded for the name: a loaded
    // inventory is at least as new as any row a load read, because every change to it queues its
    // write when it happens and an evicted inventory's loaded prefetch is dropped by the runtime.
    // An inventory evicted before its writes were confirmed, whose writes then failed (or are still
    // unconfirmed), is restored instead of the rows (TryRestoreEvicted).
    public void ApplyLoaded(string playerName, PersistedPlayerInventory? inventory)
    {
        if (TryRestoreEvicted(playerName))
        {
            return;
        }

        if (inventory == null || _slotsByPlayer.ContainsKey(playerName))
        {
            return;
        }

        Dictionary<int, long> slots = GetOrCreateSlots(playerName);
        if (inventory.StarterKitVersion is int starterKitVersion)
        {
            _starterKitByPlayer[playerName] = starterKitVersion;
        }

        // A hand-edited row can hold an id of 0 or below, which is not a valid ObjectId: the row still
        // loads, it just has nothing to reserve (the allocator never hands such an id out).
        int unreservableIds = 0;
        foreach (PersistedItemInstance item in inventory.Items)
        {
            _itemInstances[item.ItemInstanceId] = item;
            if (!TryReserve(item.ItemInstanceId))
            {
                unreservableIds++;
            }
        }

        foreach (PersistedInventorySlot slot in inventory.Slots)
        {
            slots[slot.SlotIndex] = slot.ItemInstanceId;
            if (!TryReserve(slot.ItemInstanceId))
            {
                unreservableIds++;
            }
        }

        if (unreservableIds > 0)
        {
            NetworkDiagnosticsLog.Write(
                "server.persistence.inventory-invalid-item-id",
                $"player={playerName} count={unreservableIds}");
        }
    }

    private bool TryReserve(long itemInstanceId)
    {
        if (itemInstanceId < 1)
        {
            return false;
        }

        _objectIds.Reserve(new ObjectId(itemInstanceId));
        return true;
    }

    // Makes sure the player's inventory is loaded and usable: loads it if no join did, grants the
    // starter kinds introduced after the kit version the player has received (the whole kit for a
    // new player; see GrantStarterKit) and equips the first occupied backpack slot when nothing is
    // equipped. False when the inventory could not be loaded yet (see TryLoadSynchronously).
    public bool Ensure(string playerName)
    {
        if (!TryLoadSynchronously(playerName))
        {
            return false;
        }

        Dictionary<int, long> slots = GetOrCreateSlots(playerName);
        bool hasBackpackContent = HasBackpackContent(slots);
        if (hasBackpackContent)
        {
            // An existing inventory keeps the item it had first; a new one equips the first kit item.
            EquipFirstOccupiedSlotIfNone(playerName, slots);
        }

        GrantStarterKit(playerName, slots, isTopUp: hasBackpackContent);
        if (!hasBackpackContent)
        {
            EquipFirstOccupiedSlotIfNone(playerName, slots);
        }

        return true;
    }

    // Equips the item in a backpack slot and queues the equipped slot's write. The caller refreshes
    // the player's combat stats on Equipped.
    public InventoryEquipOutcome TryEquip(string playerName, int slotIndex)
    {
        if (slotIndex < 0 || slotIndex >= InventoryConstants.BackpackSlotCount)
        {
            return InventoryEquipOutcome.SlotOutOfRange;
        }

        if (!Ensure(playerName))
        {
            return InventoryEquipOutcome.StillLoading;
        }

        if (!_slotsByPlayer.TryGetValue(playerName, out Dictionary<int, long>? slots)
            || !slots.TryGetValue(slotIndex, out long itemInstanceId)
            || !_itemInstances.ContainsKey(itemInstanceId))
        {
            return InventoryEquipOutcome.SlotEmpty;
        }

        slots[InventoryConstants.EquippedSlotIndex] = itemInstanceId;
        PersistSlot(playerName, InventoryConstants.EquippedSlotIndex, itemInstanceId);
        return InventoryEquipOutcome.Equipped;
    }

    // Drops a departed player's loaded inventory. Its changes were queued as they happened, but the
    // queue drops a write that fails and only counts it in FailedWrites, so:
    // - When a write failed since the inventory's first queued write (FailedWrites rose past the
    //   tracker's baseline; any failed write counts, as the queue does not say which one failed),
    //   the inventory stays loaded for the rest of the run and is logged: its rows may be stale and
    //   the in-memory copy is the only current one.
    // - When its writes may still be queued, it is moved out of the loaded maps into its tracker,
    //   with a read queued behind them that records whether any failed. A later load of the name
    //   runs after that read (the join prefetch is queued behind it; the synchronous load flushes
    //   the queue first) and restores the moved inventory unless the read found no failure.
    // - With no write queued since it was loaded, it is simply dropped.
    public void Evict(string playerName)
    {
        ForgetConfirmedEvictions();
        if (!_slotsByPlayer.TryGetValue(playerName, out Dictionary<int, long>? slots))
        {
            return;
        }

        if (_writes != null && _writeTrackers.TryGetValue(playerName, out InventoryWriteTracker? tracker))
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

            _slotsByPlayer.Remove(playerName);
            tracker.EvictedSlots = slots;
            tracker.EvictedItems = items;
            tracker.EvictedStarterKitVersion = _starterKitByPlayer.Remove(playerName, out int starterKitVersion)
                ? starterKitVersion
                : null;
            if (!writes.EnqueueRead(() => tracker.Settle(failed: writes.FailedWrites > baseline)))
            {
                tracker.Settle(failed: true);
            }

            if (tracker.Verdict == InventoryWriteVerdict.Clean)
            {
                // The inline queue ran the read already.
                _writeTrackers.Remove(playerName);
            }

            return;
        }

        _writeTrackers.Remove(playerName);
        _slotsByPlayer.Remove(playerName);
        _starterKitByPlayer.Remove(playerName);
        foreach (long itemInstanceId in slots.Values)
        {
            _itemInstances.Remove(itemInstanceId);
        }
    }

    public PlayerInventorySnapshot BuildSnapshot(string playerName)
    {
        Dictionary<int, long> slots = GetOrCreateSlots(playerName);
        List<InventorySlotSnapshot> backpack = [];
        for (int slotIndex = 0; slotIndex < InventoryConstants.BackpackSlotCount; slotIndex++)
        {
            InventoryItemSnapshot? item = TryResolveItem(slots, slotIndex);
            bool equipped = slots.TryGetValue(InventoryConstants.EquippedSlotIndex, out long equippedItemId)
                && item.HasValue
                && item.Value.ItemInstanceId == equippedItemId;
            backpack.Add(new InventorySlotSnapshot(slotIndex, item, equipped));
        }

        InventoryItemSnapshot? equippedItem = TryResolveItem(slots, InventoryConstants.EquippedSlotIndex);
        return new PlayerInventorySnapshot(
            playerName,
            InventoryConstants.GridColumns,
            InventoryConstants.GridRows,
            backpack,
            new InventorySlotSnapshot(InventoryConstants.EquippedSlotIndex, equippedItem, equippedItem.HasValue));
    }

    public ToolStatModifier ResolveEquippedToolModifier(string playerName)
    {
        if (!_slotsByPlayer.TryGetValue(playerName, out Dictionary<int, long>? slots)
            || !slots.TryGetValue(InventoryConstants.EquippedSlotIndex, out long itemInstanceId)
            || !_itemInstances.TryGetValue(itemInstanceId, out PersistedItemInstance item))
        {
            return default;
        }

        // A retired kind is inert: no modifier, no throw (the row stays as it is).
        return ToolCatalog.TryResolve(item.ItemKind, out ToolDefinition definition) ? definition.StatModifier : default;
    }

    // The light the equipped item casts; mirrors ResolveEquippedToolModifier and allocates nothing.
    public HeldLightKind ResolveEquippedHeldLight(string playerName)
    {
        if (!_slotsByPlayer.TryGetValue(playerName, out Dictionary<int, long>? slots)
            || !slots.TryGetValue(InventoryConstants.EquippedSlotIndex, out long itemInstanceId)
            || !_itemInstances.TryGetValue(itemInstanceId, out PersistedItemInstance item))
        {
            return HeldLightKind.None;
        }

        return ToolCatalog.TryResolve(item.ItemKind, out ToolDefinition definition) ? definition.HeldLight : HeldLightKind.None;
    }

    // Loads a player's inventory that no join loaded (a direct caller of a runtime without profiles,
    // such as a tool or test): the queued writes are flushed first so the read is current. False
    // when they did not drain in time; the inventory then stays unloaded, so no starter inventory
    // is seeded over rows still being written.
    private bool TryLoadSynchronously(string playerName)
    {
        if (_slotsByPlayer.ContainsKey(playerName) || TryRestoreEvicted(playerName))
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

        ApplyLoaded(playerName, _worldPersistence.LoadPlayerInventory(playerName));
        return true;
    }

    // Puts back an inventory that Evict moved out before its writes were confirmed, unless the
    // confirming read found no failed write (the rows are then current, and the moved copy is
    // dropped). An unconfirmed verdict counts as a failure. A restored inventory stays loaded for
    // the rest of the run.
    private bool TryRestoreEvicted(string playerName)
    {
        if (!_writeTrackers.TryGetValue(playerName, out InventoryWriteTracker? tracker)
            || tracker.EvictedSlots == null)
        {
            return false;
        }

        if (tracker.Verdict == InventoryWriteVerdict.Clean || _slotsByPlayer.ContainsKey(playerName))
        {
            _writeTrackers.Remove(playerName);
            return false;
        }

        _slotsByPlayer[playerName] = tracker.EvictedSlots;
        foreach (PersistedItemInstance item in tracker.EvictedItems!)
        {
            _itemInstances[item.ItemInstanceId] = item;
        }

        if (tracker.EvictedStarterKitVersion is int starterKitVersion)
        {
            _starterKitByPlayer[playerName] = starterKitVersion;
        }

        tracker.EvictedSlots = null;
        tracker.EvictedItems = null;
        tracker.EvictedStarterKitVersion = null;
        tracker.Retained = true;
        NetworkDiagnosticsLog.Write(
            "server.persistence.inventory-restored",
            $"player={playerName} verdict={tracker.Verdict}");
        return true;
    }

    // Drops the moved-out inventories whose writes were all confirmed.
    private void ForgetConfirmedEvictions()
    {
        foreach (KeyValuePair<string, InventoryWriteTracker> entry in _writeTrackers)
        {
            if (entry.Value.EvictedSlots != null && entry.Value.Verdict == InventoryWriteVerdict.Clean)
            {
                _writeTrackers.Remove(entry.Key);
            }
        }
    }

    // Grants the starter kinds introduced after the kit version the player has received, each into
    // the lowest free backpack slot, then records the new version. The received version is the
    // recorded one; without a record, an inventory with backpack items predates kit versions and is
    // taken to hold ToolCatalog.UnrecordedStarterKitVersion (the pre-T2 kit), and an empty one is a
    // new player's, which receives the whole kit. So a kind is granted once: an item the player
    // later gets rid of is not granted again, even into an empty backpack.
    // - A kind the backpack already holds is not granted again but counts as received (a pre-record
    //   top-up may have granted it).
    // - A full backpack receives nothing; the version recorded then stops below the first kind that
    //   did not fit, so it is granted once a slot is free.
    // - The grant record is queued after the item and slot writes, so they share the write queue's
    //   group commit. Writing it only when the version rises makes a second call write nothing.
    private void GrantStarterKit(string playerName, Dictionary<int, long> slots, bool isTopUp)
    {
        bool recorded = _starterKitByPlayer.TryGetValue(playerName, out int receivedVersion);
        if (!recorded)
        {
            receivedVersion = isTopUp ? ToolCatalog.UnrecordedStarterKitVersion : 0;
        }

        if (recorded && receivedVersion >= ToolCatalog.CurrentStarterKitVersion)
        {
            return;
        }

        int grantedVersion = ToolCatalog.CurrentStarterKitVersion;
        IReadOnlyList<StarterKitEntry> kit = ToolCatalog.StarterKit;
        for (int kindIndex = 0; kindIndex < kit.Count; kindIndex++)
        {
            StarterKitEntry entry = kit[kindIndex];
            if (entry.IntroducedVersion <= receivedVersion || BackpackHoldsKind(slots, entry.ItemKind))
            {
                continue;
            }

            int freeSlot = FindLowestFreeBackpackSlot(slots);
            if (freeSlot < 0)
            {
                grantedVersion = Math.Min(grantedVersion, entry.IntroducedVersion - 1);
                continue;
            }

            GrantItem(playerName, slots, entry.ItemKind, freeSlot);
            if (isTopUp)
            {
                NetworkDiagnosticsLog.Write(
                    "server.inventory.starter-top-up",
                    $"player={playerName} kind={entry.ItemKind} slot={freeSlot}");
            }
        }

        if (recorded && grantedVersion <= receivedVersion)
        {
            return;
        }

        _starterKitByPlayer[playerName] = grantedVersion;
        NoteWriteQueued(playerName);
        EnqueueWorldWrite(world => world.SaveStarterKitGrant(playerName, grantedVersion));
    }

    private static bool HasBackpackContent(Dictionary<int, long> slots)
    {
        foreach (int slotIndex in slots.Keys)
        {
            if (slotIndex >= 0 && slotIndex < InventoryConstants.BackpackSlotCount)
            {
                return true;
            }
        }

        return false;
    }

    // Equips the item of the lowest occupied backpack slot when nothing is equipped.
    private void EquipFirstOccupiedSlotIfNone(string playerName, Dictionary<int, long> slots)
    {
        if (slots.ContainsKey(InventoryConstants.EquippedSlotIndex))
        {
            return;
        }

        for (int slotIndex = 0; slotIndex < InventoryConstants.BackpackSlotCount; slotIndex++)
        {
            if (slots.TryGetValue(slotIndex, out long itemInstanceId))
            {
                slots[InventoryConstants.EquippedSlotIndex] = itemInstanceId;
                PersistSlot(playerName, InventoryConstants.EquippedSlotIndex, itemInstanceId);
                return;
            }
        }
    }

    private bool BackpackHoldsKind(Dictionary<int, long> slots, string itemKind)
    {
        for (int slotIndex = 0; slotIndex < InventoryConstants.BackpackSlotCount; slotIndex++)
        {
            if (slots.TryGetValue(slotIndex, out long itemInstanceId)
                && _itemInstances.TryGetValue(itemInstanceId, out PersistedItemInstance item)
                && string.Equals(item.ItemKind, itemKind, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static int FindLowestFreeBackpackSlot(Dictionary<int, long> slots)
    {
        for (int slotIndex = 0; slotIndex < InventoryConstants.BackpackSlotCount; slotIndex++)
        {
            if (!slots.ContainsKey(slotIndex))
            {
                return slotIndex;
            }
        }

        return -1;
    }

    // Creates a new item of the kind in the backpack slot and queues its item and slot writes.
    private long GrantItem(string playerName, Dictionary<int, long> slots, string itemKind, int slotIndex)
    {
        ToolDefinition definition = ToolCatalog.Resolve(itemKind);
        long itemInstanceId = _objectIds.Take().Value;
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
        NoteWriteQueued(playerName);
        EnqueueWorldWrite(world => world.SaveItemInstance(item));
        PersistSlot(playerName, slotIndex, itemInstanceId);
        return itemInstanceId;
    }

    private InventoryItemSnapshot? TryResolveItem(Dictionary<int, long> slots, int slotIndex)
    {
        if (!slots.TryGetValue(slotIndex, out long itemInstanceId)
            || !_itemInstances.TryGetValue(itemInstanceId, out PersistedItemInstance item))
        {
            return null;
        }

        // A kind the catalog no longer knows is shown as an inert "Unknown item": the row's own
        // fields are kept and nothing throws.
        bool known = ToolCatalog.TryResolve(item.ItemKind, out ToolDefinition definition);
        return new InventoryItemSnapshot(
            item.ItemInstanceId,
            item.ItemKind,
            item.DisplayName,
            item.VisualId,
            item.StackCount,
            known ? definition.Description : ToolCatalog.UnknownItemDescription,
            known ? definition.DetailLines : Array.Empty<string>());
    }

    private Dictionary<int, long> GetOrCreateSlots(string playerName)
    {
        if (!_slotsByPlayer.TryGetValue(playerName, out Dictionary<int, long>? slots))
        {
            slots = [];
            _slotsByPlayer[playerName] = slots;
        }

        return slots;
    }

    private void PersistSlot(string playerName, int slotIndex, long itemInstanceId)
    {
        PersistedInventorySlot slot = new(
            playerName,
            slotIndex,
            itemInstanceId,
            DateTime.UtcNow);
        NoteWriteQueued(playerName);
        EnqueueWorldWrite(world => world.SaveInventorySlot(slot));
    }

    private void EnqueueWorldWrite(Action<IWorldPersistenceRepository> write)
    {
        if (_worldPersistence == null || _writes == null)
        {
            return;
        }

        IWorldPersistenceRepository world = _worldPersistence;
        _writes.Enqueue(() => write(world));
    }

    // Called before each inventory write is queued. The baseline is the queue's FailedWrites when
    // the first write since the inventory was loaded is queued, so every later failure counts.
    private void NoteWriteQueued(string playerName)
    {
        if (_writes == null || _worldPersistence == null || _writeTrackers.ContainsKey(playerName))
        {
            return;
        }

        _writeTrackers[playerName] = new InventoryWriteTracker(_writes.FailedWrites);
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

        // An inventory moved out by Evict while its writes are unconfirmed.
        public Dictionary<int, long>? EvictedSlots { get; set; }

        public List<PersistedItemInstance>? EvictedItems { get; set; }

        // The moved-out inventory's recorded starter kit version, if any.
        public int? EvictedStarterKitVersion { get; set; }

        public InventoryWriteVerdict Verdict => (InventoryWriteVerdict)Volatile.Read(ref _verdict);

        public void Settle(bool failed)
        {
            Volatile.Write(ref _verdict, (int)(failed ? InventoryWriteVerdict.Failed : InventoryWriteVerdict.Clean));
        }
    }
}
