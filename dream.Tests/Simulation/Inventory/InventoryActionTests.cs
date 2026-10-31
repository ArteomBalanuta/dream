using Xunit;
using Dream.Multiplayer.Sessions;
using Dream.Server.Persistence;
using Dream.Simulation.Inventory;
using Dream.Simulation.Persistence;
using Dream.Simulation.Stats;
using Dream.Simulation.World;
using Dream.Tests.Support;

namespace Dream.Tests.Simulation.Inventory;

// Wave H1: move, unequip, destroy and equip on PlayerInventoryService - the alias (an equipment row
// holds the id of an item that also has a backpack row), legacy rows with no backpack row, auto-equip
// only on first sight (H-D3), the action budget (H-D18) and the cached equipment summary.
public sealed class InventoryActionTests
{
    private const string Player = "Alice";

    private static PlayerInventoryService Create(EquipmentTestWorld world, out PersistenceWriteQueue writes)
        => EquipmentSlotTests.Create(world, out writes);

    private static long MainHandId(PlayerInventoryService inventory)
        => inventory.BuildSnapshot(Player).EquippedSlot!.Value.Item?.ItemInstanceId ?? 0;

    [Fact]
    public void MovingAnEquippedItem_KeepsItEquipped_AndTheRowsSurviveARestart()
    {
        EquipmentTestWorld world = new();
        PlayerInventoryService inventory = Create(world, out PersistenceWriteQueue writes);
        using (writes)
        {
            Assert.Equal(InventoryEquipOutcome.Equipped, inventory.TryEquip(Player, 1));
            long bladeId = MainHandId(inventory);

            Assert.Equal(InventoryEquipOutcome.Equipped, inventory.TryMove(Player, 1, 9));

            PlayerInventorySnapshot snapshot = inventory.BuildSnapshot(Player);
            Assert.Equal(bladeId, snapshot.Slots[9].Item!.Value.ItemInstanceId);
            Assert.True(snapshot.Slots[9].IsEquipped);
            Assert.Null(snapshot.Slots[1].Item);
            Assert.Equal(bladeId, MainHandId(inventory));

            Dictionary<int, long> rows = world.SlotsOf(Player);
            Assert.False(rows.ContainsKey(1));
            Assert.Equal(bladeId, rows[9]);
            Assert.Equal(bladeId, rows[100]);

            PlayerInventoryService restarted = Create(world, out PersistenceWriteQueue restartWrites);
            using (restartWrites)
            {
                Assert.True(restarted.Ensure(Player));
                Assert.Equal(bladeId, MainHandId(restarted));
                Assert.Equal(bladeId, restarted.BuildSnapshot(Player).Slots[9].Item!.Value.ItemInstanceId);
            }
        }
    }

    [Fact]
    public void SwapOfTwoEquippedItems_Works_AndBothStayEquipped()
    {
        EquipmentTestWorld world = new();
        PlayerInventoryService inventory = Create(world, out PersistenceWriteQueue writes);
        using (writes)
        {
            Assert.Equal(InventoryEquipOutcome.Equipped, inventory.TryEquip(Player, 4));
            Assert.Equal(InventoryEquipOutcome.Equipped, inventory.TryEquip(Player, 3, 101));
            PlayerInventorySnapshot before = inventory.BuildSnapshot(Player);
            long torch = before.Slots[4].Item!.Value.ItemInstanceId;
            long lantern = before.Slots[3].Item!.Value.ItemInstanceId;

            Assert.Equal(InventoryEquipOutcome.Equipped, inventory.TryMove(Player, 3, 4));

            PlayerInventorySnapshot after = inventory.BuildSnapshot(Player);
            Assert.Equal(torch, after.Slots[3].Item!.Value.ItemInstanceId);
            Assert.Equal(lantern, after.Slots[4].Item!.Value.ItemInstanceId);
            Assert.True(after.Slots[3].IsEquipped);
            Assert.True(after.Slots[4].IsEquipped);
            Assert.Equal(torch, MainHandId(inventory));
            Assert.Equal(lantern, world.SlotsOf(Player)[101]);
        }
    }

