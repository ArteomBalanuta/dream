using Xunit;
using Dream.Simulation.Inventory;
using Dream.Simulation.Persistence;
using Dream.Simulation.Stats;
using Dream.Tests.Support;

namespace Dream.Tests.Simulation.Inventory;

// Classic HUD iteration 2: the rules of move, swap, equip, unequip and the ring pair on the 8x8 footprint
// bag (D10, D11, D13), each with the rows the action queued: the rows in the repository say exactly what
// the inventory in memory says after every outcome, accepted or not.
public sealed class BagActionRulesTests
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

    // What the repository holds for the player is what the inventory shows: every item of the snapshot in the
    // row of its slot, and no other row.
    private static void AssertRowsMatch(EquipmentTestWorld world, PlayerInventoryService inventory)
    {
        PlayerInventorySnapshot snapshot = inventory.BuildSnapshot(Player);
        Dictionary<int, long> expected = [];
        foreach (InventorySlotSnapshot slot in snapshot.Slots.Where(slot => slot.Item != null))
        {
            expected[slot.SlotIndex] = slot.Item!.Value.ItemInstanceId;
        }

        if (snapshot.EquippedSlot?.Item is { } main)
        {
            expected[EquipmentSlots.FirstSlotIndex] = main.ItemInstanceId;
        }

        foreach (InventorySlotSnapshot slot in snapshot.Equipment ?? [])
        {
            expected[slot.SlotIndex] = slot.Item!.Value.ItemInstanceId;
        }

        Assert.Equal(expected.OrderBy(pair => pair.Key), world.SlotsOf(Player).OrderBy(pair => pair.Key));
        Assert.True(snapshot.BoardIsConsistent());
    }

    private static PlayerInventoryService Open(EquipmentTestWorld world, out PersistenceWriteQueue writes)
    {
        PlayerInventoryService inventory = EquipmentSlotTests.Create(world, out writes);
        Assert.True(inventory.Ensure(Player));
        return inventory;
    }

    // ---- move ----

    [Fact]
    public void AMoveToFreeCells_MovesTheAnchor_DeletingTheOldRowAndWritingTheNewOne()
    {
        EquipmentTestWorld world = WorldWith((50, "gear.iron-blade", 0));
        PlayerInventoryService inventory = Open(world, out PersistenceWriteQueue writes);
        using (writes)
        {
            Assert.Equal(InventoryEquipOutcome.Equipped, inventory.TryMove(Player, 0, 24));

            Assert.Equal(new Dictionary<int, long> { [24] = 50 }, world.SlotsOf(Player));
            AssertRowsMatch(world, inventory);
        }
    }

    [Fact]
    public void AShortShiftInsideTheItemsOwnArea_IsAPlainMove()
    {
        // The new area overlaps the old one; its own old cells are not obstacles.
        EquipmentTestWorld world = WorldWith((50, "gear.iron-blade", 0));
        PlayerInventoryService inventory = Open(world, out PersistenceWriteQueue writes);
        using (writes)
        {
            Assert.Equal(InventoryEquipOutcome.Equipped, inventory.TryMove(Player, 0, 1));
            Assert.Equal(new Dictionary<int, long> { [1] = 50 }, world.SlotsOf(Player));
            Assert.Equal(InventoryEquipOutcome.Equipped, inventory.TryMove(Player, 1, 8));
            Assert.Equal(new Dictionary<int, long> { [8] = 50 }, world.SlotsOf(Player));
            AssertRowsMatch(world, inventory);
        }
    }

    [Fact]
    public void ASwapOfTwoItemsOfTheSameSize_ExchangesTheirAnchors()
    {
        EquipmentTestWorld world = WorldWith((50, "gear.iron-blade", 0), (51, "tool.stone-maul", 2));
        PlayerInventoryService inventory = Open(world, out PersistenceWriteQueue writes);
        using (writes)
        {
            Assert.Equal(InventoryEquipOutcome.Equipped, inventory.TryMove(Player, 0, 2));

            Assert.Equal(new Dictionary<int, long> { [0] = 51, [2] = 50 }, world.SlotsOf(Player));
            AssertRowsMatch(world, inventory);
        }
    }

    [Fact]
    public void ASwapWithASmallerItem_SendsItToTheMoversOldAnchor()
    {
        // The ring sits under the blade's new area (cols 2-3); the blade's old anchor is free for it.
        EquipmentTestWorld world = WorldWith((50, "gear.iron-blade", 0), (51, "gear.iron-band", 10));
        PlayerInventoryService inventory = Open(world, out PersistenceWriteQueue writes);
        using (writes)
        {
            Assert.Equal(InventoryEquipOutcome.Equipped, inventory.TryMove(Player, 0, 2));

            Assert.Equal(new Dictionary<int, long> { [2] = 50, [0] = 51 }, world.SlotsOf(Player));
            AssertRowsMatch(world, inventory);
        }
    }

    [Fact]
    public void ASwapWhereTheOtherCannotGoToTheMoversOldAnchor_TakesTheFirstFit()
    {
        // The cap (2x2) moves onto the blade (2x3). The blade cannot take the cap's old anchor, because a
        // ring below the cap is in the way, so it goes to the first fit: anchor 4.
        EquipmentTestWorld world = WorldWith((50, "gear.leather-cap", 0), (51, "gear.iron-blade", 2), (52, "gear.iron-band", 16));
        PlayerInventoryService inventory = Open(world, out PersistenceWriteQueue writes);
        using (writes)
        {
            Assert.Equal(InventoryEquipOutcome.Equipped, inventory.TryMove(Player, 0, 2));

            Assert.Equal(new Dictionary<int, long> { [2] = 50, [4] = 51, [16] = 52 }, world.SlotsOf(Player));
            AssertRowsMatch(world, inventory);
        }
    }

    [Fact]
    public void ASwapThatLeavesTheOtherNowhere_IsNoRoom_AndChangesNothing()
    {
        // A board of rings with one blade: the blade moves onto a ring and the ring would have to go to the
        // blade's old cells, but a ring (1x1) fits there; make the displaced item the big one instead: the
        // ring moves onto the blade, which cannot fit anywhere around.
        List<(long, string, int)> rows = [(50, "gear.iron-blade", 0)];
        for (int cell = 0; cell < BagGrid.CellCount; cell++)
        {
            if (cell is not (0 or 1 or 8 or 9 or 16 or 17))
            {
                rows.Add((200 + cell, "gear.iron-band", cell));
            }
        }

        // The ring at 60 moves onto the blade's anchor: the blade has no 2x3 hole anywhere but under the ring.
        EquipmentTestWorld world = WorldWith([.. rows]);
        PlayerInventoryService inventory = Open(world, out PersistenceWriteQueue writes);
        using (writes)
        {
            Dictionary<int, long> before = world.SlotsOf(Player);
            int writesBefore = world.SlotWrites;

            Assert.Equal(InventoryEquipOutcome.NoRoom, inventory.TryMove(Player, 60, 0));

            Assert.Equal(before, world.SlotsOf(Player));
            Assert.Equal(writesBefore, world.SlotWrites);
            AssertRowsMatch(world, inventory);
        }
    }

    [Theory]
    [InlineData(0, 5)]   // the blade over the maul and the lantern: two items
    [InlineData(0, 7)]   // a 2-wide blade at the last column
    [InlineData(0, 62)]  // a 3-tall blade at the last but one row
    [InlineData(0, 63)]
    public void AMoveOverTwoItemsOrOffTheBoard_IsNoRoom_SpendsNoBudget_AndChangesNothing(int from, int to)
    {
        EquipmentTestWorld world = WorldWith((50, "gear.iron-blade", 0), (51, "tool.stone-maul", 4), (52, "tool.survey-lantern", 6));
        PlayerInventoryService inventory = Open(world, out PersistenceWriteQueue writes);
        using (writes)
        {
            int spent = 0;
            inventory.ActionGate = () =>
            {
                spent++;
                return true;
            };
            Dictionary<int, long> before = world.SlotsOf(Player);
            int writesBefore = world.SlotWrites;

            Assert.Equal(InventoryEquipOutcome.NoRoom, inventory.TryMove(Player, from, to));

            Assert.Equal(0, spent);
            Assert.Equal(before, world.SlotsOf(Player));
            Assert.Equal(writesBefore, world.SlotWrites);
            AssertRowsMatch(world, inventory);
        }
    }

    [Fact]
    public void AMoveOntoACellACoveredItemOwns_IsASwapWithThatItem()
    {
        // Cell 9 belongs to the blade at 0 (its second column, second row); dropping a ring there swaps.
        EquipmentTestWorld world = WorldWith((50, "gear.iron-blade", 0), (51, "gear.iron-band", 40));
        PlayerInventoryService inventory = Open(world, out PersistenceWriteQueue writes);
        using (writes)
        {
            Assert.Equal(InventoryEquipOutcome.Equipped, inventory.TryMove(Player, 40, 9));

            // The ring took cell 9; the blade could not stay (the ring is inside its area) and was moved.
            PlayerInventorySnapshot snapshot = inventory.BuildSnapshot(Player);
            Assert.Equal(51, snapshot.IdAt(9));
            Assert.Equal(50, snapshot.IdAt(snapshot.AnchorOf("gear.iron-blade")));
            AssertRowsMatch(world, inventory);
        }
    }

    [Fact]
    public void AMoveOntoItself_IsAcceptedAndWritesNothing_AndAMoveFromACoveredCellIsRefused()
    {
        EquipmentTestWorld world = WorldWith((50, "gear.iron-blade", 0));
        PlayerInventoryService inventory = Open(world, out PersistenceWriteQueue writes);
        using (writes)
        {
            int writesBefore = world.SlotWrites;

            Assert.Equal(InventoryEquipOutcome.Equipped, inventory.TryMove(Player, 0, 0));
            Assert.Equal(InventoryEquipOutcome.SlotEmpty, inventory.TryMove(Player, 9, 24));

            Assert.Equal(writesBefore, world.SlotWrites);
        }
    }

    [Fact]
    public void AThrottledMove_ChangesNothing_AndQueuesNothing()
    {
        EquipmentTestWorld world = WorldWith((50, "gear.iron-blade", 0));
        PlayerInventoryService inventory = Open(world, out PersistenceWriteQueue writes);
        using (writes)
        {
            inventory.ActionGate = () => false;
            int writesBefore = world.SlotWrites;

            Assert.Equal(InventoryEquipOutcome.Throttled, inventory.TryMove(Player, 0, 24));

            Assert.Equal(writesBefore, world.SlotWrites);
            Assert.Equal(new Dictionary<int, long> { [0] = 50 }, world.SlotsOf(Player));
            AssertRowsMatch(world, inventory);
        }
    }

    // ---- equip and unequip ----

    [Fact]
    public void AnEquipToAnEmptySlot_DeletesTheBagRowAndWritesTheWornRow()
    {
        EquipmentTestWorld world = WorldWith((50, "gear.leather-cap", 0));
        PlayerInventoryService inventory = Open(world, out PersistenceWriteQueue writes);
        using (writes)
        {
            Assert.Equal(InventoryEquipOutcome.Equipped, inventory.TryEquip(Player, 0));

            Assert.Equal(new Dictionary<int, long> { [102] = 50 }, world.SlotsOf(Player));
            AssertRowsMatch(world, inventory);
        }
    }

    [Fact]
    public void ADisplacedItem_TakesTheAnchorTheEquippedItemLeft_WhenItFitsThere()
    {
        EquipmentTestWorld world = WorldWith((50, "gear.leather-cap", 0), (51, "gear.leather-cap", 102));
        PlayerInventoryService inventory = Open(world, out PersistenceWriteQueue writes);
        using (writes)
        {
            Assert.Equal(InventoryEquipOutcome.Equipped, inventory.TryEquip(Player, 0));

            Assert.Equal(new Dictionary<int, long> { [0] = 51, [102] = 50 }, world.SlotsOf(Player));
            AssertRowsMatch(world, inventory);
        }
    }

    [Fact]
    public void ADisplacedItemThatDoesNotFitTheAnchor_TakesTheFirstFit()
    {
        // The torch (1x2) at 0 goes to the main hand and displaces the hammer (2x3), which does not fit at 0
        // because a ring sits at 1: it takes the first fit, anchor 2.
        EquipmentTestWorld world = WorldWith((50, "tool.torch", 0), (51, "gear.iron-band", 1), (52, "tool.builder-hammer", 100));
        PlayerInventoryService inventory = Open(world, out PersistenceWriteQueue writes);
        using (writes)
        {
            Assert.Equal(InventoryEquipOutcome.Equipped, inventory.TryEquip(Player, 0));

            Assert.Equal(new Dictionary<int, long> { [100] = 50, [1] = 51, [2] = 52 }, world.SlotsOf(Player));
            AssertRowsMatch(world, inventory);
        }
    }

    [Fact]
    public void AnUnequipToAFreeCell_LandsThere_AndToATakenCellIsNoRoom_WithNothingChanged()
    {
        EquipmentTestWorld world = WorldWith((50, "gear.leather-cap", 102), (51, "gear.iron-band", 9));
        PlayerInventoryService inventory = Open(world, out PersistenceWriteQueue writes);
        using (writes)
        {
            Dictionary<int, long> before = world.SlotsOf(Player);
            int writesBefore = world.SlotWrites;

            // The cap is 2x2: at 8 it would cover cell 9 (the ring); at 63 it would run off the board.
            Assert.Equal(InventoryEquipOutcome.NoRoom, inventory.TryUnequip(Player, 102, 8));
            Assert.Equal(InventoryEquipOutcome.NoRoom, inventory.TryUnequip(Player, 102, 63));
            Assert.Equal(before, world.SlotsOf(Player));
            Assert.Equal(writesBefore, world.SlotWrites);

            Assert.Equal(InventoryEquipOutcome.Equipped, inventory.TryUnequip(Player, 102, 10));
            Assert.Equal(new Dictionary<int, long> { [10] = 50, [9] = 51 }, world.SlotsOf(Player));
            AssertRowsMatch(world, inventory);
        }
    }

    // ---- worn slot to worn slot ----

    [Fact]
    public void AWornItemMovesToAnEmptySlot_ItsMaskAllows()
    {
        EquipmentTestWorld world = WorldWith((50, "tool.torch", 100));
        PlayerInventoryService inventory = Open(world, out PersistenceWriteQueue writes);
        using (writes)
        {
            Assert.Equal(InventoryEquipOutcome.Equipped, inventory.TryEquip(Player, 100, 101));

            Assert.Equal(new Dictionary<int, long> { [101] = 50 }, world.SlotsOf(Player));
            AssertRowsMatch(world, inventory);
        }
    }

    [Fact]
    public void ALanternAndATorchSwapBetweenTheHands_WhenBothMayHoldBoth()
    {
        EquipmentTestWorld world = WorldWith((50, "tool.torch", 100), (51, "tool.survey-lantern", 101));
        PlayerInventoryService inventory = Open(world, out PersistenceWriteQueue writes);
        using (writes)
        {
            Assert.Equal(InventoryEquipOutcome.Equipped, inventory.TryEquip(Player, 100, 101));

            Assert.Equal(new Dictionary<int, long> { [100] = 51, [101] = 50 }, world.SlotsOf(Player));
            AssertRowsMatch(world, inventory);
        }
    }

    [Fact]
    public void AWornSwapThatOneItemsMaskForbids_IsNotEquippable_AndChangesNothing()
    {
        // The blade is main hand only; the lantern is in the off hand. Neither direction can swap them.
        EquipmentTestWorld world = WorldWith((50, "gear.iron-blade", 100), (51, "gear.ward-lantern", 101));
        PlayerInventoryService inventory = Open(world, out PersistenceWriteQueue writes);
        using (writes)
        {
            Dictionary<int, long> before = world.SlotsOf(Player);
            int writesBefore = world.SlotWrites;
            int spent = 0;
            inventory.ActionGate = () =>
            {
                spent++;
                return true;
            };

            Assert.Equal(InventoryEquipOutcome.NotEquippable, inventory.TryEquip(Player, 100, 101));
            Assert.Equal(InventoryEquipOutcome.NotEquippable, inventory.TryEquip(Player, 101, 100));
            Assert.Equal(InventoryEquipOutcome.NotEquippable, inventory.TryEquip(Player, 100, 102));
            Assert.Equal(InventoryEquipOutcome.NotEquippable, inventory.TryEquip(Player, 100));

            Assert.Equal(0, spent);
            Assert.Equal(before, world.SlotsOf(Player));
            Assert.Equal(writesBefore, world.SlotWrites);
        }
    }

    [Fact]
    public void ARowOfAWornSlot_WithNoItemToMove_IsSlotEmpty_AndWornToWornNeedsAWornTarget()
    {
        EquipmentTestWorld world = WorldWith((50, "tool.torch", 100));
        PlayerInventoryService inventory = Open(world, out PersistenceWriteQueue writes);
        using (writes)
        {
            Assert.Equal(InventoryEquipOutcome.SlotEmpty, inventory.TryEquip(Player, 105, 101));
            Assert.Equal(InventoryEquipOutcome.TargetOutOfRange, inventory.TryEquip(Player, 100, 0));
            Assert.Equal(InventoryEquipOutcome.TargetOutOfRange, inventory.TryEquip(Player, 100, 112));
            Assert.Equal(InventoryEquipOutcome.SlotOutOfRange, inventory.TryEquip(Player, 112, 100));
            Assert.Equal(InventoryEquipOutcome.Equipped, inventory.TryEquip(Player, 100, 100));
        }
    }

    // ---- the ring pair (D13) ----

    [Fact]
    public void RingsWithNoTarget_FillTheFirstEmptyRingSlot_AndTheThirdDisplacesTheLeftOne()
    {
        EquipmentTestWorld world = WorldWith((50, "gear.iron-band", 0), (51, "gear.iron-band", 1), (52, "gear.iron-band", 2));
        PlayerInventoryService inventory = Open(world, out PersistenceWriteQueue writes);
        using (writes)
        {
            Assert.Equal(InventoryEquipOutcome.Equipped, inventory.TryEquip(Player, 0));
            Assert.Equal(new Dictionary<int, long> { [108] = 50, [1] = 51, [2] = 52 }, world.SlotsOf(Player));

            Assert.Equal(InventoryEquipOutcome.Equipped, inventory.TryEquip(Player, 1));
            Assert.Equal(new Dictionary<int, long> { [108] = 50, [109] = 51, [2] = 52 }, world.SlotsOf(Player));

            // Both ring slots are taken: the third goes to the left ring slot and the ring that was there
            // takes the third one's anchor.
            Assert.Equal(InventoryEquipOutcome.Equipped, inventory.TryEquip(Player, 2));
            Assert.Equal(new Dictionary<int, long> { [108] = 52, [109] = 51, [2] = 50 }, world.SlotsOf(Player));
            AssertRowsMatch(world, inventory);
        }
    }

    [Fact]
    public void ARingWithTheLeftSlotFree_AndARightOne_GoesLeft_AndAnExplicitTargetIsHonoured()
    {
        EquipmentTestWorld world = WorldWith((50, "gear.iron-band", 0), (51, "gear.iron-band", 1));
        PlayerInventoryService inventory = Open(world, out PersistenceWriteQueue writes);
        using (writes)
        {
            Assert.Equal(InventoryEquipOutcome.Equipped, inventory.TryEquip(Player, 0, 109));
            Assert.Equal(new Dictionary<int, long> { [109] = 50, [1] = 51 }, world.SlotsOf(Player));

            // Left is free, right is taken: no target means the left slot.
            Assert.Equal(InventoryEquipOutcome.Equipped, inventory.TryEquip(Player, 1));
            Assert.Equal(new Dictionary<int, long> { [109] = 50, [108] = 51 }, world.SlotsOf(Player));
            AssertRowsMatch(world, inventory);
        }
    }

    [Fact]
    public void ADragFromOneRingSlotToTheOther_SwapsThem()
    {
        EquipmentTestWorld world = WorldWith((50, "gear.iron-band", 108), (51, "gear.iron-band", 109));
        PlayerInventoryService inventory = Open(world, out PersistenceWriteQueue writes);
        using (writes)
        {
            Assert.Equal(InventoryEquipOutcome.Equipped, inventory.TryEquip(Player, 108, 109));

            Assert.Equal(new Dictionary<int, long> { [108] = 51, [109] = 50 }, world.SlotsOf(Player));
            AssertRowsMatch(world, inventory);
        }
    }

    [Fact]
    public void AnItemThatIsNotARing_KeepsTheFirstListedSlotRule()
    {
        // A lantern lists the main hand first: with the main hand taken and the off hand free, it still goes
        // to the main hand and displaces what is there (H-D8).
        EquipmentTestWorld world = WorldWith((50, "tool.survey-lantern", 0), (51, "tool.torch", 100));
        PlayerInventoryService inventory = Open(world, out PersistenceWriteQueue writes);
        using (writes)
        {
            Assert.Equal(InventoryEquipOutcome.Equipped, inventory.TryEquip(Player, 0));

            Assert.Equal(new Dictionary<int, long> { [100] = 50, [0] = 51 }, world.SlotsOf(Player));
        }
    }

    [Fact]
    public void TheSameRingInBothRingSlots_OnlyAHandEditCanDoThat_CountsOnce()
    {
        ItemRoll roll = new(ItemRarity.Rare, [new StatModifier(StatId.Strength, StatOp.Flat, 3f)]);
        EquipmentTestWorld Make(params int[] slots)
        {
            EquipmentTestWorld world = new();
            world.PutItem(EquipmentTestWorld.MakeItem(50, Player, "gear.iron-band", roll));
            foreach (int slot in slots)
            {
                world.PutSlot(Player, slot, 50);
            }

            world.PutGrant(Player, ToolCatalog.CurrentStarterKitVersion);
            return world;
        }

        (StatModifier[] Left, StatModifier[] Right, ulong Digest) Read(EquipmentTestWorld world)
        {
            PlayerInventoryService inventory = Open(world, out PersistenceWriteQueue writes);
            using (writes)
            {
                EquipmentLoadout loadout = new();
                inventory.CopyEquipment(Player, loadout);
                return (loadout.Get(EquipmentSlot.RingLeft).ToArray(), loadout.Get(EquipmentSlot.RingRight).ToArray(), inventory.ResolveModifierDigest(Player));
            }
        }

        (StatModifier[] left, StatModifier[] right, ulong digest) = Read(Make(108, 109));
        (StatModifier[] oneLeft, _, ulong oneDigest) = Read(Make(108));

        // The lowest slot keeps the item's stats; the second well adds nothing, to the loadout or the digest.
        Assert.Equal([new StatModifier(StatId.Strength, StatOp.Flat, 3f)], left);
        Assert.Empty(right);
        Assert.Equal(oneLeft, left);
        Assert.Equal(oneDigest, digest);
        Assert.NotEqual(0UL, digest);
    }

    [Fact]
    public void TwoDifferentRings_StackTheirStats_InSeparateSources()
    {
        ItemRoll roll = new(ItemRarity.Rare, [new StatModifier(StatId.Strength, StatOp.Flat, 3f)]);
        EquipmentTestWorld world = new();
        world.PutItem(EquipmentTestWorld.MakeItem(50, Player, "gear.iron-band", roll));
        world.PutItem(EquipmentTestWorld.MakeItem(51, Player, "gear.iron-band", roll));
        world.PutSlot(Player, 108, 50);
        world.PutSlot(Player, 109, 51);
        world.PutGrant(Player, ToolCatalog.CurrentStarterKitVersion);
        PlayerInventoryService inventory = Open(world, out PersistenceWriteQueue writes);
        using (writes)
        {
            EquipmentLoadout loadout = new();
            inventory.CopyEquipment(Player, loadout);

            Assert.Equal([new StatModifier(StatId.Strength, StatOp.Flat, 3f)], loadout.Get(EquipmentSlot.RingLeft).ToArray());
            Assert.Equal([new StatModifier(StatId.Strength, StatOp.Flat, 3f)], loadout.Get(EquipmentSlot.RingRight).ToArray());
        }
    }

    // ---- the starter kit, with the alias gone ----

    private static int Count(EquipmentTestWorld world, string kind) => world.LoadItemInstances().Count(item => item.ItemKind == kind);

    [Fact]
    public void ALegacyPlayerWithEverythingWorn_IsNotSeededASecondKit()
    {
        // Four kit items, all of them worn (so none has a bag row), and no grant record: HasBackpackContent
        // must count the worn rows, or this player is taken for a new one and gets the whole kit again.
        EquipmentTestWorld world = new();
        string[] kinds = ["tool.builder-hammer", "tool.field-blade", "tool.stone-maul", "tool.survey-lantern"];
        for (int i = 0; i < kinds.Length; i++)
        {
            world.PutItem(EquipmentTestWorld.MakeItem(30 + i, Player, kinds[i]));
            world.PutSlot(Player, 100 + i, 30 + i);
        }

        PlayerInventoryService inventory = Open(world, out PersistenceWriteQueue writes);
        using (writes)
        {
            Assert.All(kinds, kind => Assert.Equal(1, Count(world, kind)));
            // Only the torch, which came after the kit this player is taken to hold, is granted.
            Assert.Equal(1, Count(world, "tool.torch"));
            Assert.Equal(5, world.LoadItemInstances().Count);
            Assert.Equal(ToolCatalog.CurrentStarterKitVersion, world.LoadPlayerInventory(Player).StarterKitVersion);
        }
    }

    [Fact]
    public void AWornTorch_CountsAsHeld_SoTheTopUpDoesNotGrantASecond()
    {
        EquipmentTestWorld world = new();
        world.PutItem(EquipmentTestWorld.MakeItem(30, Player, "tool.builder-hammer"));
        world.PutItem(EquipmentTestWorld.MakeItem(31, Player, "tool.torch"));
        world.PutSlot(Player, 4, 30);
        world.PutSlot(Player, 100, 31);

        PlayerInventoryService inventory = Open(world, out PersistenceWriteQueue writes);
        using (writes)
        {
            Assert.Equal(1, Count(world, "tool.torch"));
            Assert.Equal(2, world.LoadItemInstances().Count);
        }
    }

    [Fact]
    public void ASeedCutShortWithItsFirstItemWorn_IsStillTakenForACutShortSeed_AndGetsOnlyTheMissingKinds()
    {
        // The hammer (worn) and the blade (bag) are the first two kit kinds: what an interrupted old seed left,
        // with its first item equipped. The missing kinds arrive once and nothing is duplicated.
        EquipmentTestWorld world = new();
        world.PutItem(EquipmentTestWorld.MakeItem(30, Player, "tool.builder-hammer"));
        world.PutItem(EquipmentTestWorld.MakeItem(31, Player, "tool.field-blade"));
        world.PutSlot(Player, 100, 30);
        world.PutSlot(Player, 2, 31);

        PlayerInventoryService inventory = Open(world, out PersistenceWriteQueue writes);
        using (writes)
        {
            foreach (string kind in ToolCatalog.StarterItemKinds)
            {
                Assert.Equal(1, Count(world, kind));
            }

            Assert.True(inventory.Ensure(Player));
            Assert.Equal(ToolCatalog.StarterItemKinds.Count, world.LoadItemInstances().Count);
        }
    }

    [Fact]
    public void AMaulAloneIsNotAKitPrefix_SoItGetsNoOldKinds_WhetherWornOrInTheBag()
    {
        foreach (int slot in new[] { 100, 5 })
        {
            EquipmentTestWorld world = new();
            world.PutItem(EquipmentTestWorld.MakeItem(30, Player, "tool.stone-maul"));
            world.PutSlot(Player, slot, 30);

            PlayerInventoryService inventory = Open(world, out PersistenceWriteQueue writes);
            using (writes)
            {
                Assert.Equal(1, Count(world, "tool.stone-maul"));
                Assert.Equal(0, Count(world, "tool.builder-hammer"));
                Assert.Equal(1, Count(world, "tool.torch"));
            }
        }
    }

    [Fact]
    public void TheFirstSightEquip_MovesTheRow_ItDoesNotAliasIt()
    {
        EquipmentTestWorld world = new();
        world.PutItem(EquipmentTestWorld.MakeItem(30, Player, "tool.field-blade"));
        world.PutSlot(Player, 2, 30);

        PlayerInventoryService inventory = Open(world, out PersistenceWriteQueue writes);
        using (writes)
        {
            Dictionary<int, long> rows = world.SlotsOf(Player);
            Assert.Equal(30, rows[100]);
            Assert.False(rows.ContainsKey(2));
            Assert.Equal(1, rows.Values.Count(id => id == 30));
        }
    }

    // ---- the board ----

    [Fact]
    public void TheBoard_PlacesFirstComeFirstKept_AndPlansLikeBagGrid()
    {
        BagBoard board = new();
        Assert.True(board.TryPlace(0, new ItemFootprint(2, 3)));
        Assert.False(board.TryPlace(9, new ItemFootprint(2, 2)));
        Assert.False(board.TryPlace(7, new ItemFootprint(2, 1)));
        Assert.False(board.TryPlace(64, ItemFootprint.One));
        Assert.False(board.TryPlace(-1, ItemFootprint.One));
        Assert.True(board.TryPlace(2, new ItemFootprint(1, 2)));
        Assert.Equal(2, board.ItemCount);
        Assert.True(board.IsAnchor(0));
        Assert.False(board.IsAnchor(9));
        Assert.True(board.Fits(3, ItemFootprint.One));
        Assert.False(board.Fits(2, ItemFootprint.One));
        Assert.Equal(3, board.FirstFit(new ItemFootprint(2, 3)));
        Assert.Equal(BagGrid.PlanMove(board.Owners, board.Footprints, 0, 2), board.PlanMove(0, 2));

        Assert.True(board.Remove(0));
        Assert.False(board.Remove(0));
        Assert.Equal(0, board.FirstFit(new ItemFootprint(2, 3)));
        Assert.Equal(1, board.ItemCount);
        board.Clear();
        Assert.Equal(0UL, board.Occupied);
    }
}
