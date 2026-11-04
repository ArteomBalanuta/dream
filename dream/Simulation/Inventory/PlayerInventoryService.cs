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
    // M3 ground items and vendors: the player or the NPC is out of range, the ground item is claimed by
    // someone else, it (or the sold item) is gone, the wallet cannot pay or cannot take the gold, the
    // item has no sell price, the vendor does not stock the kind.
    TooFar = 11,
    NotYours = 12,
    ItemGone = 13,
    NotEnoughGold = 14,
    WalletFull = 15,
    NotSellable = 16,
    NotInStock = 17,
    // A write of this player's inventory failed, so the rows cannot be trusted; trading is refused.
    WriteFailed = 18,
    // M4 consumables: the item's kind is on its use cooldown, or it would restore nothing (the vitals
    // it restores are already full).
    OnCooldown = 19,
    NothingToRestore = 20,
    // M5 trade and M6 quests: the trade or quest action is not possible now (no such trade, the quest is not
    // active, not complete, not offered by this NPC).
    TradeUnavailable = 21,
    QuestUnavailable = 22,
}

// The authoritative players' inventories: slots and item instances by player name, item-id
// allocation, equip, starter seeding, per-player load and eviction, the failed-write retention and
// the snapshot and equipped-tool stat queries. Main-thread state (the simulation tick), except the
// write trackers' verdicts, which the write worker settles. It knows players only by name; the
// runtime maps sessions to names and refreshes combat stats after a change.
//
// The bag and the worn slots (classic HUD iteration 2). A player's rows by slot index
// (InventoryConstants): 0..63 are the anchors of items on the 8x8 bag board (an item covers its
// footprint from its top-left cell; BagGrid and BagBoard hold the placement rules), 100..111 are the
// twelve equipment slots (EquipmentSlots) and 200..215 are held-back rows. A worn item leaves the bag:
// its worn row is the only row it has (the wave H alias, one row in each, is gone). Which slots an
// item may occupy comes from its content definition; a kind the catalog no longer knows, or a slot
// it may not occupy, is inert (no stats, no light) with its row kept.
//
// Load-time repair (the first Ensure of each loaded inventory): rows that are out of bounds or
// overlap an earlier item are re-homed (largest first, first fit) and what still does not fit becomes
// a held-back row. Held-back rows wait for room and drain, in order, after every accepted move,
// destroy, equip or unequip; loot never enters them. Every action checks its input in a fixed order
// and spends the action budget last, so a rejection changes and queues nothing; an accepted action
// queues one closure (guarded deletes, then upserts), so its rows land together or not at all.
//
// Its only dependencies are the world repository (reads) and the ordered write queue (writes and
// the confirming reads); both are null when the runtime has no persistence.
internal sealed partial class PlayerInventoryService
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

    // The gold each loaded inventory's wallet holds (0..MaxGold); no entry means 0. Changed only on the
    // main thread, together with the queued wallet delta (TryAddGold and the vendor actions).
    private readonly Dictionary<string, long> _goldByPlayer = new(StringComparer.Ordinal);

    // The quests each loaded inventory's player has accepted or completed (M6, PlayerInventoryService.Quests.cs).
    // Kept here so they share the failed-write gate, the eviction retention and the queued closures.
    private readonly Dictionary<string, Dictionary<string, QuestRecord>> _questsByPlayer = new(StringComparer.Ordinal);

    // Bumped on every inventory write queued (first line of NoteWriteQueued) and on every quest change, so
    // the runtime can tell cheaply when the quest log it last sent is stale.
    private readonly Dictionary<string, long> _inventoryRevisions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _questRevisions = new(StringComparer.Ordinal);

    // The loaded inventories whose layout has been repaired (RepairLayout): done once per load, on the
    // first Ensure, and forgotten on Evict, so a reloaded inventory is checked again.
    private readonly HashSet<string> _repairedLayouts = new(StringComparer.Ordinal);

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

        if (inventory.Gold > 0)
        {
            _goldByPlayer[playerName] = Math.Min(inventory.Gold, InventoryConstants.MaxGold);
        }

        LoadQuickbar(playerName, inventory.Quickbar);
        LoadQuests(playerName, inventory.Quests);

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

    // Makes sure the player's inventory is loaded and usable: loads it if no join did, repairs its
    // layout once per load (RepairLayout), grants the starter kinds introduced after the kit version
    // the player has received (the whole kit for a new player; see GrantStarterKit) and, on first
    // sight of the inventory only (no starter grant recorded yet, H-D3), moves the item of the lowest
    // occupied anchor that may be held in the main hand into the main hand when it is empty. After
    // that an empty hand stays empty. A character seeded from nothing also wears the kit kinds whose
    // entry names an equipment well (EquipStarterKitWells, the torch in the off hand); an inventory
    // that already had rows never does. The writes of one call (repair, items, slots, equip, grant
    // record) are buffered into ONE queued closure, so they land together or not at all: a crash or
    // a failed write leaves the player as before (a new player is seeded again by the next join) or
    // with the complete grant. False when the inventory could not be loaded yet (see
    // TryLoadSynchronously).
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
        if (_repairedLayouts.Add(playerName))
        {
            RepairLayout(playerName, slots);
        }

        bool firstSight = !_starterKitByPlayer.ContainsKey(playerName);
        bool hasBackpackContent = HasInventoryContent(slots);
        if (hasBackpackContent && firstSight)
        {
            // An existing inventory keeps the item it had first; a new one equips the first kit item.
            EquipFirstOccupiedSlotIfNone(playerName, slots);
        }

        int? grantedVersion = GrantStarterKit(playerName, slots, isTopUp: hasBackpackContent);
        if (!hasBackpackContent && firstSight)
        {
            EquipFirstOccupiedSlotIfNone(playerName, slots);
            EquipStarterKitWells(playerName, slots);
        }

        // Last in the closure, so the grant record never lands without the items.
        if (grantedVersion is int version)
        {
            RecordStarterKitGrant(playerName, version);
        }
    }

    // Equips an item into an equipment slot and queues the writes. slotIndex is the item's bag anchor
    // (0..63), or an equipment slot (100..111) to move a worn item to another equipment slot, which
    // needs a target (an unequip empties a slot). With no target the item's first listed slot is used
    // (H-D8), except that an item whose first listed slot is a ring takes the first empty ring slot it
    // lists, else its first (D13). Checks run in a fixed order, and a rejection changes nothing and
    // queues nothing: source range, target range, loading, occupied, equippable and slot allowed,
    // room for the item the target holds, then (only for an action that would write) the budget.
    // Equipping what the target already holds is accepted and writes nothing.
    //
    // From the bag the item leaves the bag. The item the target held goes to the vacated anchor when
    // it fits there, else to the first fit, else the equip is rejected as BackpackFull (D10). From an
    // equipment slot the two items swap when the target is occupied and the other item may occupy the
    // source slot, else NotEquippable. The caller refreshes the player's combat stats on Equipped.
    public InventoryEquipOutcome TryEquip(string playerName, int slotIndex, int? targetSlotIndex = null)
    {
        bool fromWell = EquipmentSlots.TryFromSlotIndex(slotIndex, out EquipmentSlot sourceWell);
        if (!fromWell && !InventoryConstants.IsBagAnchor(slotIndex))
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
            if (fromWell || definition.EquipSlots.Count == 0)
            {
                return InventoryEquipOutcome.NotEquippable;
            }

            target = DefaultTarget(slots, definition);
        }

        if ((definition.EquipSlotMask & EquipmentSlots.MaskOf(target)) == 0)
        {
            return InventoryEquipOutcome.NotEquippable;
        }

        return fromWell
            ? EquipFromWell(playerName, slots, sourceWell, item, target)
            : EquipFromBag(playerName, slots, slotIndex, item, target);
    }

    // The slot an equip with no target goes to: the item's first listed slot (H-D8). When that is a
    // ring, the first listed ring slot that is empty, so a second ring fills the free ring slot instead
    // of displacing the first (D13); with both taken, the first listed (the left ring is displaced).
    private EquipmentSlot DefaultTarget(Dictionary<int, long> slots, ItemDefinition definition)
    {
        EquipmentSlot first = definition.EquipSlots[0];
        if (!IsRingSlot(first))
        {
            return first;
        }

        foreach (EquipmentSlot listed in definition.EquipSlots)
        {
            if (IsRingSlot(listed) && !HoldsLiveItem(slots, EquipmentSlots.ToSlotIndex(listed)))
            {
                return listed;
            }
        }

        return first;
    }

    private static bool IsRingSlot(EquipmentSlot slot) => slot is EquipmentSlot.RingLeft or EquipmentSlot.RingRight;

    private bool HoldsLiveItem(Dictionary<int, long> slots, int slotIndex)
        => slots.TryGetValue(slotIndex, out long itemInstanceId) && _itemInstances.ContainsKey(itemInstanceId);

    private InventoryEquipOutcome EquipFromBag(
        string playerName,
        Dictionary<int, long> slots,
        int anchor,
        PersistedItemInstance item,
        EquipmentSlot target)
    {
        long itemInstanceId = item.ItemInstanceId;
        int targetIndex = EquipmentSlots.ToSlotIndex(target);
        bool targetHeld = slots.TryGetValue(targetIndex, out long displacedId);
        if (targetHeld && displacedId == itemInstanceId)
        {
            return InventoryEquipOutcome.Equipped;
        }

        // The item the target held needs a place in the bag (a hand-edited alias already has one): the
        // anchor this item leaves if its footprint fits there, else the first fit, else nothing happens.
        int displacedAnchor = -1;
        if (targetHeld && _itemInstances.ContainsKey(displacedId) && !BagHolds(slots, displacedId))
        {
            BagBoard board = BuildBoard(slots);
            board.Remove(anchor);
            ItemFootprint footprint = FootprintOfItem(displacedId);
            displacedAnchor = board.Fits(anchor, footprint) ? anchor : board.FirstFit(footprint);
            if (displacedAnchor < 0)
            {
                return InventoryEquipOutcome.BackpackFull;
            }
        }

        if (!TrySpendAction())
        {
            return InventoryEquipOutcome.Throttled;
        }

        // An item that sits in another equipment slot (only a hand-edited row) leaves it.
        int previousIndex = FindEquipmentSlotOf(slots, itemInstanceId);
        SlotChanges changes = new();
        ClearSlot(slots, changes, anchor);
        if (previousIndex >= 0)
        {
            ClearSlot(slots, changes, previousIndex);
        }

        SetSlot(slots, changes, targetIndex, itemInstanceId);
        if (displacedAnchor >= 0)
        {
            SetSlot(slots, changes, displacedAnchor, displacedId);
        }

        DrainHeldBack(slots, changes);
        QueueChanges(playerName, changes);
        RefreshEquippedSummary(playerName);
        return InventoryEquipOutcome.Equipped;
    }

    private InventoryEquipOutcome EquipFromWell(
        string playerName,
        Dictionary<int, long> slots,
        EquipmentSlot source,
        PersistedItemInstance item,
        EquipmentSlot target)
    {
        if (source == target)
        {
            return InventoryEquipOutcome.Equipped;
        }

        int sourceIndex = EquipmentSlots.ToSlotIndex(source);
        int targetIndex = EquipmentSlots.ToSlotIndex(target);
        bool targetHeld = slots.TryGetValue(targetIndex, out long otherId);
        if (targetHeld && otherId == item.ItemInstanceId)
        {
            return InventoryEquipOutcome.Equipped;
        }

        // The other item swaps into the source slot when its kind may occupy it; a row that holds no
        // live item is simply overwritten.
        PersistedItemInstance otherItem = default;
        bool swap = targetHeld && _itemInstances.TryGetValue(otherId, out otherItem);
        if (swap)
        {
            if (!ContentCatalog.Shipped.TryGetItem(otherItem.ItemKind, out ItemDefinition? otherDefinition)
                || (otherDefinition.EquipSlotMask & EquipmentSlots.MaskOf(source)) == 0)
            {
                return InventoryEquipOutcome.NotEquippable;
            }
        }

        if (!TrySpendAction())
        {
            return InventoryEquipOutcome.Throttled;
        }

        SlotChanges changes = new();
        if (swap)
        {
            SetSlot(slots, changes, sourceIndex, otherId);
        }
        else
        {
            ClearSlot(slots, changes, sourceIndex);
        }

        SetSlot(slots, changes, targetIndex, item.ItemInstanceId);
        QueueChanges(playerName, changes);
        RefreshEquippedSummary(playerName);
        return InventoryEquipOutcome.Equipped;
    }

    // Empties an equipment slot (slotIndex is 100..111). The item goes back into the bag: onto
    // targetCell (a bag anchor, 0..63) when one is named, which must hold the item's whole footprint on
    // free cells, else NoRoom; at the first fit when none is named, else BackpackFull. Either way a
    // rejection changes nothing. A hand-edited item that still has a bag row only loses its worn row.
    public InventoryEquipOutcome TryUnequip(string playerName, int slotIndex, int? targetCell = null)
    {
        if (!EquipmentSlots.TryFromSlotIndex(slotIndex, out _))
        {
            return InventoryEquipOutcome.SlotOutOfRange;
        }

        if (targetCell is int cell && !InventoryConstants.IsBagAnchor(cell))
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

        int place = -1;
        if (!BagHolds(slots, item.ItemInstanceId))
        {
            BagBoard board = BuildBoard(slots);
            ItemFootprint footprint = BagGrid.FootprintOf(item.ItemKind);
            if (targetCell is int wanted)
            {
                if (!board.Fits(wanted, footprint))
                {
                    return InventoryEquipOutcome.NoRoom;
                }

                place = wanted;
            }
            else
            {
                place = board.FirstFit(footprint);
                if (place < 0)
                {
                    return InventoryEquipOutcome.BackpackFull;
                }
            }
        }

        if (!TrySpendAction())
        {
            return InventoryEquipOutcome.Throttled;
        }

        SlotChanges changes = new();
        ClearSlot(slots, changes, slotIndex);
        if (place >= 0)
        {
            SetSlot(slots, changes, place, item.ItemInstanceId);
        }

        DrainHeldBack(slots, changes);
        QueueChanges(playerName, changes);
        RefreshEquippedSummary(playerName);
        return InventoryEquipOutcome.Equipped;
    }

    // Moves the item anchored at bag cell from so that its top-left cell is on to (both 0..63), or
    // swaps it with the one item it lands on (BagGrid.PlanMove, D10). Two or more items under the new
    // area, a position off the board, or nowhere for the swapped item to go is NoRoom; a from that is
    // not the anchor of an item (an empty cell, or a cell an item covers without being its anchor) is
    // SlotEmpty. Equipment rows are not touched. From == To is accepted and writes nothing.
    public InventoryEquipOutcome TryMove(string playerName, int from, int to)
    {
        if (!InventoryConstants.IsBagAnchor(from) || !InventoryConstants.IsBagAnchor(to))
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

        BagMovePlan plan = BuildBoard(slots).PlanMove(from, to);
        if (plan.Verdict is BagMoveVerdict.Blocked or BagMoveVerdict.Same
            || (plan.Verdict == BagMoveVerdict.Swap && !_itemInstances.ContainsKey(slots[plan.OtherAnchor])))
        {
            // A row whose item is missing (stale data) is kept as it is; nothing is swapped onto it.
            return InventoryEquipOutcome.NoRoom;
        }

        if (!TrySpendAction())
        {
            return InventoryEquipOutcome.Throttled;
        }

        SlotChanges changes = new();
        long movingId = moving.ItemInstanceId;
        if (plan.Verdict == BagMoveVerdict.Swap)
        {
            long otherId = slots[plan.OtherAnchor];
            ClearSlot(slots, changes, plan.OtherAnchor);
            ClearSlot(slots, changes, from);
            SetSlot(slots, changes, to, movingId);
            SetSlot(slots, changes, plan.OtherTo, otherId);
        }
        else
        {
            ClearSlot(slots, changes, from);
            SetSlot(slots, changes, to, movingId);
        }

        DrainHeldBack(slots, changes);
        QueueChanges(playerName, changes);
        RefreshEquippedSummary(playerName);
        return InventoryEquipOutcome.Equipped;
    }

    // Destroys the item anchored at a bag cell (0..63) for good. The request names the item, so a
    // queued destroy cannot hit whatever moved there. A worn item is never destroyed. The item row is
    // removed too unless another loaded inventory still references it (only corrupted data).
    public InventoryEquipOutcome TryDestroy(string playerName, int slotIndex, long itemInstanceId)
    {
        if (!InventoryConstants.IsBagAnchor(slotIndex))
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

        RemoveItem(playerName, slots, itemInstanceId);
        return InventoryEquipOutcome.Equipped;
    }

    // Removes every row of the player that holds the item (the repository's destroy deletes them all),
    // and the item row itself unless another loaded inventory still references it, then drains the
    // held-back rows into the freed cells. Shared by TryDestroy and TryConsume (held: the consume closure,
    // which throws on a phantom item like the vendor sale does).
    private void RemoveItem(string playerName, Dictionary<int, long> slots, long itemInstanceId, bool held = false)
    {
        // Every row of the player that holds the item goes (the repository's destroy deletes them all).
        List<int> rows = [];
        foreach (KeyValuePair<int, long> row in slots)
        {
            if (row.Value == itemInstanceId)
            {
                rows.Add(row.Key);
            }
        }

        foreach (int row in rows)
        {
            slots.Remove(row);
        }

        if (!IsReferencedByOtherInventory(playerName, itemInstanceId))
        {
            _itemInstances.Remove(itemInstanceId);
        }

        SlotChanges changes = new();
        DrainHeldBack(slots, changes);
        QueueChanges(
            playerName,
            changes,
            held
                ? world => world.DestroyHeldInventoryItem(playerName, itemInstanceId)
                : world => world.DestroyInventoryItem(playerName, itemInstanceId));
        RefreshEquippedSummary(playerName);
    }

    // Creates a rolled item of the kind at the first fit of the bag. Not a client action and not
    // budgeted: wave I's loot and tests call it. NotEquippable when the kind is unknown or has no roll
    // table; BackpackFull when the item fits nowhere. Never touches the held-back rows.
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
        int freeSlot = BuildBoard(slots).FirstFit(definition.Footprint);
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

        // A reloaded inventory is repaired again (a restored one finds nothing to do).
        _repairedLayouts.Remove(playerName);

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
            tracker.EvictedGold = _goldByPlayer.Remove(playerName, out long evictedGold) ? evictedGold : 0;
            tracker.EvictedQuickbar = _quickbarByPlayer.Remove(playerName, out QuickbarSlotSnapshot[]? evictedQuickbar)
                ? evictedQuickbar
                : null;
            tracker.EvictedQuests = _questsByPlayer.Remove(playerName, out Dictionary<string, QuestRecord>? evictedQuests)
                ? evictedQuests
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
        _goldByPlayer.Remove(playerName);
        _quickbarByPlayer.Remove(playerName);
        _questsByPlayer.Remove(playerName);
        _inventoryRevisions.Remove(playerName);
        _questRevisions.Remove(playerName);
        foreach (long itemInstanceId in slots.Values)
        {
            _itemInstances.Remove(itemInstanceId);
        }
    }

    public PlayerInventorySnapshot BuildSnapshot(string playerName)
    {
        // Slots is sparse: the items on the board, by anchor, then the held-back rows. Worn items are
        // not in it; they are EquippedSlot (the main hand, always present) and Equipment (the rest).
        Dictionary<int, long> slots = GetOrCreateSlots(playerName);
        List<InventorySlotSnapshot> backpack = [];
        for (int slotIndex = 0; slotIndex < InventoryConstants.BackpackSlotCount; slotIndex++)
        {
            if (TryResolveItem(slots, slotIndex) is InventoryItemSnapshot item)
            {
                backpack.Add(new InventorySlotSnapshot(slotIndex, item));
            }
        }

        for (int slotIndex = InventoryConstants.HeldBackFirstIndex; slotIndex <= InventoryConstants.HeldBackLastIndex; slotIndex++)
        {
            if (TryResolveItem(slots, slotIndex) is InventoryItemSnapshot item)
            {
                backpack.Add(new InventorySlotSnapshot(slotIndex, item));
            }
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
            equipment,
            GoldOf(playerName),
            BuildQuickbarSnapshot(playerName));
    }

    // The gold in a loaded player's wallet; 0 for a player that is not loaded or has none.
    internal long GoldOf(string player) => _goldByPlayer.GetValueOrDefault(player);

    // Credits the wallet and queues the matching delta as its own closure. False (nothing changed) for
    // an inventory that is not loaded, an amount of 0 or less, or a credit that would pass MaxGold.
    internal bool TryAddGold(string player, long amount)
    {
        if (amount <= 0 || !_slotsByPlayer.ContainsKey(player))
        {
            return false;
        }

        long current = GoldOf(player);
        if (amount > InventoryConstants.MaxGold - current)
        {
            return false;
        }

        _goldByPlayer[player] = current + amount;
        NoteWriteQueued(player);
        EnqueueWorldWrite(world => world.ApplyWalletDelta(player, amount));
        return true;
    }

    // Fills the loadout with the modifiers of what the player has equipped, per slot. A rolled item's
    // modifiers replace its definition's (H-D4). A kind the catalog no longer knows, a slot the item
    // may not occupy and a damaged roll give no modifiers: the row stays and is inert, nothing throws.
    // An item id that sits in several equipment slots (only a hand-edited row) counts once, in the
    // lowest slot. Allocates nothing.
    public void CopyEquipment(string playerName, EquipmentLoadout into)
    {
        ArgumentNullException.ThrowIfNull(into);
        into.Clear();
        if (!_slotsByPlayer.TryGetValue(playerName, out Dictionary<int, long>? slots))
        {
            return;
        }

        Span<long> seen = stackalloc long[EquipmentSlots.Count];
        int seenCount = 0;
        for (int index = 0; index < EquipmentSlots.Count; index++)
        {
            EquipmentSlot slot = (EquipmentSlot)index;
            if (TryResolveEquipped(slots, slot, out PersistedItemInstance item, out ItemDefinition? definition)
                && NoteCounted(seen, ref seenCount, item.ItemInstanceId))
            {
                into.Set(slot, EffectiveModifiers(in item, definition));
            }
        }
    }

    // True the first time an item id is seen, recording it; false for an id already counted.
    private static bool NoteCounted(Span<long> seen, ref int seenCount, long itemInstanceId)
    {
        for (int i = 0; i < seenCount; i++)
        {
            if (seen[i] == itemInstanceId)
            {
                return false;
            }
        }

        seen[seenCount++] = itemInstanceId;
        return true;
    }

    // The light the equipped items cast (EquipmentSlots.ResolveHeldLight); read from the cache.
    // Allocates nothing.
    public string? ResolveEquippedVisualKey(string playerName)
        => _equippedSummaryByPlayer.TryGetValue(playerName, out EquippedSummary summary) ? summary.VisualKey : null;

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
        string mainVisual = string.Empty;
        string offVisual = string.Empty;
        ulong digest = ModifierDigestSalt;
        bool anyRolled = false;
        Span<long> seen = stackalloc long[EquipmentSlots.Count];
        int seenCount = 0;
        for (int index = 0; index < EquipmentSlots.Count; index++)
        {
            EquipmentSlot slot = (EquipmentSlot)index;
            if (!TryResolveEquipped(slots, slot, out PersistedItemInstance item, out ItemDefinition? definition)
                || !NoteCounted(seen, ref seenCount, item.ItemInstanceId))
            {
                continue;
            }

            if (slot == EquipmentSlot.MainHand)
            {
                mainLight = definition.HeldLight;
                mainVisual = definition.VisualId ?? string.Empty;
            }
            else if (slot == EquipmentSlot.OffHand)
            {
                offLight = definition.HeldLight;
                offVisual = definition.VisualId ?? string.Empty;
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
            anyRolled ? digest : 0UL,
            BuildVisualKey(mainVisual, offVisual));
    }

    // The player VisualKey: "<mainHandVisualId>|<offHandVisualId>", an empty side for an empty hand,
    // and null (off the wire) when both hands are empty. The cached string is reused every snapshot.
    private static string? BuildVisualKey(string mainVisual, string offVisual)
    {
        if (mainVisual.Length == 0 && offVisual.Length == 0)
        {
            return null;
        }

        string key = string.Concat(mainVisual, "|", offVisual);
        return key.Length <= MaxPlayerVisualKeyLength ? key : null;
    }

    public const int MaxPlayerVisualKeyLength = 129;

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

    // The equipment slot index (100..111) holding the item, or -1.
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

    // True when a bag anchor (0..63) holds the item.
    private static bool BagHolds(Dictionary<int, long> slots, long itemInstanceId)
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

        if (tracker.EvictedGold > 0)
        {
            _goldByPlayer[playerName] = tracker.EvictedGold;
        }

        tracker.EvictedGold = 0;
        if (tracker.EvictedQuickbar != null)
        {
            _quickbarByPlayer[playerName] = tracker.EvictedQuickbar;
        }

        tracker.EvictedQuickbar = null;
        if (tracker.EvictedQuests != null)
        {
            _questsByPlayer[playerName] = tracker.EvictedQuests;
        }

        tracker.EvictedQuests = null;
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

    // Grants the starter kinds introduced after the kit version the player has received, each at the
    // first fit of the bag, then records the new version. The received version is the recorded one;
    // without a record, an inventory with items (bag, worn or held back) predates kit versions and is
    // taken to hold ToolCatalog.UnrecordedStarterKitVersion (the pre-T2 kit), and an empty one is a
    // new player's, which receives the whole kit. So a kind is granted once: an item the player
    // later gets rid of is not granted again, even into an empty backpack.
    // - A kind the player already holds, in the bag or worn, is not granted again but counts as
    //   received (a pre-record top-up may have granted it).
    // - A kind that fits nowhere is not granted; the version recorded then stops below the first kind
    //   that did not fit, so it is granted once there is room.
    // - The grant record is not queued here: the caller records it (RecordStarterKitGrant) after the
    //   first-sight main-hand equip. Ensure buffers the kit, the equip and the record into one queued
    //   closure, so they land together or not at all. Returns the version to record, or null when it
    //   does not rise, which makes a second call write nothing.
    // - With no record, an inventory holding exactly the first k (1 to kit size - 1) version-1 kinds,
    //   one each, in the bag or worn, and nothing else is the leftover of a seed that was cut short
    //   (written before seeding was one closure): it is taken as a new player's, so the missing kinds
    //   arrive.
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
        BagBoard? board = null;
        for (int kindIndex = 0; kindIndex < kit.Count; kindIndex++)
        {
            StarterKitEntry entry = kit[kindIndex];
            if (entry.IntroducedVersion <= receivedVersion || HoldsKind(slots, entry.ItemKind))
            {
                continue;
            }

            ItemFootprint footprint = BagGrid.FootprintOf(entry.ItemKind);
            board ??= BuildBoard(slots);
            int freeSlot = board.FirstFit(footprint);
            if (freeSlot < 0)
            {
                grantedVersion = Math.Min(grantedVersion, entry.IntroducedVersion - 1);
                continue;
            }

            GrantItem(playerName, slots, entry.ItemKind, freeSlot);
            board.TryPlace(freeSlot, footprint);
            if (isTopUp)
            {
                NetworkDiagnosticsLog.Write(
                    "server.inventory.starter-top-up",
                    $"player={playerName} kind={entry.ItemKind} slot={freeSlot}");
            }
        }

        return recorded && grantedVersion <= receivedVersion ? null : grantedVersion;
    }

    // True when the inventory holds exactly a strict prefix of the version-1 kit, one item of each of
    // its first k kinds, in the bag, worn or held back, and nothing else (see GrantStarterKit). Where
    // the items sit does not matter: a seed that was cut short may have had its first item equipped.
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
            if (InventoryConstants.IsKnownRow(slotIndex))
            {
                occupied++;
            }
        }

        if (occupied < 1 || occupied >= firstVersionKinds)
        {
            return false;
        }

        // The rows are exactly `occupied` items, so finding each of the first `occupied` kit kinds
        // (all different) among them means each is held once and nothing else is.
        for (int index = 0; index < occupied; index++)
        {
            if (!HoldsKind(slots, kit[index].ItemKind))
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

    // True when the player has any row in the bag, worn or held back. A worn item counts: a player with
    // everything worn is not new and is not seeded a second kit.
    private static bool HasInventoryContent(Dictionary<int, long> slots)
    {
        foreach (int slotIndex in slots.Keys)
        {
            if (InventoryConstants.IsKnownRow(slotIndex))
            {
                return true;
            }
        }

        return false;
    }

    // Moves the item of the lowest occupied anchor that may be held in the main hand into the main
    // hand, when the main hand is empty. The bag row is replaced by the worn row (no alias).
    private void EquipFirstOccupiedSlotIfNone(string playerName, Dictionary<int, long> slots)
    {
        if (slots.ContainsKey(InventoryConstants.EquippedSlotIndex))
        {
            return;
        }

        for (int anchor = 0; anchor < InventoryConstants.BackpackSlotCount; anchor++)
        {
            if (slots.TryGetValue(anchor, out long itemInstanceId)
                && _itemInstances.TryGetValue(itemInstanceId, out PersistedItemInstance candidate)
                && ContentCatalog.Shipped.TryGetItem(candidate.ItemKind, out ItemDefinition? candidateDefinition)
                && (candidateDefinition.EquipSlotMask & EquipmentSlots.MaskOf(EquipmentSlot.MainHand)) != 0)
            {
                SlotChanges changes = new();
                ClearSlot(slots, changes, anchor);
                SetSlot(slots, changes, InventoryConstants.EquippedSlotIndex, itemInstanceId);
                QueueChanges(playerName, changes);
                return;
            }
        }
    }

    // Wears the starter kinds whose kit entry names an equipment well (StarterKitEntry.EquipWell):
    // today only the torch, in the off hand, so a new character is lit after dark instead of being
    // the 0.059-spread smudge an unlit figure measures at night. Called from EnsureCore only while
    // seeding a character that had no inventory row at all and no grant record, never on a later
    // login and never on a top-up: a returning player may have equipped, sold, dropped or destroyed
    // the torch, and equipping over whatever they hold would be the worse bug. A well is filled only
    // when it is empty and the kind sits at a bag anchor, so the main-hand item
    // EquipFirstOccupiedSlotIfNone chose (the builder hammer) stays where it is. The bag row is
    // replaced by the worn row (no alias), and the writes join the one closure Ensure buffers.
    private void EquipStarterKitWells(string playerName, Dictionary<int, long> slots)
    {
        foreach (StarterKitEntry entry in ToolCatalog.StarterKit)
        {
            if (entry.EquipWell is not EquipmentSlot well)
            {
                continue;
            }

            int wellIndex = EquipmentSlots.ToSlotIndex(well);
            if (slots.ContainsKey(wellIndex))
            {
                continue;
            }

            for (int anchor = 0; anchor < InventoryConstants.BackpackSlotCount; anchor++)
            {
                if (!slots.TryGetValue(anchor, out long itemInstanceId)
                    || !_itemInstances.TryGetValue(itemInstanceId, out PersistedItemInstance candidate)
                    || !string.Equals(candidate.ItemKind, entry.ItemKind, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                SlotChanges changes = new();
                ClearSlot(slots, changes, anchor);
                SetSlot(slots, changes, wellIndex, itemInstanceId);
                QueueChanges(playerName, changes);
                break;
            }
        }
    }

    // True when the player holds an item of the kind in the bag, worn or held back.
    private bool HoldsKind(Dictionary<int, long> slots, string itemKind)
    {
        foreach (KeyValuePair<int, long> row in slots)
        {
            if (InventoryConstants.IsKnownRow(row.Key)
                && _itemInstances.TryGetValue(row.Value, out PersistedItemInstance item)
                && string.Equals(item.ItemKind, itemKind, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    // The footprint of the item an id names: its kind's, One for a row whose item is missing or whose
    // kind the catalog does not know.
    private ItemFootprint FootprintOfItem(long itemInstanceId)
        => _itemInstances.TryGetValue(itemInstanceId, out PersistedItemInstance item)
            ? BagGrid.FootprintOf(item.ItemKind)
            : ItemFootprint.One;

    // The bag board of the rows in 0..63, placed in ascending anchor order, first come first kept. A
    // row that does not place (out of bounds or over an earlier one) is added to unplaced, when given;
    // otherwise it is left out. After RepairLayout there are none.
    private BagBoard BuildBoard(Dictionary<int, long> slots, List<int>? unplaced = null)
    {
        BagBoard board = new();
        for (int anchor = 0; anchor < InventoryConstants.BackpackSlotCount; anchor++)
        {
            if (slots.TryGetValue(anchor, out long itemInstanceId)
                && !board.TryPlace(anchor, FootprintOfItem(itemInstanceId)))
            {
                unplaced?.Add(anchor);
            }
        }

        return board;
    }

    // Sets the layout right once per loaded inventory (the first Ensure), deterministically so that
    // lockstep holds. 1. Walk the anchors ascending and keep each item that is in bounds and overlaps
    // nothing kept. 2. Re-home the rest, largest area first then lowest old anchor, at the first fit.
    // 3. What still does not fit becomes a held-back row (200 up, the lowest free one; 300 up, parked
    // and inert, when all sixteen are taken). The unplaced rows are lifted off first, so a re-homed
    // item can never land on a row that has not been dealt with. Finally held-back rows (these and
    // earlier ones) move onto any room there is. The writes are queued with the caller's Ensure.
    private void RepairLayout(string playerName, Dictionary<int, long> slots)
    {
        List<int> unplaced = [];
        BagBoard board = BuildBoard(slots, unplaced);
        SlotChanges changes = new();
        int rehomed = 0;
        int heldBack = 0;
        if (unplaced.Count > 0)
        {
            List<(int Anchor, long ItemId, ItemFootprint Footprint)> lifted = new(unplaced.Count);
            foreach (int anchor in unplaced)
            {
                long liftedId = slots[anchor];
                lifted.Add((anchor, liftedId, FootprintOfItem(liftedId)));
                ClearSlot(slots, changes, anchor);
            }

            lifted.Sort(static (a, b) =>
            {
                int byArea = (b.Footprint.Cols * b.Footprint.Rows).CompareTo(a.Footprint.Cols * a.Footprint.Rows);
                return byArea != 0 ? byArea : a.Anchor.CompareTo(b.Anchor);
            });
            foreach ((int _, long itemInstanceId, ItemFootprint footprint) in lifted)
            {
                int fit = board.FirstFit(footprint);
                if (fit >= 0)
                {
                    board.TryPlace(fit, footprint);
                    SetSlot(slots, changes, fit, itemInstanceId);
                    rehomed++;
                    continue;
                }

                int held = FirstFreeRow(slots, InventoryConstants.HeldBackFirstIndex, InventoryConstants.HeldBackCount);
                if (held < 0)
                {
                    held = FirstFreeRow(slots, InventoryConstants.ParkedFirstIndex, slots.Count + 1);
                }

                SetSlot(slots, changes, held, itemInstanceId);
                heldBack++;
            }
        }

        DrainHeldBack(slots, changes);
        if (rehomed > 0 || heldBack > 0)
        {
            NetworkDiagnosticsLog.Write(
                "server.inventory.layout-repaired",
                $"player={playerName} rehomed={rehomed} heldBack={heldBack}");
        }

        QueueChanges(playerName, changes);
    }

    // The lowest slot index of count consecutive indexes from first that has no row, or -1.
    private static int FirstFreeRow(Dictionary<int, long> slots, int first, int count)
    {
        for (int index = first; index < first + count; index++)
        {
            if (!slots.ContainsKey(index))
            {
                return index;
            }
        }

        return -1;
    }

    // Moves the held-back rows, in ascending order, to the first fit of the bag where there is room.
    // Called after every accepted action that can free cells; does nothing when nothing is held back.
    private void DrainHeldBack(Dictionary<int, long> slots, SlotChanges changes)
    {
        bool any = false;
        for (int index = InventoryConstants.HeldBackFirstIndex; index <= InventoryConstants.HeldBackLastIndex && !any; index++)
        {
            any = slots.ContainsKey(index);
        }

        if (!any)
        {
            return;
        }

        BagBoard board = BuildBoard(slots);
        for (int index = InventoryConstants.HeldBackFirstIndex; index <= InventoryConstants.HeldBackLastIndex; index++)
        {
            if (!slots.TryGetValue(index, out long itemInstanceId))
            {
                continue;
            }

            ItemFootprint footprint = FootprintOfItem(itemInstanceId);
            int fit = board.FirstFit(footprint);
            if (fit < 0)
            {
                continue;
            }

            board.TryPlace(fit, footprint);
            ClearSlot(slots, changes, index);
            SetSlot(slots, changes, fit, itemInstanceId);
        }
    }

    // Creates a new item of the kind with its top-left cell at the bag anchor and queues its item and
    // slot writes.
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

    // The net row changes of one action, queued as ONE closure (QueueChanges) so the queue's savepoint
    // makes them atomic. Removes are guarded by the item id the row held, so a row that diverged from
    // the in-memory view (an earlier write failed) is left alone; sets are upserts. A slot that is
    // set over an occupied row records that previous occupant as a guarded remove too (SetSlot), and
    // every guarded delete runs before any upsert, including for a slot that is set again. The unique
    // index on (world_id, item_instance_id) allows an item one row only, so a swap (item A moves to
    // B's slot while B moves to A's) must delete A's old row before A's new row is written.
    private sealed class SlotChanges
    {
        private readonly List<(int Slot, long ItemInstanceId)> _removed = [];
        private readonly List<(int Slot, long ItemInstanceId)> _set = [];

        public bool IsEmpty => _removed.Count == 0 && _set.Count == 0;

        public void Remove(int slot, long itemInstanceId)
        {
            _set.RemoveAll(entry => entry.Slot == slot);
            _removed.Add((slot, itemInstanceId));
        }

        public void Set(int slot, long itemInstanceId)
        {
            _set.RemoveAll(entry => entry.Slot == slot);
            _set.Add((slot, itemInstanceId));
        }

        // The writes in the order they must run: every guarded delete first (also for a slot that is set
        // again: the item that left it may be written to another slot in the same closure), then the
        // upserts. A delete is guarded by the item id, so it never removes a row that has been replaced
        // by a different item.
        public Action<IWorldPersistenceRepository> ToWrite(string playerName, Action<IWorldPersistenceRepository>? before)
        {
            (int Slot, long ItemInstanceId)[] deletes = [.. _removed];

            DateTime now = DateTime.UtcNow;
            PersistedInventorySlot[] upserts = new PersistedInventorySlot[_set.Count];
            for (int i = 0; i < upserts.Length; i++)
            {
                upserts[i] = new PersistedInventorySlot(playerName, _set[i].Slot, _set[i].ItemInstanceId, now);
            }

            return world =>
            {
                before?.Invoke(world);
                foreach ((int slot, long itemInstanceId) in deletes)
                {
                    world.DeleteInventorySlot(playerName, slot, itemInstanceId);
                }

                foreach (PersistedInventorySlot upsert in upserts)
                {
                    world.SaveInventorySlot(upsert);
                }
            };
        }
    }

    // Sets a row in memory and records the upsert, preceded by the guarded remove of the row's previous
    // occupant when there is one (see SlotChanges).
    private static void SetSlot(Dictionary<int, long> slots, SlotChanges changes, int slotIndex, long itemInstanceId)
    {
        if (slots.TryGetValue(slotIndex, out long previousItemInstanceId))
        {
            changes.Remove(slotIndex, previousItemInstanceId);
        }

        slots[slotIndex] = itemInstanceId;
        changes.Set(slotIndex, itemInstanceId);
    }

    // Removes a row in memory and records the guarded delete; nothing when there is no row.
    private static void ClearSlot(Dictionary<int, long> slots, SlotChanges changes, int slotIndex)
    {
        if (slots.Remove(slotIndex, out long itemInstanceId))
        {
            changes.Remove(slotIndex, itemInstanceId);
        }
    }

    // Queues an action's changes as one closure, after an optional write that belongs to it (the
    // destroy of an item). Nothing is queued when there is neither.
    private void QueueChanges(string playerName, SlotChanges changes, Action<IWorldPersistenceRepository>? before = null)
    {
        if (changes.IsEmpty && before == null)
        {
            return;
        }

        NoteWriteQueued(playerName);
        EnqueueWorldWrite(changes.ToWrite(playerName, before));
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
        _inventoryRevisions[playerName] = _inventoryRevisions.GetValueOrDefault(playerName) + 1;
        if (_writes == null || _worldPersistence == null || _writeTrackers.ContainsKey(playerName))
        {
            return;
        }

        _writeTrackers[playerName] = new InventoryWriteTracker(_writes.FailedWrites);
    }

    // The held light and rolled-modifier digest an inventory's equipment gives its actor.
    private readonly record struct EquippedSummary(HeldLightKind Light, ulong Digest, string? VisualKey);

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

        // The moved-out inventory's wallet.
        public long EvictedGold { get; set; }

        // The moved-out inventory's quickbar rows, if it had any.
        public QuickbarSlotSnapshot[]? EvictedQuickbar { get; set; }

        // The moved-out inventory's quest records, if it had any.
        public Dictionary<string, QuestRecord>? EvictedQuests { get; set; }

        public InventoryWriteVerdict Verdict => (InventoryWriteVerdict)Volatile.Read(ref _verdict);

        public void Settle(bool failed)
        {
            Volatile.Write(ref _verdict, (int)(failed ? InventoryWriteVerdict.Failed : InventoryWriteVerdict.Clean));
        }
    }
}
