using Xunit;
using Dream.Multiplayer.Sessions;
using Dream.Server.Persistence;
using Dream.Simulation.Inventory;
using Dream.Simulation.Persistence;
using Dream.Simulation.Stats;
using Dream.Simulation.World;
using Dream.Tests.Support;

namespace Dream.Tests.Simulation.Inventory;

// Wave H1: move, unequip, destroy and equip on PlayerInventoryService - auto-equip only on first sight
// (H-D3), the action budget (H-D18) and the cached equipment summary. Classic HUD iteration 2: the
// bag is a footprint board and a worn item has no bag row (the wave H alias is gone), so a move never
// touches a worn row and an unequip puts the item at the first fit. A new player's kit: the hammer in the
// main hand, the blade at anchor 2, the maul at 4, the lantern at 6 and the torch at 7.
public sealed class InventoryActionTests
{
    private const string Player = "Alice";

    private static PlayerInventoryService Create(EquipmentTestWorld world, out PersistenceWriteQueue writes)
        => EquipmentSlotTests.Create(world, out writes);

    // Every item of a snapshot with where it is: the board by anchor, held back, and the worn slots.
    private static string Layout(PlayerInventorySnapshot snapshot)
        => string.Join(
            ";",
            snapshot.Slots.Select(slot => $"{slot.SlotIndex}={slot.Item?.ItemInstanceId}")
                .Append($"worn100={snapshot.EquippedSlot?.Item?.ItemInstanceId}")
                .Concat((snapshot.Equipment ?? []).Select(slot => $"worn{slot.SlotIndex}={slot.Item?.ItemInstanceId}")));

    private static long MainHandId(PlayerInventoryService inventory)
        => inventory.BuildSnapshot(Player).EquippedSlot!.Value.Item?.ItemInstanceId ?? 0;

    [Fact]
    public void MovingAnItem_ChangesItsAnchor_LeavesTheWornRowsAlone_AndSurvivesARestart()
    {
        EquipmentTestWorld world = new();
        PlayerInventoryService inventory = Create(world, out PersistenceWriteQueue writes);
        using (writes)
        {
            Assert.True(inventory.Ensure(Player));
            long hammerId = MainHandId(inventory);
            long bladeId = inventory.BuildSnapshot(Player).IdAt(2);

            // The blade is a 2x3: rows 3-5, columns 0-1 are free.
            Assert.Equal(InventoryEquipOutcome.Equipped, inventory.TryMove(Player, 2, 24));

            PlayerInventorySnapshot snapshot = inventory.BuildSnapshot(Player);
            Assert.Equal(bladeId, snapshot.IdAt(24));
            Assert.Null(snapshot.ItemAt(2));
            Assert.Equal(hammerId, MainHandId(inventory));
            Assert.True(snapshot.BoardIsConsistent());

            Dictionary<int, long> rows = world.SlotsOf(Player);
            Assert.False(rows.ContainsKey(2));
            Assert.Equal(bladeId, rows[24]);
            Assert.Equal(hammerId, rows[100]);

            PlayerInventoryService restarted = Create(world, out PersistenceWriteQueue restartWrites);
            using (restartWrites)
            {
                Assert.True(restarted.Ensure(Player));
                Assert.Equal(hammerId, MainHandId(restarted));
                Assert.Equal(bladeId, restarted.BuildSnapshot(Player).IdAt(24));
            }
        }
    }

    [Fact]
    public void SwapOfTwoBagItems_Works_AndTheWornRowsAreUntouched()
    {
        EquipmentTestWorld world = new();
        PlayerInventoryService inventory = Create(world, out PersistenceWriteQueue writes);
        using (writes)
        {
            Assert.True(inventory.Ensure(Player));
            PlayerInventorySnapshot before = inventory.BuildSnapshot(Player);
            long blade = before.IdAt(2);
            long maul = before.IdAt(4);
            long hammer = MainHandId(inventory);

            // Both are 2x3: the blade takes the maul's place and the maul goes to the blade's.
            Assert.Equal(InventoryEquipOutcome.Equipped, inventory.TryMove(Player, 2, 4));

            PlayerInventorySnapshot after = inventory.BuildSnapshot(Player);
            Assert.Equal(blade, after.IdAt(4));
            Assert.Equal(maul, after.IdAt(2));
            Assert.Equal(hammer, MainHandId(inventory));
            Dictionary<int, long> rows = world.SlotsOf(Player);
            Assert.Equal(blade, rows[4]);
            Assert.Equal(maul, rows[2]);
            Assert.Equal(hammer, rows[100]);
        }
    }