    [Fact]
    public void Unequip_KeepsTheBackpackRow_AndSurvivesEnsureSnapshotAndRestart()
    {
        EquipmentTestWorld world = new();
        PlayerInventoryService inventory = Create(world, out PersistenceWriteQueue writes);
        using (writes)
        {
            Assert.True(inventory.Ensure(Player));
            long hammer = MainHandId(inventory);

            Assert.Equal(InventoryEquipOutcome.Equipped, inventory.TryUnequip(Player, 100));

            Assert.Equal(0, MainHandId(inventory));
            Assert.Equal(hammer, inventory.BuildSnapshot(Player).Slots[0].Item!.Value.ItemInstanceId);
            Assert.False(inventory.BuildSnapshot(Player).Slots[0].IsEquipped);
            Assert.False(world.SlotsOf(Player).ContainsKey(100));

            // The empty hand is not refilled by Ensure (H-D3), nor by a rejoin or a restart.
            Assert.True(inventory.Ensure(Player));
            Assert.Equal(0, MainHandId(inventory));
            PlayerInventoryService restarted = Create(world, out PersistenceWriteQueue restartWrites);
            using (restartWrites)
            {
                Assert.True(restarted.Ensure(Player));
                Assert.Equal(0, MainHandId(restarted));
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
            Assert.Equal(30L, legacyWorld.SlotsOf("Bob")[100]);
        }
    }

    [Fact]
    public void UnequipOfALegacyItemWithNoBackpackRow_RescuesItIntoTheLowestFreeSlot()
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

            Dictionary<int, long> rows = world.SlotsOf(Player);
            Assert.Equal(90L, rows[1]);
            Assert.False(rows.ContainsKey(100));
        }
    }

    [Fact]
    public void UnequipOfALegacyItem_WithAFullBackpack_IsRejected_AndTheItemIsNeverLost()
    {
        EquipmentTestWorld world = new();
        world.PutItem(EquipmentTestWorld.MakeItem(90, Player, "tool.torch"));
        world.PutSlot(Player, 100, 90);
        for (int slot = 0; slot < InventoryConstants.BackpackSlotCount; slot++)
        {
            world.PutItem(EquipmentTestWorld.MakeItem(200 + slot, Player, "tool.builder-hammer"));
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
    public void EquipOverALegacyItemWithNoBackpackRow_RescuesTheDisplacedItem_OrRejectsWhenFull()
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

            Dictionary<int, long> rows = world.SlotsOf(Player);
            Assert.Equal(91L, rows[100]);
            Assert.Equal(90L, rows[1]);
        }

        EquipmentTestWorld fullWorld = new();
        fullWorld.PutItem(EquipmentTestWorld.MakeItem(90, Player, "tool.torch"));
        fullWorld.PutSlot(Player, 100, 90);
        for (int slot = 0; slot < InventoryConstants.BackpackSlotCount; slot++)
        {
            fullWorld.PutItem(EquipmentTestWorld.MakeItem(200 + slot, Player, "tool.field-blade"));
            fullWorld.PutSlot(Player, slot, 200 + slot);
        }

        fullWorld.PutGrant(Player, ToolCatalog.CurrentStarterKitVersion);
        PlayerInventoryService full = Create(fullWorld, out PersistenceWriteQueue fullWrites);
        using (fullWrites)
        {
            Dictionary<int, long> before = fullWorld.SlotsOf(Player);
            int writesBefore = fullWorld.SlotWrites;

            Assert.Equal(InventoryEquipOutcome.BackpackFull, full.TryEquip(Player, 3));

            Assert.Equal(before, fullWorld.SlotsOf(Player));
            Assert.Equal(writesBefore, fullWorld.SlotWrites);
            Assert.Equal(90L, full.BuildSnapshot(Player).EquippedSlot!.Value.Item!.Value.ItemInstanceId);
        }
    }

    [Fact]
    public void Destroy_OfABackpackItem_RemovesBothRows_AndALaterKitTopUpDoesNotReturnIt()
    {
        EquipmentTestWorld world = new();
        PlayerInventoryService inventory = Create(world, out PersistenceWriteQueue writes);
        using (writes)
        {
            Assert.True(inventory.Ensure(Player));
            long lantern = inventory.BuildSnapshot(Player).Slots[3].Item!.Value.ItemInstanceId;

            Assert.Equal(InventoryEquipOutcome.Equipped, inventory.TryDestroy(Player, 3, lantern));

            Assert.Null(inventory.BuildSnapshot(Player).Slots[3].Item);
            Assert.False(world.SlotsOf(Player).ContainsKey(3));
            Assert.False(world.HasItem(lantern));
            Assert.True(inventory.Ensure(Player));
            Assert.Null(inventory.BuildSnapshot(Player).Slots[3].Item);

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
    public void Destroy_OfAnEquippedItem_ABadId_OrAnotherPlayersItem_IsRejected_WithNothingChanged()
    {
        EquipmentTestWorld world = new();
        PlayerInventoryService inventory = Create(world, out PersistenceWriteQueue writes);
        using (writes)
        {
            Assert.True(inventory.Ensure(Player));
            Assert.True(inventory.Ensure("Bob"));
            long hammer = MainHandId(inventory);
            long bobsBlade = inventory.BuildSnapshot("Bob").Slots[1].Item!.Value.ItemInstanceId;
            Dictionary<int, long> aliceBefore = world.SlotsOf(Player);
            Dictionary<int, long> bobBefore = world.SlotsOf("Bob");
            int writesBefore = world.SlotWrites;

            Assert.Equal(InventoryEquipOutcome.ItemEquipped, inventory.TryDestroy(Player, 0, hammer));
            Assert.Equal(InventoryEquipOutcome.ItemMismatch, inventory.TryDestroy(Player, 1, bobsBlade));
            Assert.Equal(InventoryEquipOutcome.ItemMismatch, inventory.TryDestroy(Player, 1, 99999));
            Assert.Equal(InventoryEquipOutcome.ItemMismatch, inventory.TryDestroy(Player, 1, -1));

            Assert.Equal(aliceBefore, world.SlotsOf(Player));
            Assert.Equal(bobBefore, world.SlotsOf("Bob"));
            Assert.Equal(writesBefore, world.SlotWrites);
            Assert.Equal(1, inventory.BuildSnapshot("Bob").Slots.Count(slot => slot.Item?.ItemInstanceId == bobsBlade));
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
            Assert.Equal(InventoryEquipOutcome.Equipped, inventory.TryMove(Player, 0, 0));
            Assert.Equal(InventoryEquipOutcome.SlotOutOfRange, inventory.TryEquip(Player, 16));
            Assert.Equal(InventoryEquipOutcome.Equipped, inventory.TryEquip(Player, 0));
            Assert.Equal(0, spent);

            Dictionary<int, long> before = world.SlotsOf(Player);
            int writesBefore = world.SlotWrites;
            allow = false;
            Assert.Equal(InventoryEquipOutcome.Throttled, inventory.TryMove(Player, 0, 9));
            Assert.Equal(InventoryEquipOutcome.Throttled, inventory.TryEquip(Player, 1));
            Assert.Equal(InventoryEquipOutcome.Throttled, inventory.TryUnequip(Player, 100));
            Assert.Equal(InventoryEquipOutcome.Throttled, inventory.TryDestroy(Player, 1, before[1]));
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
                InventoryEquipOutcome outcome = accepted % 2 == 0 ? inventory.TryMove(Player, 1, 12) : inventory.TryMove(Player, 12, 1);
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
                switch (random.Next(5))
                {
                    case 0:
                        inventory.TryEquip(Player, random.Next(-1, 17), random.Next(3) == 0 ? null : random.Next(98, 106));
                        break;
                    case 1:
                        inventory.TryUnequip(Player, random.Next(98, 106));
                        break;
                    case 2:
                        inventory.TryMove(Player, random.Next(0, 16), random.Next(0, 16));
                        break;
                    case 3:
                        int slot = random.Next(0, 16);
                        long id = snapshot.Slots[slot].Item?.ItemInstanceId ?? 0;
                        // Mostly the right id; sometimes a stale one.
                        inventory.TryDestroy(Player, slot, random.Next(4) == 0 ? id + 1 : id);
                        break;
                    default:
                        inventory.TryEquip(Player, random.Next(0, 16));
                        break;
                }

                PlayerInventoryService reloaded = Create(world, out PersistenceWriteQueue reloadWrites);
                using (reloadWrites)
                {
                    Assert.True(reloaded.Ensure(Player));
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
