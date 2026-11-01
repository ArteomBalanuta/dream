using Xunit;
using Dream.Simulation.Inventory;
using Dream.Simulation.Persistence;
using Dream.Simulation.World;
using Dream.Tests.Support;

namespace Dream.Tests.Simulation.Inventory;

// Classic HUD iteration 2: the load-time repair of a player's bag (the first Ensure of each loaded
// inventory) and the held-back rows. The bag is an 8x8 board; a row's slot index is its item's top-left
// cell. Rows that are out of bounds or overlap an earlier item are re-homed (largest first, first fit) and
// what still does not fit is held back (200..215), waiting for room; nothing is ever lost.
public sealed class BagLayoutRepairTests
{
    private const string Player = "Alice";

    private static EquipmentTestWorld WorldWith(params (long Id, string Kind, int Slot)[] rows)
    {
        EquipmentTestWorld world = new();
        foreach ((long id, string kind, int slot) in rows)
        {
            world.PutItem(EquipmentTestWorld.MakeItem(id, Player, kind));
            world.PutSlot(Player, slot, id);
        }

        world.PutGrant(Player, ToolCatalog.CurrentStarterKitVersion);
        return world;
    }

    // Sixteen blades as migration 0010 leaves a full legacy backpack: old slot i at anchor (i / 4) * 16 + (i % 4) * 2.
    private static EquipmentTestWorld SixteenBlades()
    {
        (long, string, int)[] rows = [.. Enumerable.Range(0, 16).Select(i => ((long)(100 + i), "gear.iron-blade", ((i / 4) * 16) + ((i % 4) * 2)))];
        return WorldWith(rows);
    }

    [Fact]
    public void ARowOutOfBounds_IsRehomedAtTheFirstFit()
    {
        // A 2-wide blade anchored in the last column runs off the board.
        EquipmentTestWorld world = WorldWith((50, "gear.iron-blade", 7));
        PlayerInventoryService inventory = EquipmentSlotTests.Create(world, out PersistenceWriteQueue writes);
        using (writes)
        {
            Assert.True(inventory.Ensure(Player));

            PlayerInventorySnapshot snapshot = inventory.BuildSnapshot(Player);
            Assert.Equal(50, snapshot.IdAt(0));
            Assert.Single(snapshot.Slots);
            Assert.Equal(new Dictionary<int, long> { [0] = 50 }, world.SlotsOf(Player));
        }
    }

    [Fact]
    public void AnOverlappingRow_KeepsTheLowerAnchor_AndTheOtherIsRehomed()
    {
        // A blade (2x3) at 0 covers cells 9 and 17; a cap (2x2) at 9 and a ring at 17 sit under it.
        EquipmentTestWorld world = WorldWith((50, "gear.iron-blade", 0), (51, "gear.leather-cap", 9), (52, "gear.iron-band", 17));
        PlayerInventoryService inventory = EquipmentSlotTests.Create(world, out PersistenceWriteQueue writes);
        using (writes)
        {
            Assert.True(inventory.Ensure(Player));

            PlayerInventorySnapshot snapshot = inventory.BuildSnapshot(Player);
            Assert.Equal(50, snapshot.IdAt(0));
            // Largest first: the cap (4 cells) before the ring (1 cell); first fit after the blade.
            Assert.Equal(51, snapshot.IdAt(2));
            Assert.Equal(52, snapshot.IdAt(4));
            Assert.True(snapshot.BoardIsConsistent());
            Assert.Equal(new Dictionary<int, long> { [0] = 50, [2] = 51, [4] = 52 }, world.SlotsOf(Player));
        }
    }

    [Fact]
    public void ARowTheContentNowMakesBigger_IsRepairedLikeAnyOverlap()
    {
        // Two blades laid out when a blade took 2x2 (anchors 0 and 16 are a clean fit for that); as a 2x3 the
        // second one lies on the first one's third row.
        EquipmentTestWorld world = WorldWith((50, "gear.iron-blade", 0), (51, "gear.iron-blade", 16));
        PlayerInventoryService inventory = EquipmentSlotTests.Create(world, out PersistenceWriteQueue writes);
        using (writes)
        {
            Assert.True(inventory.Ensure(Player));

            PlayerInventorySnapshot snapshot = inventory.BuildSnapshot(Player);
            Assert.Equal(50, snapshot.IdAt(0));
            Assert.Equal(51, snapshot.IdAt(2));
            Assert.Equal(new Dictionary<int, long> { [0] = 50, [2] = 51 }, world.SlotsOf(Player));
        }
    }