    [Fact]
    public void Unequip_PutsTheItemAtTheFirstFit_AndSurvivesEnsureSnapshotAndRestart()
    {
        EquipmentTestWorld world = new();
        PlayerInventoryService inventory = Create(world, out PersistenceWriteQueue writes);
        using (writes)
        {
            Assert.True(inventory.Ensure(Player));
            long hammer = MainHandId(inventory);

            Assert.Equal(InventoryEquipOutcome.Equipped, inventory.TryUnequip(Player, 100));

            Assert.Equal(0, MainHandId(inventory));
            Assert.Equal(hammer, inventory.BuildSnapshot(Player).IdAt(0));
            Assert.All(inventory.BuildSnapshot(Player).Slots, slot => Assert.False(slot.IsEquipped));
            Assert.False(world.SlotsOf(Player).ContainsKey(100));
            Assert.Equal(hammer, world.SlotsOf(Player)[0]);

            // The empty hand is not refilled by Ensure (H-D3), nor by a rejoin or a restart.
            Assert.True(inventory.Ensure(Player));
            Assert.Equal(0, MainHandId(inventory));
            PlayerInventoryService restarted = Create(world, out PersistenceWriteQueue restartWrites);
            using (restartWrites)
            {
                Assert.True(restarted.Ensure(Player));
                Assert.Equal(0, MainHandId(restarted));
                Assert.Equal(hammer, restarted.BuildSnapshot(Player).IdAt(0));
            }
        }
    }

    [Fact]
    public void NewPlayer_HasTheHammerEquipped_AndALegacyUnrecordedInventoryEquipsItsFirstItem()
    {
        EquipmentTestWorld world = new();
        PlayerInventoryService fresh = Create(world, out PersistenceWriteQueue writes);
        using (writes)
        {
            Assert.True(fresh.Ensure(Player));
            Assert.Equal("tool.builder-hammer", fresh.BuildSnapshot(Player).EquippedSlot!.Value.Item!.Value.ItemKind);
        }

        EquipmentTestWorld legacyWorld = new();
        legacyWorld.PutItem(EquipmentTestWorld.MakeItem(30, "Bob", "tool.field-blade"));
        legacyWorld.PutSlot("Bob", 2, 30);
        PlayerInventoryService legacy = Create(legacyWorld, out PersistenceWriteQueue legacyWrites);
        using (legacyWrites)
        {
            Assert.True(legacy.Ensure("Bob"));
            Dictionary<int, long> bob = legacyWorld.SlotsOf("Bob");
            Assert.Equal(30L, bob[100]);
            // The item moved into the main hand: it has no bag row any more.
            Assert.False(bob.TryGetValue(2, out long atTwo) && atTwo == 30);
        }
    }

    [Fact]
    public void Unequip_TakesTheFirstFitThatIsFree_NotAnOccupiedAnchor()
    {
        EquipmentTestWorld world = new();
        world.PutItem(EquipmentTestWorld.MakeItem(90, Player, "tool.torch"));
        world.PutItem(EquipmentTestWorld.MakeItem(91, Player, "tool.builder-hammer"));
        world.PutSlot(Player, 100, 90);
        world.PutSlot(Player, 0, 91);
        world.PutGrant(Player, ToolCatalog.CurrentStarterKitVersion);
        PlayerInventoryService inventory = Create(world, out PersistenceWriteQueue writes);
        using (writes)
        {
            Assert.True(inventory.Ensure(Player));

            Assert.Equal(InventoryEquipOutcome.Equipped, inventory.TryUnequip(Player, 100));

            // The hammer is a 2x3 at anchor 0 (cells 0, 1, 8, 9, 16 and 17), so the torch goes to 2.
            Dictionary<int, long> rows = world.SlotsOf(Player);
            Assert.Equal(90L, rows[2]);
            Assert.Equal(91L, rows[0]);
            Assert.False(rows.ContainsKey(100));
        }
    }

