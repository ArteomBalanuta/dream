using Dream.Simulation.Content;
using Dream.Simulation.Persistence;

namespace Dream.Simulation.Inventory;

// Vendor trade (M3 workstream B): buy and sell against the wallet. The wallet itself (GoldOf,
// TryAddGold) lives in PlayerInventoryService.cs. Both actions check their input in a fixed order and
// spend the action budget last, so a rejection changes and queues nothing. Each accepted action queues
// ONE closure that holds the item rows and a wallet DELTA, never an absolute gold value, so the queue's
// savepoint lands them together or not at all: a failed write cannot keep the gold and the item.
internal sealed partial class PlayerInventoryService
{
    // True when a write failed since this player's first queued write (the same test Evict uses: the
    // queue does not say which write failed, so any failure counts) or the inventory is already retained.
    // The rows may then lack items or hold stale ones, so trading, which moves gold against rows, is
    // refused. It does not clear while the run lasts: the in-memory copy stays the only current one
    // (Evict keeps it, Retained is never reset), and only a restart reloads the rows.
    private bool HasSeenFailedWrite(string playerName)
        => _writes != null
            && _writeTrackers.TryGetValue(playerName, out InventoryWriteTracker? tracker)
            && (tracker.Retained || _writes.FailedWrites > tracker.FailedWritesBaseline);

    // Buys one unrolled item of the kind for price gold into the first free place of the bag. The caller
    // (the runtime) has already checked the NPC's range and that it stocks the kind at that price.
    // NotInStock when the kind is unknown or rolled, NotEnoughGold, BackpackFull, then Throttled.
    internal InventoryEquipOutcome TryBuy(string playerName, string itemKind, long price)
    {
        if (!Ensure(playerName))
        {
            return InventoryEquipOutcome.StillLoading;
        }

        if (HasSeenFailedWrite(playerName))
        {
            return InventoryEquipOutcome.WriteFailed;
        }

        if (price <= 0
            || !ContentCatalog.Shipped.TryGetItem(itemKind, out ItemDefinition? definition)
            || definition.Rolls != null)
        {
            return InventoryEquipOutcome.NotInStock;
        }

        long gold = GoldOf(playerName);
        if (gold < price)
        {
            return InventoryEquipOutcome.NotEnoughGold;
        }

        Dictionary<int, long> slots = GetOrCreateSlots(playerName);
        int freeSlot = BuildBoard(slots).FirstFit(definition.Footprint);
        if (freeSlot < 0)
        {
            return InventoryEquipOutcome.BackpackFull;
        }

        if (!TrySpendAction())
        {
            return InventoryEquipOutcome.Throttled;
        }

        ToolDefinition tool = ToolCatalog.Resolve(definition.Key);
        long itemInstanceId = _objectIds.Take().Value;
        PersistedItemInstance item = new(
            itemInstanceId,
            tool.ItemKind,
            tool.DisplayName,
            tool.VisualId,
            playerName,
            WorldPosition: null,
            State: "Inventory",
            StackCount: 1,
            CreatedUtc: DateTime.UtcNow,
            UpdatedUtc: DateTime.UtcNow,
            Roll: null);
        _itemInstances[itemInstanceId] = item;
        slots[freeSlot] = itemInstanceId;
        _goldByPlayer[playerName] = gold - price;
        PersistedInventorySlot slot = new(playerName, freeSlot, itemInstanceId, DateTime.UtcNow);
        NoteWriteQueued(playerName);
        EnqueueWorldWrite(world =>
        {
            // The debit first: a wallet that cannot pay (CHECK gold >= 0) aborts before any item row.
            world.ApplyWalletDelta(playerName, -price);
            world.SaveItemInstance(item);
            world.SaveInventorySlot(slot);
        });
        RefreshEquippedSummary(playerName);
        return InventoryEquipOutcome.Equipped;
    }

    // Sells the item anchored at a bag cell. The request names the item (like TryDestroy), so a queued
    // sell cannot hit whatever moved there, and a second sell of the same item finds the cell empty or
    // another item and fails. A worn item is never sold. NotSellable when the definition has no sell price
    // (or the kind is unknown), WalletFull when the credit would pass MaxGold.
    internal InventoryEquipOutcome TrySell(string playerName, int slotIndex, long itemInstanceId)
    {
        if (!InventoryConstants.IsBagAnchor(slotIndex))
        {
            return InventoryEquipOutcome.SlotOutOfRange;
        }

        if (!Ensure(playerName))
        {
            return InventoryEquipOutcome.StillLoading;
        }

        if (HasSeenFailedWrite(playerName))
        {
            return InventoryEquipOutcome.WriteFailed;
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

        long price = ContentCatalog.Shipped.TryGetItem(item.ItemKind, out ItemDefinition? definition)
            ? VendorRules.SellPrice(definition, item.Roll?.Rarity ?? ItemRarity.Common)
            : 0;
        if (price <= 0)
        {
            return InventoryEquipOutcome.NotSellable;
        }

        long gold = GoldOf(playerName);
        if (price > InventoryConstants.MaxGold - gold)
        {
            return InventoryEquipOutcome.WalletFull;
        }

        if (!TrySpendAction())
        {
            return InventoryEquipOutcome.Throttled;
        }

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

        _goldByPlayer[playerName] = gold + price;
        SlotChanges changes = new();
        DrainHeldBack(slots, changes);
        QueueChanges(playerName, changes, world =>
        {
            // The destroy first: it throws when no slot row holds the item (a phantom from a failed
            // buy, grant or kit), which rolls the closure back before any gold is credited.
            world.DestroyHeldInventoryItem(playerName, itemInstanceId);
            world.ApplyWalletDelta(playerName, price);
        });
        RefreshEquippedSummary(playerName);
        return InventoryEquipOutcome.Equipped;
    }
}
