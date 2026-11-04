using Dream.Simulation.Inventory;

namespace Dream.Tests.Support;

// The bag is a footprint board now: an item's slot index is its top-left anchor cell, not "the nth
// slot". Tests find items by kind instead of by guessing where first fit put them.
internal static class BagTestSupport
{
    // The anchor of the first item of the kind on the board (lowest anchor), failing the test with a
    // clear message when the bag holds none. Held-back items are not on the board and are not found.
    public static int AnchorOf(this PlayerInventorySnapshot snapshot, string itemKind)
    {
        int anchor = TryAnchorOf(snapshot, itemKind);
        return anchor >= 0
            ? anchor
            : throw new Xunit.Sdk.XunitException(
                $"The bag holds no {itemKind}; it holds: {string.Join(", ", BagItems(snapshot).Select(slot => $"{slot.Item!.Value.ItemKind}@{slot.SlotIndex}"))}.");
    }

    // The same from a service, using its current snapshot.
    internal static int AnchorOf(this PlayerInventoryService service, string player, string itemKind)
        => service.BuildSnapshot(player).AnchorOf(itemKind);

    // The anchor of the first item of the kind, or -1.
    public static int TryAnchorOf(this PlayerInventorySnapshot snapshot, string itemKind)
    {
        foreach (InventorySlotSnapshot slot in BagItems(snapshot))
        {
            if (string.Equals(slot.Item!.Value.ItemKind, itemKind, StringComparison.OrdinalIgnoreCase))
            {
                return slot.SlotIndex;
            }
        }

        return -1;
    }

    // The items on the board (anchors 0..63), in anchor order.
    public static IEnumerable<InventorySlotSnapshot> BagItems(this PlayerInventorySnapshot snapshot)
        => snapshot.Slots.Where(slot => slot.Item != null && InventoryConstants.IsBagAnchor(slot.SlotIndex));

    // Every item the inventory holds: the board, held back, the main hand and the other worn slots.
    public static IEnumerable<InventoryItemSnapshot> AllItems(this PlayerInventorySnapshot snapshot)
    {
        foreach (InventorySlotSnapshot slot in snapshot.Slots)
        {
            if (slot.Item is { } item)
            {
                yield return item;
            }
        }

        if (snapshot.EquippedSlot?.Item is { } main)
        {
            yield return main;
        }

        foreach (InventorySlotSnapshot slot in snapshot.Equipment ?? [])
        {
            if (slot.Item is { } item)
            {
                yield return item;
            }
        }
    }

    // The anchor the item with that id sits at on the board; fails the test when it is not on the board
    // (it may be worn, held back, or gone).
    public static int AnchorOfId(this PlayerInventorySnapshot snapshot, long itemInstanceId)
    {
        foreach (InventorySlotSnapshot slot in snapshot.BagItems())
        {
            if (slot.Item!.Value.ItemInstanceId == itemInstanceId)
            {
                return slot.SlotIndex;
            }
        }

        throw new Xunit.Sdk.XunitException($"Item {itemInstanceId} is not on the board.");
    }

    // The held-back items (rows 200..215), in row order.
    public static IEnumerable<InventorySlotSnapshot> HeldBackItems(this PlayerInventorySnapshot snapshot)
        => snapshot.Slots.Where(slot => slot.Item != null && InventoryConstants.IsHeldBack(slot.SlotIndex));

    // The item at an anchor, or null when no item is anchored there.
    public static InventoryItemSnapshot? ItemAt(this PlayerInventorySnapshot snapshot, int anchor)
        => snapshot.Slots.FirstOrDefault(slot => slot.SlotIndex == anchor).Item;

    // The id of the item anchored at the cell, or 0.
    public static long IdAt(this PlayerInventorySnapshot snapshot, int anchor)
        => snapshot.ItemAt(anchor)?.ItemInstanceId ?? 0;

    // The id of the item worn in the equipment slot (100..111), or 0.
    public static long WornId(this PlayerInventorySnapshot snapshot, int slotIndex)
    {
        if (snapshot.EquippedSlot is { } main && main.SlotIndex == slotIndex)
        {
            return main.Item?.ItemInstanceId ?? 0;
        }

        return snapshot.Equipment?.FirstOrDefault(slot => slot.SlotIndex == slotIndex).Item?.ItemInstanceId ?? 0;
    }

    // The cells an item at the anchor covers, from its kind's footprint.
    public static ulong MaskOf(int anchor, string itemKind)
        => BagGrid.TryMask(anchor, BagGrid.FootprintOf(itemKind), out ulong mask) ? mask : 0;

    // The cells the items on the board cover.
    public static int OccupiedCells(this PlayerInventorySnapshot snapshot)
    {
        ulong occupied = 0;
        foreach (InventorySlotSnapshot slot in snapshot.BagItems())
        {
            if (BagGrid.TryMask(slot.SlotIndex, BagGrid.FootprintOf(slot.Item!.Value.ItemKind), out ulong mask))
            {
                occupied |= mask;
            }
        }

        return System.Numerics.BitOperations.PopCount(occupied);
    }

    public static int FreeCells(this PlayerInventorySnapshot snapshot) => BagGrid.CellCount - snapshot.OccupiedCells();

    // The starter kit wears the torch in the off hand (StarterKitEntry.EquipWell), so a test that
    // wants it on the board puts it back and takes its anchor from the bag rather than pinning one.
    internal static int UnequipStarterTorch(this PlayerInventoryService service, string player)
    {
        Xunit.Assert.Equal(
            InventoryEquipOutcome.Equipped,
            service.TryUnequip(player, EquipmentSlots.ToSlotIndex(EquipmentSlot.OffHand)));
        return service.AnchorOf(player, "tool.torch");
    }

    // Grants 1x1 items (rings by default) at the first fits until exactly freeCells cells of the board
    // are free, so a test can say "room for exactly three more" in cells.
    internal static void FillBagLeaving(this PlayerInventoryService service, string player, int freeCells, string kind = "gear.iron-band")
    {
        int seed = 0;
        while (service.BuildSnapshot(player).FreeCells() > freeCells)
        {
            InventoryEquipOutcome outcome = service.TryGrantRolled(player, kind, ItemRarity.Common, new ItemRollSeed(5, 5, 5, 5, seed++));
            Xunit.Assert.Equal(InventoryEquipOutcome.Equipped, outcome);
        }

        Xunit.Assert.Equal(freeCells, service.BuildSnapshot(player).FreeCells());
    }

    // Every item of a snapshot with where it is, in one line: the board and held-back rows by slot, the
    // main hand and the other worn slots. Two inventories with the same layout have the same text.
    public static string Layout(this PlayerInventorySnapshot snapshot)
        => string.Join(
            ";",
            snapshot.Slots.Select(slot => $"{slot.SlotIndex}={slot.Item?.ItemInstanceId}")
                .Append($"worn100={snapshot.EquippedSlot?.Item?.ItemInstanceId}")
                .Concat((snapshot.Equipment ?? []).Select(slot => $"worn{slot.SlotIndex}={slot.Item?.ItemInstanceId}")));

    // True when no two items on the board share a cell and every item is inside the board.
    public static bool BoardIsConsistent(this PlayerInventorySnapshot snapshot)
    {
        ulong occupied = 0;
        foreach (InventorySlotSnapshot slot in snapshot.BagItems())
        {
            if (!BagGrid.TryMask(slot.SlotIndex, BagGrid.FootprintOf(slot.Item!.Value.ItemKind), out ulong mask)
                || (mask & occupied) != 0)
            {
                return false;
            }

            occupied |= mask;
        }

        return true;
    }
}