    [Fact]
    public void Unequip_WithAFullBag_IsRejected_AndTheItemIsNeverLost()
    {
        EquipmentTestWorld world = new();
        world.PutItem(EquipmentTestWorld.MakeItem(90, Player, "tool.torch"));
        world.PutSlot(Player, 100, 90);
        // 64 rings fill every cell of the board.
        for (int slot = 0; slot < InventoryConstants.BackpackSlotCount; slot++)
        {
            world.PutItem(EquipmentTestWorld.MakeItem(200 + slot, Player, "gear.iron-band"));
            world.PutSlot(Player, slot, 200 + slot);
        }

        world.PutGrant(Player, ToolCatalog.CurrentStarterKitVersion);
        PlayerInventoryService inventory = Create(world, out PersistenceWriteQueue writes);
        using (writes)
        {
            Assert.True(inventory.Ensure(Player));
            Dictionary<int, long> before = world.SlotsOf(Player);
            int writesBefore = world.SlotWrites;

            Assert.Equal(InventoryEquipOutcome.BackpackFull, inventory.TryUnequip(Player, 100));

            Assert.Equal(before, world.SlotsOf(Player));
            Assert.Equal(writesBefore, world.SlotWrites);
            Assert.Equal(90L, MainHandId(inventory));
        }
    }

    [Fact]
    public void EquipOverAWornItem_SendsTheDisplacedItemBackToTheBag_OrRejectsWhenItFitsNowhere()
    {
        EquipmentTestWorld world = new();
        world.PutItem(EquipmentTestWorld.MakeItem(90, Player, "tool.torch"));
        world.PutItem(EquipmentTestWorld.MakeItem(91, Player, "tool.field-blade"));
        world.PutSlot(Player, 100, 90);
        world.PutSlot(Player, 0, 91);
        world.PutGrant(Player, ToolCatalog.CurrentStarterKitVersion);
        PlayerInventoryService inventory = Create(world, out PersistenceWriteQueue writes);
        using (writes)
        {
            Assert.Equal(InventoryEquipOutcome.Equipped, inventory.TryEquip(Player, 0));

            // The blade is worn; the torch it displaced takes the blade's anchor, where its 1x2 fits.
            Dictionary<int, long> rows = world.SlotsOf(Player);
            Assert.Equal(91L, rows[100]);
            Assert.Equal(90L, rows[0]);
        }

        // The torch (a 1x2 at anchor 0) is the only item that can leave room: the board is full of
        // rings around it, and the hammer it would displace is a 2x3 that fits nowhere.
        EquipmentTestWorld fullWorld = new();
        fullWorld.PutItem(EquipmentTestWorld.MakeItem(90, Player, "tool.builder-hammer"));
        fullWorld.PutItem(EquipmentTestWorld.MakeItem(91, Player, "tool.torch"));
        fullWorld.PutSlot(Player, 100, 90);
        fullWorld.PutSlot(Player, 0, 91);
        for (int slot = 0; slot < InventoryConstants.BackpackSlotCount; slot++)
        {
            if (slot is 0 or 8)
            {
                continue;
            }

            fullWorld.PutItem(EquipmentTestWorld.MakeItem(200 + slot, Player, "gear.iron-band"));
            fullWorld.PutSlot(Player, slot, 200 + slot);
        }

        fullWorld.PutGrant(Player, ToolCatalog.CurrentStarterKitVersion);
        PlayerInventoryService full = Create(fullWorld, out PersistenceWriteQueue fullWrites);
        using (fullWrites)
        {
            Assert.True(full.Ensure(Player));
            Dictionary<int, long> before = fullWorld.SlotsOf(Player);
            int writesBefore = fullWorld.SlotWrites;

            Assert.Equal(InventoryEquipOutcome.BackpackFull, full.TryEquip(Player, 0));

            Assert.Equal(before, fullWorld.SlotsOf(Player));
            Assert.Equal(writesBefore, fullWorld.SlotWrites);
            Assert.Equal(90L, full.BuildSnapshot(Player).EquippedSlot!.Value.Item!.Value.ItemInstanceId);
        }
    }

    [Fact]
    public void Destroy_OfABagItem_RemovesTheRowAndTheItem_AndALaterKitTopUpDoesNotReturnIt()
    {
        EquipmentTestWorld world = new();
        PlayerInventoryService inventory = Create(world, out PersistenceWriteQueue writes);
        using (writes)
        {
            Assert.True(inventory.Ensure(Player));
            long lantern = inventory.BuildSnapshot(Player).IdAt(6);

            Assert.Equal(InventoryEquipOutcome.Equipped, inventory.TryDestroy(Player, 6, lantern));

            Assert.Null(inventory.BuildSnapshot(Player).ItemAt(6));
            Assert.False(world.SlotsOf(Player).ContainsKey(6));
            Assert.False(world.HasItem(lantern));
            Assert.True(inventory.Ensure(Player));
            Assert.Null(inventory.BuildSnapshot(Player).ItemAt(6));

            PlayerInventoryService restarted = Create(world, out PersistenceWriteQueue restartWrites);
            using (restartWrites)
            {
                Assert.True(restarted.Ensure(Player));
                Assert.DoesNotContain(
                    restarted.BuildSnapshot(Player).Slots,
                    slot => slot.Item?.ItemKind == "tool.survey-lantern");
            }
        }
    }