    [Fact]
    public void ARepairedRowIsPlacedBeforeAnyRowThatHasNotBeenDealtWithCanBeOverwritten()
    {
        // The overlapped item at 8 would first fit at 2, which is a row of its own (a ring that overlaps
        // nothing, so it stays): the lifted item must go around it, never over it.
        EquipmentTestWorld world = WorldWith((50, "gear.iron-blade", 0), (51, "gear.iron-blade", 8), (52, "gear.iron-band", 2));
        PlayerInventoryService inventory = EquipmentSlotTests.Create(world, out PersistenceWriteQueue writes);
        using (writes)
        {
            Assert.True(inventory.Ensure(Player));

            PlayerInventorySnapshot snapshot = inventory.BuildSnapshot(Player);
            Assert.Equal(52, snapshot.IdAt(2));
            Assert.Equal(3, snapshot.AllItems().Count());
            Assert.True(snapshot.BoardIsConsistent());
            Assert.Equal(3, world.SlotsOf(Player).Count);
        }
    }

    [Fact]
    public void SixteenTwoByThreeItems_LoadAsEightPlacedAndEightHeldBack_NoneLost()
    {
        EquipmentTestWorld world = SixteenBlades();
        PlayerInventoryService inventory = EquipmentSlotTests.Create(world, out PersistenceWriteQueue writes);
        using (writes)
        {
            Assert.True(inventory.Ensure(Player));

            PlayerInventorySnapshot snapshot = inventory.BuildSnapshot(Player);
            Assert.Equal([0, 2, 4, 6, 32, 34, 36, 38], snapshot.BagItems().Select(slot => slot.SlotIndex));
            // In the order of the old anchors they came from.
            Assert.Equal([200, 201, 202, 203, 204, 205, 206, 207], snapshot.HeldBackItems().Select(slot => slot.SlotIndex));
            Assert.Equal(
                Enumerable.Range(100, 16).Select(id => (long)id),
                snapshot.AllItems().Select(item => item.ItemInstanceId).Order());
            Assert.True(snapshot.BoardIsConsistent());

            // The rows say the same, and nothing is in a row of its own twice.
            Dictionary<int, long> rows = world.SlotsOf(Player);
            Assert.Equal(16, rows.Count);
            Assert.Equal(16, rows.Values.Distinct().Count());
            Assert.Equal(snapshot.Layout(), BuildFromRows(world).Layout());
        }
    }

    [Fact]
    public void HeldBackRows_DrainInOrderAsRoomAppears_AfterADestroyAMoveAnEquipAndAnUnequip()
    {
        EquipmentTestWorld world = SixteenBlades();
        PlayerInventoryService inventory = EquipmentSlotTests.Create(world, out PersistenceWriteQueue writes);
        using (writes)
        {
            Assert.True(inventory.Ensure(Player));
            long Held(int row) => inventory.BuildSnapshot(Player).IdAt(row);

            // A destroy frees a 2x3 block; the first held-back item takes the lowest anchor that fits.
            long firstHeld = Held(200);
            Assert.Equal(InventoryEquipOutcome.Equipped, inventory.TryDestroy(Player, 0, inventory.BuildSnapshot(Player).IdAt(0)));
            PlayerInventorySnapshot afterDestroy = inventory.BuildSnapshot(Player);
            Assert.Equal(firstHeld, afterDestroy.IdAt(0));
            Assert.Equal(7, afterDestroy.HeldBackItems().Count());
            Assert.Equal(7, world.SlotsOf(Player).Keys.Count(row => row >= 200));
            Assert.Equal(afterDestroy.Layout(), BuildFromRows(world).Layout());

            // An equip frees a block too: the blade goes to the main hand (nothing is displaced).
            Assert.Equal(InventoryEquipOutcome.Equipped, inventory.TryEquip(Player, 2));
            PlayerInventorySnapshot afterEquip = inventory.BuildSnapshot(Player);
            Assert.Equal(6, afterEquip.HeldBackItems().Count());
            Assert.NotEqual(0, afterEquip.IdAt(2));
            Assert.Equal(afterEquip.Layout(), BuildFromRows(world).Layout());

            // An unequip needs a block of its own; with the bag full again it is refused, and nothing drains.
            Assert.Equal(InventoryEquipOutcome.BackpackFull, inventory.TryUnequip(Player, 100));
            Assert.Equal(afterEquip.Layout(), inventory.BuildSnapshot(Player).Layout());

            // A move that swaps a 2x3 for nothing smaller frees nothing; the held rows stay in order.
            Assert.Equal(InventoryEquipOutcome.Equipped, inventory.TryMove(Player, 4, 6));
            Assert.Equal(afterEquip.HeldBackItems().Select(slot => slot.SlotIndex), inventory.BuildSnapshot(Player).HeldBackItems().Select(slot => slot.SlotIndex));

            // Destroying everything on the board drains all that is held back.
            foreach (InventorySlotSnapshot slot in inventory.BuildSnapshot(Player).BagItems().ToArray())
            {
                if (inventory.BuildSnapshot(Player).IdAt(slot.SlotIndex) == slot.Item!.Value.ItemInstanceId)
                {
                    Assert.Equal(InventoryEquipOutcome.Equipped, inventory.TryDestroy(Player, slot.SlotIndex, slot.Item!.Value.ItemInstanceId));
                }
            }

            PlayerInventorySnapshot final = inventory.BuildSnapshot(Player);
            Assert.Empty(final.HeldBackItems());
            Assert.Equal(final.Layout(), BuildFromRows(world).Layout());
            Assert.True(final.BoardIsConsistent());
        }
    }

