using Dream.Multiplayer.Diagnostics;
using Dream.Simulation.Content;
using Dream.Simulation.Determinism;
using Dream.Simulation.Persistence;
using Dream.Simulation.Stats;
using Dream.Simulation.World;

namespace Dream.Simulation.Inventory;

// Outcome of PlayerInventoryService.TryEquip; every value but Equipped is a rejection.
public enum InventoryEquipOutcome
{
    Equipped = 0,
    SlotOutOfRange = 1,
    StillLoading = 2,
    SlotEmpty = 3,
    TargetOutOfRange = 4,
    NotEquippable = 5,
    BackpackFull = 6,
    ItemEquipped = 7,
    ItemMismatch = 8,
    Throttled = 9,
    // The item does not fit there: out of the bag's bounds, over two or more items, nowhere for a
    // swapped item to go (BagGrid.PlanMove Blocked), or an unequip target cell that is not free.
    NoRoom = 10,
}

// The authoritative players' inventories: slots and item instances by player name, item-id
// allocation, equip, starter seeding, per-player load and eviction, the failed-write retention and
// the snapshot and equipped-tool stat queries. Main-thread state (the simulation tick), except the
// write trackers' verdicts, which the write worker settles. It knows players only by name; the
// runtime maps sessions to names and refreshes combat stats after a change.
//
// Equipment (wave H): a player has four equipment slots, rows 100..103 of the slot map (EquipmentSlots).
// A row holds the id of an item that normally also has a backpack row (the alias, H-D1), so moving or
// swapping backpack slots never touches the equipment rows. A legacy row may have no backpack row.
// Which slots an item may occupy comes from its content definition; a kind the catalog no longer
// knows, or a slot it may not occupy, is inert (no stats, no light) with its row kept.
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
    private readonly Dictionary<string, Dictionary<int, long>> _slotsByPlayer = new(StringComparer.Ordinal);

    // The starter kit version each loaded inventory has received (see GrantStarterKit). A loaded
    // inventory without an entry has no recorded grant yet.
    private readonly Dictionary<string, int> _starterKitByPlayer = new(StringComparer.Ordinal);

    // Inventory write outcomes, by player name. The write queue drops a write that fails (other than
    // on a lock) and only counts it in FailedWrites, so an inventory whose writes may have failed is
    // the only current copy and must not be replaced by its rows. An entry is made when a loaded
    // inventory queues its first write and lives while that inventory is loaded, or while an
    // evicted inventory waits for its writes to be confirmed (see Evict).
    private readonly Dictionary<string, InventoryWriteTracker> _writeTrackers = new(StringComparer.Ordinal);

    // What each loaded inventory's equipment gives the replicated actor: the held light and the
    // digest of the rolled modifiers. Recomputed by RefreshEquippedSummary on every change, so the
    // tick path (CaptureActorSnapshots) only reads it.
    private readonly Dictionary<string, EquippedSummary> _equippedSummaryByPlayer = new(StringComparer.Ordinal);

    private const ulong ModifierDigestSalt = 0x6D6F642D64696765UL; // "mod-dige"

    // Spent by an action that would write (H-D18); null means unlimited (a service used on its own).
    // The runtime sets it and spends the budget of the session making the request.
    internal Func<bool>? ActionGate { get; set; }

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

        RefreshEquippedSummary(playerName);
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
    // new player; see GrantStarterKit) and, on first sight of the inventory only (no starter grant
    // recorded yet, H-D3), equips the lowest occupied slot whose item may be held in the main hand
    // when the main hand is empty. After that an empty hand stays empty. The writes of one call
    // (items, slots, equip, grant record) are buffered into ONE queued closure, so they land
    // together or not at all: a crash or a failed write leaves the player as before (a new player
    // is seeded again by the next join) or with the complete grant. False when the inventory
    // could not be loaded yet (see TryLoadSynchronously).
    public bool Ensure(string playerName)
    {
        if (!TryLoadSynchronously(playerName))
        {
            return false;
        }

        List<Action<IWorldPersistenceRepository>> buffer = [];
        _ensureBuffer = buffer;
        try
        {
            EnsureCore(playerName);
        }
        finally
        {
            _ensureBuffer = null;
        }

        if (buffer.Count > 0)
        {
            EnqueueWorldWrite(world =>
            {
                foreach (Action<IWorldPersistenceRepository> write in buffer)
                {
                    write(world);
                }
            });
        }

        RefreshEquippedSummary(playerName);
        return true;
    }

    private void EnsureCore(string playerName)
    {
        Dictionary<int, long> slots = GetOrCreateSlots(playerName);
        bool firstSight = !_starterKitByPlayer.ContainsKey(playerName);
        bool hasBackpackContent = HasBackpackContent(slots);
        if (hasBackpackContent && firstSight)
        {
            // An existing inventory keeps the item it had first; a new one equips the first kit item.
            EquipFirstOccupiedSlotIfNone(playerName, slots);
        }

        int? grantedVersion = GrantStarterKit(playerName, slots, isTopUp: hasBackpackContent);
        if (!hasBackpackContent && firstSight)
        {
            EquipFirstOccupiedSlotIfNone(playerName, slots);
        }

        // Last in the closure, so the grant record never lands without the items.
        if (grantedVersion is int version)
        {
            RecordStarterKitGrant(playerName, version);
        }
    }

    // Equips the item in a backpack slot into an equipment slot and queues the writes. With no target
    // the item's first listed slot is used (H-D8). Checks run in a fixed order, and a rejection changes
    // nothing and queues nothing: backpack range, target range, loading, occupied, equippable and slot
    // allowed, then (only for an action that would write) the budget. Equipping what the target
    // already holds is accepted and writes nothing. An item that sits in another equipment slot
    // leaves it; the item the target held is only displaced, and stays in the backpack (a legacy one
    // with no backpack row first moves to the lowest free slot, or the equip is rejected as
    // BackpackFull so it is never lost). The caller refreshes the player's combat stats on Equipped.
    public InventoryEquipOutcome TryEquip(string playerName, int slotIndex, int? targetSlotIndex = null)
    {
        if (slotIndex < 0 || slotIndex >= InventoryConstants.BackpackSlotCount)
        {
            return InventoryEquipOutcome.SlotOutOfRange;
        }

        EquipmentSlot target = default;
        bool hasTarget = false;
        if (targetSlotIndex is int targetIndexValue)
        {
            if (!EquipmentSlots.TryFromSlotIndex(targetIndexValue, out target))
            {
                return InventoryEquipOutcome.TargetOutOfRange;
            }

            hasTarget = true;
        }

        if (!Ensure(playerName))
        {
            return InventoryEquipOutcome.StillLoading;
        }

        if (!_slotsByPlayer.TryGetValue(playerName, out Dictionary<int, long>? slots)
            || !TryGetItem(slots, slotIndex, out PersistedItemInstance item))
        {
            return InventoryEquipOutcome.SlotEmpty;
        }

        if (!ContentCatalog.Shipped.TryGetItem(item.ItemKind, out ItemDefinition? definition))
        {
            return InventoryEquipOutcome.NotEquippable;
        }

        if (!hasTarget)
        {
            if (definition.EquipSlots.Count == 0)
            {
                return InventoryEquipOutcome.NotEquippable;
            }

            target = definition.EquipSlots[0];
        }

        if ((definition.EquipSlotMask & EquipmentSlots.MaskOf(target)) == 0)
        {
            return InventoryEquipOutcome.NotEquippable;
        }

        long itemInstanceId = item.ItemInstanceId;
        int targetIndex = EquipmentSlots.ToSlotIndex(target);
        bool targetHeld = slots.TryGetValue(targetIndex, out long displacedId);
        if (targetHeld && displacedId == itemInstanceId)
        {
            return InventoryEquipOutcome.Equipped;
        }

        int rescueSlot = -1;
        if (targetHeld && _itemInstances.ContainsKey(displacedId) && !BackpackHolds(slots, displacedId))
        {
            rescueSlot = FindLowestFreeBackpackSlot(slots);
            if (rescueSlot < 0)
            {
                return InventoryEquipOutcome.BackpackFull;
            }
        }

        if (!TrySpendAction())
        {
            return InventoryEquipOutcome.Throttled;
        }

        int previousIndex = FindEquipmentSlotOf(slots, itemInstanceId);
        DateTime now = DateTime.UtcNow;
        PersistedInventorySlot? rescueRow = null;
        if (rescueSlot >= 0)
        {
            slots[rescueSlot] = displacedId;
            rescueRow = new PersistedInventorySlot(playerName, rescueSlot, displacedId, now);
        }

        slots[targetIndex] = itemInstanceId;
        if (previousIndex >= 0)
        {
            slots.Remove(previousIndex);
        }

        QueueSlotWrites(
            playerName,
            rescueRow,
            new PersistedInventorySlot(playerName, targetIndex, itemInstanceId, now),
            previousIndex,
            itemInstanceId);
        RefreshEquippedSummary(playerName);
        return InventoryEquipOutcome.Equipped;
    }

    // Empties an equipment slot (slotIndex is 100..103). The item keeps its backpack row; a legacy item
    // with no backpack row first moves to the lowest free backpack slot, or the unequip is rejected as
    // BackpackFull so the item is never lost.
    public InventoryEquipOutcome TryUnequip(string playerName, int slotIndex)
    {
        if (!EquipmentSlots.TryFromSlotIndex(slotIndex, out _))
        {
            return InventoryEquipOutcome.SlotOutOfRange;
        }

        if (!Ensure(playerName))
        {
            return InventoryEquipOutcome.StillLoading;
        }

        if (!_slotsByPlayer.TryGetValue(playerName, out Dictionary<int, long>? slots)
            || !TryGetItem(slots, slotIndex, out PersistedItemInstance item))
        {
            return InventoryEquipOutcome.SlotEmpty;
        }

        int rescueSlot = -1;
        if (!BackpackHolds(slots, item.ItemInstanceId))
        {
            rescueSlot = FindLowestFreeBackpackSlot(slots);
            if (rescueSlot < 0)
            {
                return InventoryEquipOutcome.BackpackFull;
            }
        }

        if (!TrySpendAction())
        {
            return InventoryEquipOutcome.Throttled;
        }

        PersistedInventorySlot? rescueRow = null;
        if (rescueSlot >= 0)
        {
            slots[rescueSlot] = item.ItemInstanceId;
            rescueRow = new PersistedInventorySlot(playerName, rescueSlot, item.ItemInstanceId, DateTime.UtcNow);
        }

        slots.Remove(slotIndex);
        QueueSlotWrites(playerName, rescueRow, null, slotIndex, item.ItemInstanceId);
        RefreshEquippedSummary(playerName);
        return InventoryEquipOutcome.Equipped;
    }

    // Moves the item of a backpack slot to another, or swaps the two when both are occupied. Both
    // slots are 0..15. Equipment rows follow by item id, so an equipped item stays equipped.
    // From == To is accepted and writes nothing.
    public InventoryEquipOutcome TryMove(string playerName, int from, int to)
    {
        if (from < 0 || from >= InventoryConstants.BackpackSlotCount
            || to < 0 || to >= InventoryConstants.BackpackSlotCount)
        {
            return InventoryEquipOutcome.SlotOutOfRange;
        }

        if (!Ensure(playerName))
        {
            return InventoryEquipOutcome.StillLoading;
        }

        if (!_slotsByPlayer.TryGetValue(playerName, out Dictionary<int, long>? slots)
            || !TryGetItem(slots, from, out PersistedItemInstance moving))
        {
            return InventoryEquipOutcome.SlotEmpty;
        }

        if (from == to)
        {
            return InventoryEquipOutcome.Equipped;
        }

        if (!TrySpendAction())
        {
            return InventoryEquipOutcome.Throttled;
        }

        DateTime now = DateTime.UtcNow;
        PersistedInventorySlot toRow = new(playerName, to, moving.ItemInstanceId, now);
        if (TryGetItem(slots, to, out PersistedItemInstance other))
        {
            slots[from] = other.ItemInstanceId;
            slots[to] = moving.ItemInstanceId;
            QueueSlotWrites(
                playerName,
                new PersistedInventorySlot(playerName, from, other.ItemInstanceId, now),
                toRow,
                -1,
                0);
        }
        else
        {
            slots.Remove(from);
            slots[to] = moving.ItemInstanceId;
            QueueSlotWrites(playerName, toRow, null, from, moving.ItemInstanceId);
        }

        RefreshEquippedSummary(playerName);
        return InventoryEquipOutcome.Equipped;
    }

    // Destroys the item of a backpack slot for good. The request names the item, so a queued destroy
    // cannot hit whatever moved into the slot. An equipped item is never destroyed. The item row is
    // removed too unless another loaded inventory still references it (only corrupted data).
    public InventoryEquipOutcome TryDestroy(string playerName, int slotIndex, long itemInstanceId)
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
            || !TryGetItem(slots, slotIndex, out PersistedItemInstance item))
        {
            return InventoryEquipOutcome.SlotEmpty;
        }

        if (item.ItemInstanceId != itemInstanceId)
        {
            return InventoryEquipOutcome.ItemMismatch;
        }

        if (FindEquipmentSlotOf(slots, itemInstanceId) >= 0)
        {
            return InventoryEquipOutcome.ItemEquipped;
        }

        if (!TrySpendAction())
        {
            return InventoryEquipOutcome.Throttled;
        }

        for (int index = 0; index < InventoryConstants.BackpackSlotCount; index++)
        {
            if (slots.TryGetValue(index, out long held) && held == itemInstanceId)
            {
                slots.Remove(index);
            }
        }

        if (!IsReferencedByOtherInventory(playerName, itemInstanceId))
        {
            _itemInstances.Remove(itemInstanceId);
        }

        NoteWriteQueued(playerName);
        EnqueueWorldWrite(world => world.DestroyInventoryItem(playerName, itemInstanceId));
        RefreshEquippedSummary(playerName);
        return InventoryEquipOutcome.Equipped;
    }

    // Creates a rolled item of the kind in the lowest free backpack slot. Not a client action and not
    // budgeted: wave I's loot and tests call it. NotEquippable when the kind is unknown or has no roll
    // table; BackpackFull when there is no free slot.
    internal InventoryEquipOutcome TryGrantRolled(string playerName, string kind, ItemRarity rarity, in ItemRollSeed seed)
    {
        if (!Ensure(playerName))
        {
            return InventoryEquipOutcome.StillLoading;
        }

        if (!ContentCatalog.Shipped.TryGetItem(kind, out ItemDefinition? definition)
            || definition.Rolls == null
            || !Enum.IsDefined(rarity))
        {
            return InventoryEquipOutcome.NotEquippable;
        }

        Dictionary<int, long> slots = GetOrCreateSlots(playerName);
        int freeSlot = FindLowestFreeBackpackSlot(slots);
        if (freeSlot < 0)
        {
            return InventoryEquipOutcome.BackpackFull;
        }

        GrantItem(playerName, slots, definition.Key, freeSlot, ItemRoller.Roll(definition, rarity, in seed));
        return InventoryEquipOutcome.Equipped;
    }

    // The digest of the rolled modifiers of the equipped items (SimulationStateChecksum); zero when
    // none is rolled. Read from the cache. Allocates nothing.
    public ulong ResolveModifierDigest(string playerName)
        => _equippedSummaryByPlayer.TryGetValue(playerName, out EquippedSummary summary) ? summary.Digest : 0UL;

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
            _equippedSummaryByPlayer.Remove(playerName);
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
        _equippedSummaryByPlayer.Remove(playerName);
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
            bool equipped = item.HasValue && FindEquipmentSlotOf(slots, item.Value.ItemInstanceId) >= 0;
            backpack.Add(new InventorySlotSnapshot(slotIndex, item, equipped));
        }

        InventoryItemSnapshot? mainHand = TryResolveItem(slots, EquipmentSlots.FirstSlotIndex);
        List<InventorySlotSnapshot>? equipment = null;
        for (int slotIndex = EquipmentSlots.FirstSlotIndex + 1; slotIndex <= EquipmentSlots.LastSlotIndex; slotIndex++)
        {
            InventoryItemSnapshot? item = TryResolveItem(slots, slotIndex);
            if (item.HasValue)
            {
                (equipment ??= []).Add(new InventorySlotSnapshot(slotIndex, item, true));
            }
        }

        return new PlayerInventorySnapshot(
            playerName,
            InventoryConstants.GridColumns,
            InventoryConstants.GridRows,
            backpack,
            new InventorySlotSnapshot(EquipmentSlots.FirstSlotIndex, mainHand, mainHand.HasValue),
            equipment);
    }

    // Fills the loadout with the modifiers of what the player has equipped, per slot. A rolled item's
    // modifiers replace its definition's (H-D4). A kind the catalog no longer knows, a slot the item
    // may not occupy and a damaged roll give no modifiers: the row stays and is inert, nothing throws.
    // Allocates nothing.
    public void CopyEquipment(string playerName, EquipmentLoadout into)
    {
        ArgumentNullException.ThrowIfNull(into);
        into.Clear();
        if (!_slotsByPlayer.TryGetValue(playerName, out Dictionary<int, long>? slots))
        {
            return;
        }

        for (int index = 0; index < EquipmentSlots.Count; index++)
        {
            EquipmentSlot slot = (EquipmentSlot)index;
            if (TryResolveEquipped(slots, slot, out PersistedItemInstance item, out ItemDefinition? definition))
            {
                into.Set(slot, EffectiveModifiers(in item, definition));
            }
        }
    }

    // The light the equipped items cast (EquipmentSlots.ResolveHeldLight); read from the cache.
    // Allocates nothing.
    public HeldLightKind ResolveEquippedHeldLight(string playerName)
        => _equippedSummaryByPlayer.TryGetValue(playerName, out EquippedSummary summary) ? summary.Light : HeldLightKind.None;

    // Recomputes the cached summary of a loaded inventory; called from every path that changes the
    // slots or loads an inventory.
    private void RefreshEquippedSummary(string playerName)
    {
        if (!_slotsByPlayer.TryGetValue(playerName, out Dictionary<int, long>? slots))
        {
            _equippedSummaryByPlayer.Remove(playerName);
            return;
        }

        HeldLightKind mainLight = HeldLightKind.None;
        HeldLightKind offLight = HeldLightKind.None;
        ulong digest = ModifierDigestSalt;
        bool anyRolled = false;
        for (int index = 0; index < EquipmentSlots.Count; index++)
        {
            EquipmentSlot slot = (EquipmentSlot)index;
            if (!TryResolveEquipped(slots, slot, out PersistedItemInstance item, out ItemDefinition? definition))
            {
                continue;
            }

            if (slot == EquipmentSlot.MainHand)
            {
                mainLight = definition.HeldLight;
            }
            else if (slot == EquipmentSlot.OffHand)
            {
                offLight = definition.HeldLight;
            }

            if (item.Roll == null)
            {
                continue;
            }

            IReadOnlyList<StatModifier> modifiers = item.Roll.Modifiers;
            int counted = 0;
            for (int m = 0; m < modifiers.Count && counted < StatSheet.MaxModifiersPerSource; m++)
            {
                StatModifier modifier = modifiers[m];
                if (!IsApplicable(modifier))
                {
                    continue;
                }

                counted++;
                anyRolled = true;
                digest = Seed.Combine(
                    digest,
                    (ulong)index,
                    ((ulong)(uint)modifier.Stat << 8) | (uint)modifier.Op,
                    BitConverter.SingleToUInt32Bits(modifier.Value));
            }
        }

        if (anyRolled && digest == 0UL)
        {
            digest = 1UL;
        }

        _equippedSummaryByPlayer[playerName] = new EquippedSummary(
            EquipmentSlots.ResolveHeldLight(mainLight, offLight),
            anyRolled ? digest : 0UL);
    }

    private static bool IsApplicable(StatModifier modifier)
        => (uint)modifier.Stat < (uint)StatId.Count
            && (uint)modifier.Op <= (uint)StatOp.Percent
            && float.IsFinite(modifier.Value);

    private static IReadOnlyList<StatModifier> EffectiveModifiers(in PersistedItemInstance item, ItemDefinition definition)
        => item.Roll != null ? item.Roll.Modifiers : definition.Modifiers;

    // The item in an equipment slot, when it is live: the instance exists, its kind is in the catalog
    // and the definition lets it occupy the slot.
    private bool TryResolveEquipped(
        Dictionary<int, long> slots,
        EquipmentSlot slot,
        out PersistedItemInstance item,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ItemDefinition? definition)
    {
        if (TryGetItem(slots, EquipmentSlots.ToSlotIndex(slot), out item)
            && ContentCatalog.Shipped.TryGetItem(item.ItemKind, out definition)
            && (definition.EquipSlotMask & EquipmentSlots.MaskOf(slot)) != 0)
        {
            return true;
        }

        definition = null;
        return false;
    }

    private bool TryGetItem(Dictionary<int, long> slots, int slotIndex, out PersistedItemInstance item)
    {
        if (slots.TryGetValue(slotIndex, out long itemInstanceId) && _itemInstances.TryGetValue(itemInstanceId, out item))
        {
            return true;
        }

        item = default;
        return false;
    }

    // The equipment slot index (100..103) holding the item, or -1.
    private static int FindEquipmentSlotOf(Dictionary<int, long> slots, long itemInstanceId)
    {
        for (int slotIndex = EquipmentSlots.FirstSlotIndex; slotIndex <= EquipmentSlots.LastSlotIndex; slotIndex++)
        {
            if (slots.TryGetValue(slotIndex, out long held) && held == itemInstanceId)
            {
                return slotIndex;
            }
        }

        return -1;
    }

    private static bool BackpackHolds(Dictionary<int, long> slots, long itemInstanceId)
    {
        for (int slotIndex = 0; slotIndex < InventoryConstants.BackpackSlotCount; slotIndex++)
        {
            if (slots.TryGetValue(slotIndex, out long held) && held == itemInstanceId)
            {
                return true;
            }
        }

        return false;
    }

    private bool IsReferencedByOtherInventory(string playerName, long itemInstanceId)
    {
        foreach (KeyValuePair<string, Dictionary<int, long>> entry in _slotsByPlayer)
        {
            if (string.Equals(entry.Key, playerName, StringComparison.Ordinal))
            {
                continue;
            }

            foreach (long held in entry.Value.Values)
            {
                if (held == itemInstanceId)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private bool TrySpendAction() => ActionGate?.Invoke() ?? true;

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
        RefreshEquippedSummary(playerName);
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
    // - The grant record is not queued here: the caller records it (RecordStarterKitGrant) after the
    //   first-sight main-hand equip. Ensure buffers the kit, the equip and the record into one queued
    //   closure, so they land together or not at all. Returns the version to record, or null when it
    //   does not rise, which makes a second call write nothing.
    // - With no record, a backpack holding exactly the first k (1 to kit size - 1) version-1 kinds in
    //   slots 0..k-1 and nothing else is the leftover of a seed that was cut short (written before
    //   seeding was one closure): it is taken as a new player's, so the missing kinds arrive.
    private int? GrantStarterKit(string playerName, Dictionary<int, long> slots, bool isTopUp)
    {
        bool recorded = _starterKitByPlayer.TryGetValue(playerName, out int receivedVersion);
        if (!recorded)
        {
            receivedVersion = isTopUp && !IsCutShortSeed(slots) ? ToolCatalog.UnrecordedStarterKitVersion : 0;
        }

        if (recorded && receivedVersion >= ToolCatalog.CurrentStarterKitVersion)
        {
            return null;
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

        return recorded && grantedVersion <= receivedVersion ? null : grantedVersion;
    }

    // True when the backpack holds exactly a strict, in-order prefix of the version-1 kit in slots
    // 0..k-1 and nothing else (see GrantStarterKit).
    private bool IsCutShortSeed(Dictionary<int, long> slots)
    {
        IReadOnlyList<StarterKitEntry> kit = ToolCatalog.StarterKit;
        int firstVersionKinds = 0;
        while (firstVersionKinds < kit.Count && kit[firstVersionKinds].IntroducedVersion <= ToolCatalog.UnrecordedStarterKitVersion)
        {
            firstVersionKinds++;
        }

        int occupied = 0;
        foreach (int slotIndex in slots.Keys)
        {
            if (slotIndex >= 0 && slotIndex < InventoryConstants.BackpackSlotCount)
            {
                occupied++;
            }
        }

        if (occupied < 1 || occupied >= firstVersionKinds)
        {
            return false;
        }

        for (int index = 0; index < occupied; index++)
        {
            if (!slots.TryGetValue(index, out long itemInstanceId)
                || !_itemInstances.TryGetValue(itemInstanceId, out PersistedItemInstance item)
                || !string.Equals(item.ItemKind, kit[index].ItemKind, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private void RecordStarterKitGrant(string playerName, int grantedVersion)
    {
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

    // Equips the item of the lowest occupied backpack slot that may be held in the main hand, when the
    // main hand is empty.
    private void EquipFirstOccupiedSlotIfNone(string playerName, Dictionary<int, long> slots)
    {
        if (slots.ContainsKey(InventoryConstants.EquippedSlotIndex))
        {
            return;
        }

        for (int slotIndex = 0; slotIndex < InventoryConstants.BackpackSlotCount; slotIndex++)
        {
            if (slots.TryGetValue(slotIndex, out long itemInstanceId)
                && _itemInstances.TryGetValue(itemInstanceId, out PersistedItemInstance candidate)
                && ContentCatalog.Shipped.TryGetItem(candidate.ItemKind, out ItemDefinition? candidateDefinition)
                && (candidateDefinition.EquipSlotMask & EquipmentSlots.MaskOf(EquipmentSlot.MainHand)) != 0)
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
    private long GrantItem(string playerName, Dictionary<int, long> slots, string itemKind, int slotIndex, ItemRoll? roll = null)
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
            UpdatedUtc: DateTime.UtcNow,
            Roll: roll);
        _itemInstances[item.ItemInstanceId] = item;
        slots[slotIndex] = itemInstanceId;
        PersistedInventorySlot slot = new(playerName, slotIndex, itemInstanceId, DateTime.UtcNow);
        NoteWriteQueued(playerName);
        EnqueueWorldWrite(world =>
        {
            // One closure, so the queue's savepoint lands the item and its slot together.
            world.SaveItemInstance(item);
            world.SaveInventorySlot(slot);
        });
        return itemInstanceId;
    }

    // A kind the catalog no longer knows, and an item in an equipment slot it may not occupy, are
    // inert: no modifiers and no light, the row's own fields kept, nothing throws. The modifiers of
    // an item in the backpack are those it would give when equipped (a rolled item's roll, else the
    // definition's); null, never empty, when there are none.
    private InventoryItemSnapshot? TryResolveItem(Dictionary<int, long> slots, int slotIndex)
    {
        if (!TryGetItem(slots, slotIndex, out PersistedItemInstance item))
        {
            return null;
        }

        bool known = ContentCatalog.Shipped.TryGetItem(item.ItemKind, out ItemDefinition? definition);
        bool inert = !known
            || (EquipmentSlots.TryFromSlotIndex(slotIndex, out EquipmentSlot equipmentSlot)
                && (definition!.EquipSlotMask & EquipmentSlots.MaskOf(equipmentSlot)) == 0);
        IReadOnlyList<StatModifier>? modifiers = null;
        if (!inert)
        {
            IReadOnlyList<StatModifier> effective = EffectiveModifiers(in item, definition!);
            modifiers = effective.Count == 0 ? null : effective;
        }

        return new InventoryItemSnapshot(
            item.ItemInstanceId,
            item.ItemKind,
            item.DisplayName,
            item.VisualId,
            item.StackCount,
            known ? definition!.Description : ToolCatalog.UnknownItemDescription,
            known ? definition!.DetailLines : Array.Empty<string>(),
            modifiers,
            inert ? HeldLightKind.None : definition!.HeldLight,
            known ? definition!.EquipSlotMask : 0,
            item.Roll?.Rarity ?? ItemRarity.Common,
            item.Roll != null);
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

    // One queued closure for writes that belong together, so the queue's savepoint makes them atomic:
    // up to two slot upserts, then the delete of a vacated slot row (-1 for none), which only removes
    // the row while it still holds deleteExpectedItemId.
    private void QueueSlotWrites(
        string playerName,
        PersistedInventorySlot? first,
        PersistedInventorySlot? second,
        int deleteSlotIndex,
        long deleteExpectedItemId)
    {
        NoteWriteQueued(playerName);
        EnqueueWorldWrite(world =>
        {
            if (first is PersistedInventorySlot firstRow)
            {
                world.SaveInventorySlot(firstRow);
            }

            if (second is PersistedInventorySlot secondRow)
            {
                world.SaveInventorySlot(secondRow);
            }

            if (deleteSlotIndex >= 0)
            {
                world.DeleteInventorySlot(playerName, deleteSlotIndex, deleteExpectedItemId);
            }
        });
    }

    // Set while Ensure runs: its writes are collected and queued as one closure.
    private List<Action<IWorldPersistenceRepository>>? _ensureBuffer;

    private void EnqueueWorldWrite(Action<IWorldPersistenceRepository> write)
    {
        if (_ensureBuffer != null && _worldPersistence != null && _writes != null)
        {
            _ensureBuffer.Add(write);
            return;
        }

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

    // The held light and rolled-modifier digest an inventory's equipment gives its actor.
    private readonly record struct EquippedSummary(HeldLightKind Light, ulong Digest);

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