    [Fact]
    public void Destroy_OfAWornItem_ABadId_OrAnotherPlayersItem_IsRejected_WithNothingChanged()
    {
        EquipmentTestWorld world = new();
        PlayerInventoryService inventory = Create(world, out PersistenceWriteQueue writes);
        using (writes)
        {
            Assert.True(inventory.Ensure(Player));
            Assert.True(inventory.Ensure("Bob"));
            long hammer = MainHandId(inventory);
            long bobsBlade = inventory.BuildSnapshot("Bob").IdAt(2);
            Dictionary<int, long> aliceBefore = world.SlotsOf(Player);
            Dictionary<int, long> bobBefore = world.SlotsOf("Bob");
            int writesBefore = world.SlotWrites;

            // A worn item has no bag cell to be destroyed from, and a worn index is not a bag cell.
            Assert.Equal(InventoryEquipOutcome.SlotEmpty, inventory.TryDestroy(Player, 0, hammer));
            Assert.Equal(InventoryEquipOutcome.SlotOutOfRange, inventory.TryDestroy(Player, 100, hammer));
            Assert.Equal(InventoryEquipOutcome.ItemMismatch, inventory.TryDestroy(Player, 2, bobsBlade));
            Assert.Equal(InventoryEquipOutcome.ItemMismatch, inventory.TryDestroy(Player, 2, 99999));
            Assert.Equal(InventoryEquipOutcome.ItemMismatch, inventory.TryDestroy(Player, 2, -1));

            Assert.Equal(aliceBefore, world.SlotsOf(Player));
            Assert.Equal(bobBefore, world.SlotsOf("Bob"));
            Assert.Equal(writesBefore, world.SlotWrites);
            Assert.Equal(1, inventory.BuildSnapshot("Bob").Slots.Count(slot => slot.Item?.ItemInstanceId == bobsBlade));
        }
    }

    // Only a hand-edited row leaves a worn item in the bag as well; destroying it by the bag row is refused.
    [Fact]
    public void Destroy_OfABagRowThatIsAlsoWorn_IsRejectedAsEquipped_WithNothingChanged()
    {
        EquipmentTestWorld world = new();
        world.PutItem(EquipmentTestWorld.MakeItem(50, Player, "tool.field-blade"));
        world.PutSlot(Player, 0, 50);
        world.PutSlot(Player, 100, 50);
        world.PutGrant(Player, ToolCatalog.CurrentStarterKitVersion);
        PlayerInventoryService inventory = Create(world, out PersistenceWriteQueue writes);
        using (writes)
        {
            Assert.True(inventory.Ensure(Player));
            Dictionary<int, long> before = world.SlotsOf(Player);
            int writesBefore = world.SlotWrites;

            Assert.Equal(InventoryEquipOutcome.ItemEquipped, inventory.TryDestroy(Player, 0, 50));

            Assert.Equal(before, world.SlotsOf(Player));
            Assert.Equal(writesBefore, world.SlotWrites);
            Assert.True(world.HasItem(50));
        }
    }