    [Fact]
    public void LootNeverEntersAHeldBackRow_AndANewItemDoesNotJumpTheQueue()
    {
        EquipmentTestWorld world = SixteenBlades();
        PlayerInventoryService inventory = EquipmentSlotTests.Create(world, out PersistenceWriteQueue writes);
        using (writes)
        {
            Assert.True(inventory.Ensure(Player));
            PlayerInventorySnapshot before = inventory.BuildSnapshot(Player);
            string heldBefore = string.Join(",", before.HeldBackItems().Select(slot => $"{slot.SlotIndex}={slot.Item!.Value.ItemInstanceId}"));

            // A 2x3 has no room (the free cells are single rows); a 1x1 fits one of them.
            Assert.Equal(
                InventoryEquipOutcome.BackpackFull,
                inventory.TryGrantRolled(Player, "gear.iron-blade", ItemRarity.Common, new ItemRollSeed(1, 2, 3, 4, 5)));
            Assert.Equal(
                InventoryEquipOutcome.Equipped,
                inventory.TryGrantRolled(Player, "gear.iron-band", ItemRarity.Common, new ItemRollSeed(1, 2, 3, 4, 6)));

            PlayerInventorySnapshot after = inventory.BuildSnapshot(Player);
            Assert.Equal(heldBefore, string.Join(",", after.HeldBackItems().Select(slot => $"{slot.SlotIndex}={slot.Item!.Value.ItemInstanceId}")));
            Assert.Equal(24, after.BagItems().First(slot => slot.Item!.Value.ItemKind == "gear.iron-band").SlotIndex);
            Assert.Equal(Enumerable.Range(200, 8), world.SlotsOf(Player).Keys.Where(row => row >= 200).Order());
        }
    }

    [Fact]
    public void TheRepair_DoesNotDependOnTheOrderTheRowsWereLoadedIn()
    {
        string Repaired(bool reversed)
        {
            EquipmentTestWorld world = SixteenBlades();
            PlayerInventoryService inventory = EquipmentSlotTests.Create(world, out PersistenceWriteQueue writes);
            using (writes)
            {
                PersistedPlayerInventory loaded = world.LoadPlayerInventory(Player);
                PersistedInventorySlot[] rows = reversed ? [.. loaded.Slots.Reverse()] : [.. loaded.Slots];
                inventory.ApplyLoaded(Player, loaded with { Slots = rows, Items = reversed ? [.. loaded.Items.Reverse()] : loaded.Items });
                Assert.True(inventory.Ensure(Player));
                return inventory.BuildSnapshot(Player).Layout();
            }
        }

        Assert.Equal(Repaired(reversed: false), Repaired(reversed: true));
        Assert.Equal(Repaired(reversed: false), Repaired(reversed: false));
    }

    [Fact]
    public void ARepairedInventory_IsRepairedOnce_AndALaterLoadFindsNothingToDo()
    {
        EquipmentTestWorld world = SixteenBlades();
        PlayerInventorySnapshot first;
        PlayerInventoryService inventory = EquipmentSlotTests.Create(world, out PersistenceWriteQueue writes);
        using (writes)
        {
            Assert.True(inventory.Ensure(Player));
            first = inventory.BuildSnapshot(Player);
            int writesAfterRepair = world.SlotWrites;
            Assert.True(inventory.Ensure(Player));
            Assert.Equal(writesAfterRepair, world.SlotWrites);
        }

        int writesBefore = world.SlotWrites;
        PlayerInventoryService reloaded = EquipmentSlotTests.Create(world, out PersistenceWriteQueue reloadedWrites);
        using (reloadedWrites)
        {
            Assert.True(reloaded.Ensure(Player));
            Assert.Equal(first.Layout(), reloaded.BuildSnapshot(Player).Layout());
            Assert.Equal(writesBefore, world.SlotWrites);
        }
    }

    [Fact]
    public void WhenAllSixteenHeldBackRowsAreTaken_TheRestAreParkedAtThreeHundredUp_AndKept()
    {
        // Forty blades piled on anchors 0..39: eight place, sixteen are held back, sixteen are parked.
        EquipmentTestWorld world = WorldWith([.. Enumerable.Range(0, 40).Select(i => ((long)(100 + i), "gear.iron-blade", i))]);
        PlayerInventoryService inventory = EquipmentSlotTests.Create(world, out PersistenceWriteQueue writes);
        using (writes)
        {
            Assert.True(inventory.Ensure(Player));

            PlayerInventorySnapshot snapshot = inventory.BuildSnapshot(Player);
            Assert.Equal(8, snapshot.BagItems().Count());
            Assert.Equal(16, snapshot.HeldBackItems().Count());
            Dictionary<int, long> rows = world.SlotsOf(Player);
            Assert.Equal(16, rows.Keys.Count(row => row >= InventoryConstants.ParkedFirstIndex));
            // Every one of the forty is still referenced by exactly one row.
            Assert.Equal(40, rows.Values.Distinct().Count());
            Assert.Equal(40, rows.Count);

            // Parked rows are kept and inert: they are not shown, and a destroy that frees room drains the
            // held-back rows only.
            Assert.Equal(InventoryEquipOutcome.Equipped, inventory.TryDestroy(Player, 0, snapshot.IdAt(0)));
            Dictionary<int, long> after = world.SlotsOf(Player);
            Assert.Equal(16, after.Keys.Count(row => row >= InventoryConstants.ParkedFirstIndex));
            Assert.Equal(15, after.Keys.Count(row => row is >= 200 and <= 215));
        }
    }

    [Fact]
    public void ARowThatIsNotOnTheBoardNorWornNorHeldBack_IsKeptAndNeverShown()
    {
        EquipmentTestWorld world = WorldWith((50, "gear.iron-blade", 20016), (51, "gear.iron-band", 112), (52, "gear.iron-band", 99), (53, "gear.iron-band", 3));
        PlayerInventoryService inventory = EquipmentSlotTests.Create(world, out PersistenceWriteQueue writes);
        using (writes)
        {
            Assert.True(inventory.Ensure(Player));

            PlayerInventorySnapshot snapshot = inventory.BuildSnapshot(Player);
            Assert.Equal([3], snapshot.BagItems().Select(slot => slot.SlotIndex));
            Assert.Equal(4, world.SlotsOf(Player).Count);
            Assert.Equal(50, world.SlotsOf(Player)[20016]);
            Assert.Equal(51, world.SlotsOf(Player)[112]);
            Assert.Equal(52, world.SlotsOf(Player)[99]);
        }
    }

    [Fact]
    public void ARowWhoseItemIsMissing_TakesOneCell_IsNotShown_AndIsNotMovedOntoByAnAction()
    {
        EquipmentTestWorld world = new();
        world.PutSlot(Player, 0, 777);
        world.PutItem(EquipmentTestWorld.MakeItem(50, Player, "gear.iron-band"));
        world.PutSlot(Player, 1, 50);
        world.PutGrant(Player, ToolCatalog.CurrentStarterKitVersion);
        PlayerInventoryService inventory = EquipmentSlotTests.Create(world, out PersistenceWriteQueue writes);
        using (writes)
        {
            Assert.True(inventory.Ensure(Player));
            Assert.Equal([1], inventory.BuildSnapshot(Player).BagItems().Select(slot => slot.SlotIndex));

            // The ring cannot be moved onto the cell the stale row holds (a 1x1 there), and the row stays.
            Assert.Equal(InventoryEquipOutcome.NoRoom, inventory.TryMove(Player, 1, 0));
            Assert.Equal(777, world.SlotsOf(Player)[0]);
        }
    }

    private static PlayerInventorySnapshot BuildFromRows(EquipmentTestWorld world)
    {
        PlayerInventoryService fresh = EquipmentSlotTests.Create(world, out PersistenceWriteQueue writes);
        using (writes)
        {
            Assert.True(fresh.Ensure(Player));
            return fresh.BuildSnapshot(Player);
        }
    }
}