    [Fact]
    public void TheBudgetGate_IsSpentOnlyByActionsThatWouldWrite_AndAThrottledActionChangesNothing()
    {
        EquipmentTestWorld world = new();
        PlayerInventoryService inventory = Create(world, out PersistenceWriteQueue writes);
        using (writes)
        {
            Assert.True(inventory.Ensure(Player));
            int spent = 0;
            bool allow = true;
            inventory.ActionGate = () =>
            {
                spent++;
                return allow;
            };

            // Rejections and no-ops spend nothing.
            Assert.Equal(InventoryEquipOutcome.SlotEmpty, inventory.TryMove(Player, 9, 10));
            Assert.Equal(InventoryEquipOutcome.Equipped, inventory.TryMove(Player, 2, 2));
            Assert.Equal(InventoryEquipOutcome.SlotOutOfRange, inventory.TryEquip(Player, 64));
            Assert.Equal(InventoryEquipOutcome.Equipped, inventory.TryEquip(Player, 100, 100));
            // Two items under the new area (the maul and the lantern), a position off the board and a
            // target that is not free are refused before the budget as well.
            Assert.Equal(InventoryEquipOutcome.NoRoom, inventory.TryMove(Player, 2, 5));
            Assert.Equal(InventoryEquipOutcome.NoRoom, inventory.TryMove(Player, 2, 7));
            Assert.Equal(InventoryEquipOutcome.NoRoom, inventory.TryUnequip(Player, 100, 2));
            Assert.Equal(0, spent);

            Dictionary<int, long> before = world.SlotsOf(Player);
            int writesBefore = world.SlotWrites;
            allow = false;
            Assert.Equal(InventoryEquipOutcome.Throttled, inventory.TryMove(Player, 2, 24));
            Assert.Equal(InventoryEquipOutcome.Throttled, inventory.TryEquip(Player, 2));
            Assert.Equal(InventoryEquipOutcome.Throttled, inventory.TryUnequip(Player, 100));
            Assert.Equal(InventoryEquipOutcome.Throttled, inventory.TryDestroy(Player, 2, before[2]));
            Assert.Equal(4, spent);
            Assert.Equal(before, world.SlotsOf(Player));
            Assert.Equal(writesBefore, world.SlotWrites);
        }
    }

    [Fact]
    public void InventoryActionBudget_AllowsABurstOf16_ThenRefillsEightPerSecondInTicks()
    {
        InventoryActionBudget budget = default;
        for (int i = 0; i < 16; i++)
        {
            Assert.True(budget.TrySpend(100));
        }

        Assert.False(budget.TrySpend(100));
        Assert.False(budget.TrySpend(107));
        Assert.True(budget.TrySpend(108));
        Assert.False(budget.TrySpend(108));
        // A long idle spell refills to the burst and no further.
        for (int i = 0; i < 16; i++)
        {
            Assert.True(budget.TrySpend(100_000));
        }

        Assert.False(budget.TrySpend(100_000));
        // A tick that goes backwards neither refills nor throws.
        Assert.False(budget.TrySpend(5));
    }

    [Fact]
    public void AFlood_OfAThousandMoves_InOneTick_IsHeldTo16AcceptedAndAtMost32Writes()
    {
        EquipmentTestWorld world = new();
        PlayerInventoryService inventory = Create(world, out PersistenceWriteQueue writes);
        using (writes)
        {
            Assert.True(inventory.Ensure(Player));
            InventoryActionBudget budget = default;
            inventory.ActionGate = () => budget.TrySpend(500);
            int writesBefore = world.SlotWrites;

            int accepted = 0;
            int throttled = 0;
            for (int i = 0; i < 1000; i++)
            {
                InventoryEquipOutcome outcome = accepted % 2 == 0 ? inventory.TryMove(Player, 2, 26) : inventory.TryMove(Player, 26, 2);
                if (outcome == InventoryEquipOutcome.Equipped)
                {
                    accepted++;
                }
                else
                {
                    Assert.Equal(InventoryEquipOutcome.Throttled, outcome);
                    throttled++;
                }
            }

            Assert.Equal(16, accepted);
            Assert.Equal(984, throttled);
            Assert.True(world.SlotWrites - writesBefore <= 32, $"{world.SlotWrites - writesBefore} writes");
        }
    }

    [Fact]
    public void ModifierDigest_IsZeroWithoutAnEquippedRolledItem_AndFollowsTheItemTheSlotAndTheValue()
    {
        ulong Digest(string kind, int slotIndex, float value, out PlayerInventoryService inventory, long id = 500)
        {
            EquipmentTestWorld world = new();
            ItemRoll roll = new(ItemRarity.Uncommon, [new StatModifier(StatId.Strength, StatOp.Flat, value)]);
            world.PutItem(EquipmentTestWorld.MakeItem(id, Player, kind, roll));
            world.PutSlot(Player, 0, id);
            world.PutGrant(Player, ToolCatalog.CurrentStarterKitVersion);
            inventory = Create(world, out _);
            Assert.Equal(0UL, inventory.ResolveModifierDigest(Player));
            Assert.True(inventory.Ensure(Player));
            // In the backpack only: still zero.
            Assert.Equal(0UL, inventory.ResolveModifierDigest(Player));
            Assert.Equal(InventoryEquipOutcome.Equipped, inventory.TryEquip(Player, 0, slotIndex));
            return inventory.ResolveModifierDigest(Player);
        }

        ulong mainSix = Digest("tool.torch", 100, 6, out PlayerInventoryService inventory);
        Assert.NotEqual(0UL, mainSix);
        // The item id is not an input: the same roll under another id gives the same digest.
        Assert.Equal(mainSix, Digest("tool.torch", 100, 6, out _, id: 987_654));
        Assert.NotEqual(mainSix, Digest("tool.torch", 101, 6, out _));
        Assert.NotEqual(mainSix, Digest("tool.torch", 100, 7, out _));

        // Unequipping returns to zero.
        Assert.Equal(InventoryEquipOutcome.Equipped, inventory.TryUnequip(Player, 100));
        Assert.Equal(0UL, inventory.ResolveModifierDigest(Player));
    }

    [Fact]
    public void TheCachedSummary_MatchesAFreshRecompute_AfterARandomActionSequence()
    {
        EquipmentTestWorld world = new();
        PlayerInventoryService inventory = Create(world, out PersistenceWriteQueue writes);
        using (writes)
        {
            Assert.True(inventory.Ensure(Player));
            string[] gear = ["gear.iron-blade", "gear.ward-lantern", "gear.leather-cap", "gear.leather-vest", "gear.iron-blade", "gear.leather-cap"];
            for (int i = 0; i < gear.Length; i++)
            {
                Assert.Equal(
                    InventoryEquipOutcome.Equipped,
                    inventory.TryGrantRolled(Player, gear[i], (ItemRarity)(i % 3), new ItemRollSeed(7, (ulong)i, 11, 13, i)));
            }

            Random random = new(20261101);
            EquipmentLoadout live = new();
            EquipmentLoadout fresh = new();
            for (int step = 0; step < 400; step++)
            {
                PlayerInventorySnapshot snapshot = inventory.BuildSnapshot(Player);
                switch (random.Next(6))
                {
                    case 0:
                        inventory.TryEquip(Player, random.Next(-1, 114), random.Next(3) == 0 ? null : random.Next(98, 114));
                        break;
                    case 1:
                        inventory.TryUnequip(Player, random.Next(98, 114), random.Next(3) == 0 ? null : random.Next(-1, 66));
                        break;
                    case 2:
                        inventory.TryMove(Player, random.Next(0, 65), random.Next(0, 65));
                        break;
                    case 3:
                        int slot = random.Next(0, 64);
                        long id = snapshot.IdAt(slot);
                        // Mostly the right id; sometimes a stale one.
                        inventory.TryDestroy(Player, slot, random.Next(4) == 0 ? id + 1 : id);
                        break;
                    case 4:
                        // An item that sits at a real anchor, so most picks do something.
                        int[] anchors = snapshot.BagItems().Select(entry => entry.SlotIndex).ToArray();
                        if (anchors.Length > 0)
                        {
                            inventory.TryEquip(Player, anchors[random.Next(anchors.Length)]);
                        }

                        break;
                    default:
                        inventory.TryEquip(Player, random.Next(0, 65));
                        break;
                }

                PlayerInventoryService reloaded = Create(world, out PersistenceWriteQueue reloadWrites);
                using (reloadWrites)
                {
                    Assert.True(reloaded.Ensure(Player));
                    // The rows the actions queued say the same thing as the live inventory: the board, the
                    // main hand and the other worn slots all come back as they are.
                    Assert.Equal(Layout(inventory.BuildSnapshot(Player)), Layout(reloaded.BuildSnapshot(Player)));
                    Assert.True(inventory.BuildSnapshot(Player).BoardIsConsistent());
                    Assert.Equal(reloaded.ResolveModifierDigest(Player), inventory.ResolveModifierDigest(Player));
                    Assert.Equal(reloaded.ResolveEquippedHeldLight(Player), inventory.ResolveEquippedHeldLight(Player));
                    inventory.CopyEquipment(Player, live);
                    reloaded.CopyEquipment(Player, fresh);
                    for (int s = 0; s < EquipmentSlots.Count; s++)
                    {
                        Assert.Equal(fresh.Get((EquipmentSlot)s).ToArray(), live.Get((EquipmentSlot)s).ToArray());
                    }
                }
            }
        }
    }
}
