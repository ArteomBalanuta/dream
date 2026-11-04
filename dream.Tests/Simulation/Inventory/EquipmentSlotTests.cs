using Xunit;
using Dream.Server.Persistence;
using Dream.Simulation.Inventory;
using Dream.Simulation.Persistence;
using Dream.Simulation.Stats;
using Dream.Simulation.World;
using Dream.Tests.Support;

namespace Dream.Tests.Simulation.Inventory;

// Wave H1: the equipment slots on PlayerInventoryService (slot rules from content, targets, the
// one-slot-only rule, stats and light per slot). A new player's starter kit is: the hammer (id 1) in the
// main hand, the torch (5) in the off hand, and in the bag (anchors of the first fits) the blade (id 2)
// at 2, the maul (3) at 4 and the lantern (4) at 6. A test that wants the torch on the board unequips
// it first (UnequipStarterTorch) and reads its anchor back, so no cell is pinned here.
public sealed class EquipmentSlotTests
{
    private const string Player = "Alice";
    private const int Blade = 2;
    private const int Maul = 4;
    private const int Lantern = 6;

    internal static PlayerInventoryService Create(EquipmentTestWorld world, out PersistenceWriteQueue writes)
    {
        writes = PersistenceWriteQueue.Inline();
        return new PlayerInventoryService(world, writes, new ObjectIdAllocator());
    }

    internal static StatModifier[] Mods(EquipmentLoadout loadout, EquipmentSlot slot) => loadout.Get(slot).ToArray();

    private static EquipmentLoadout Loadout(PlayerInventoryService inventory)
    {
        EquipmentLoadout loadout = new();
        inventory.CopyEquipment(Player, loadout);
        return loadout;
    }

    [Fact]
    public void BladeInMainHand_AndTorchInOffHand_BothHold_StatsAndLightFollow()
    {
        EquipmentTestWorld world = new();
        PlayerInventoryService inventory = Create(world, out PersistenceWriteQueue writes);
        using (writes)
        {
            Assert.True(inventory.Ensure(Player));
            int torch = inventory.UnequipStarterTorch(Player);

            Assert.Equal(InventoryEquipOutcome.Equipped, inventory.TryEquip(Player, Blade));
            Assert.Equal(InventoryEquipOutcome.Equipped, inventory.TryEquip(Player, torch, EquipmentSlots.ToSlotIndex(EquipmentSlot.OffHand)));

            EquipmentLoadout loadout = Loadout(inventory);
            Assert.Equal(ToolCatalog.Resolve("tool.field-blade").Modifiers.ToArray(), Mods(loadout, EquipmentSlot.MainHand));
            Assert.Empty(Mods(loadout, EquipmentSlot.OffHand));
            Assert.Equal(HeldLightKind.Torch, inventory.ResolveEquippedHeldLight(Player));

            PlayerInventorySnapshot snapshot = inventory.BuildSnapshot(Player);
            Assert.Equal("tool.field-blade", snapshot.EquippedSlot!.Value.Item!.Value.ItemKind);
            InventorySlotSnapshot off = Assert.Single(snapshot.Equipment!);
            Assert.Equal(EquipmentSlots.ToSlotIndex(EquipmentSlot.OffHand), off.SlotIndex);
            Assert.Equal("tool.torch", off.Item!.Value.ItemKind);
            // The blade and the torch left the bag; the hammer the blade displaced took the blade's place.
            Assert.Equal("tool.builder-hammer", snapshot.ItemAt(Blade)!.Value.ItemKind);
            Assert.Null(snapshot.ItemAt(torch));
            Assert.All(snapshot.Slots, slot => Assert.False(slot.IsEquipped));
            Assert.True(snapshot.BoardIsConsistent());
        }
    }

    [Fact]
    public void LanternInOffHand_AddsItsStats_AndOffHandLightWinsOverMainHandLight()
    {
        EquipmentTestWorld world = new();
        PlayerInventoryService inventory = Create(world, out PersistenceWriteQueue writes);
        using (writes)
        {
            Assert.True(inventory.Ensure(Player));
            int torch = inventory.UnequipStarterTorch(Player);
            Assert.Equal(InventoryEquipOutcome.Equipped, inventory.TryEquip(Player, torch));
            Assert.Equal(HeldLightKind.Torch, inventory.ResolveEquippedHeldLight(Player));
            Assert.Equal(InventoryEquipOutcome.Equipped, inventory.TryEquip(Player, Lantern, 101));

            EquipmentLoadout loadout = Loadout(inventory);
            Assert.Equal(ToolCatalog.Resolve("tool.survey-lantern").Modifiers.ToArray(), Mods(loadout, EquipmentSlot.OffHand));
            Assert.Equal(HeldLightKind.Lantern, inventory.ResolveEquippedHeldLight(Player));
        }
    }

    [Fact]
    public void TorchWithNoTarget_GoesToTheMainHand_AsToday()
    {
        EquipmentTestWorld world = new();
        PlayerInventoryService inventory = Create(world, out PersistenceWriteQueue writes);
        using (writes)
        {
            Assert.True(inventory.Ensure(Player));
            int torch = inventory.UnequipStarterTorch(Player);
            long torchId = inventory.BuildSnapshot(Player).IdAt(torch);
            Assert.NotEqual(0, torchId);

            Assert.Equal(InventoryEquipOutcome.Equipped, inventory.TryEquip(Player, torch));

            Dictionary<int, long> rows = world.SlotsOf(Player);
            Assert.Equal(torchId, rows[100]);
            Assert.False(rows.ContainsKey(101));
            // The torch left the board; the hammer it displaced may well have taken its anchor.
            Assert.DoesNotContain(
                torchId,
                rows.Where(row => row.Key < EquipmentSlots.FirstSlotIndex).Select(row => row.Value));
        }
    }

    [Fact]
    public void TorchMovedFromMainToOffHand_LeavesMainEmpty_AndTheItemInOneEquipmentRow()
    {
        EquipmentTestWorld world = new();
        PlayerInventoryService inventory = Create(world, out PersistenceWriteQueue writes);
        using (writes)
        {
            Assert.True(inventory.Ensure(Player));
            int torch = inventory.UnequipStarterTorch(Player);
            Assert.Equal(InventoryEquipOutcome.Equipped, inventory.TryEquip(Player, torch));
            // The torch is worn now; moving it between the hands names the worn slot as the source.
            Assert.Equal(InventoryEquipOutcome.Equipped, inventory.TryEquip(Player, 100, 101));

            PlayerInventorySnapshot snapshot = inventory.BuildSnapshot(Player);
            Assert.Null(snapshot.EquippedSlot!.Value.Item);
            Assert.Equal("tool.torch", Assert.Single(snapshot.Equipment!).Item!.Value.ItemKind);
            Dictionary<int, long> rows = world.SlotsOf(Player);
            Assert.False(rows.ContainsKey(100));
            Assert.True(rows.ContainsKey(101));
            Assert.Single(rows.Where(pair => pair.Key >= 100));
        }
    }

    [Fact]
    public void MaulInOffHand_HelmetInBody_AndBadTargets_AreRejected_WithNothingChanged()
    {
        EquipmentTestWorld world = new();
        PlayerInventoryService inventory = Create(world, out PersistenceWriteQueue writes);
        using (writes)
        {
            Assert.True(inventory.Ensure(Player));
            Assert.Equal(
                InventoryEquipOutcome.Equipped,
                inventory.TryGrantRolled(Player, "gear.leather-cap", ItemRarity.Common, new ItemRollSeed(1, 2, 3, 4, 5)));
            int capSlot = inventory.AnchorOf(Player, "gear.leather-cap");
            Dictionary<int, long> rowsBefore = world.SlotsOf(Player);
            int writesBefore = world.SlotWrites;

            Assert.Equal(InventoryEquipOutcome.NotEquippable, inventory.TryEquip(Player, Maul, 101));
            Assert.Equal(InventoryEquipOutcome.NotEquippable, inventory.TryEquip(Player, capSlot, 103));
            foreach (int badTarget in new[] { -1, 99, 112, int.MaxValue, int.MinValue })
            {
                Assert.Equal(InventoryEquipOutcome.TargetOutOfRange, inventory.TryEquip(Player, Maul, badTarget));
            }

            Assert.Equal(rowsBefore, world.SlotsOf(Player));
            Assert.Equal(writesBefore, world.SlotWrites);

            // The cap itself goes to its own slot.
            Assert.Equal(InventoryEquipOutcome.Equipped, inventory.TryEquip(Player, capSlot, 102));
            Assert.True(world.SlotsOf(Player).ContainsKey(102));
        }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(64)]
    [InlineData(99)]
    [InlineData(112)]
    [InlineData(200)]
    [InlineData(215)]
    [InlineData(int.MaxValue)]
    [InlineData(int.MinValue)]
    public void BadBackpackSlot_IsRejectedAsOutOfRange(int slotIndex)
    {
        EquipmentTestWorld world = new();
        PlayerInventoryService inventory = Create(world, out PersistenceWriteQueue writes);
        using (writes)
        {
            Assert.True(inventory.Ensure(Player));
            int writesBefore = world.SlotWrites;

            Assert.Equal(InventoryEquipOutcome.SlotOutOfRange, inventory.TryEquip(Player, slotIndex));
            Assert.Equal(InventoryEquipOutcome.SlotOutOfRange, inventory.TryMove(Player, slotIndex, 0));
            Assert.Equal(InventoryEquipOutcome.SlotOutOfRange, inventory.TryMove(Player, 0, slotIndex));
            Assert.Equal(InventoryEquipOutcome.SlotOutOfRange, inventory.TryDestroy(Player, slotIndex, 1));
            Assert.Equal(writesBefore, world.SlotWrites);
        }
    }

    // A worn slot is not a bag cell: moving or destroying "from" it is out of range. (Equip may name one as
    // its source, to move a worn item to another worn slot.)
    [Theory]
    [InlineData(100)]
    [InlineData(104)]
    [InlineData(111)]
    public void AWornIndex_IsNotABagCell_ForMoveAndDestroy(int slotIndex)
    {
        EquipmentTestWorld world = new();
        PlayerInventoryService inventory = Create(world, out PersistenceWriteQueue writes);
        using (writes)
        {
            Assert.True(inventory.Ensure(Player));
            int writesBefore = world.SlotWrites;

            Assert.Equal(InventoryEquipOutcome.SlotOutOfRange, inventory.TryMove(Player, slotIndex, 0));
            Assert.Equal(InventoryEquipOutcome.SlotOutOfRange, inventory.TryMove(Player, 0, slotIndex));
            Assert.Equal(InventoryEquipOutcome.SlotOutOfRange, inventory.TryDestroy(Player, slotIndex, 1));
            Assert.Equal(writesBefore, world.SlotWrites);
        }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(99)]
    [InlineData(112)]
    [InlineData(200)]
    [InlineData(int.MaxValue)]
    public void Unequip_OfANonEquipmentIndex_IsRejectedAsOutOfRange(int slotIndex)
    {
        EquipmentTestWorld world = new();
        PlayerInventoryService inventory = Create(world, out PersistenceWriteQueue writes);
        using (writes)
        {
            Assert.True(inventory.Ensure(Player));
            Assert.Equal(InventoryEquipOutcome.SlotOutOfRange, inventory.TryUnequip(Player, slotIndex));
        }
    }

    [Fact]
    public void EmptyBackpackSlot_IsRejected_ForEveryAction()
    {
        EquipmentTestWorld world = new();
        PlayerInventoryService inventory = Create(world, out PersistenceWriteQueue writes);
        using (writes)
        {
            Assert.True(inventory.Ensure(Player));
            int writesBefore = world.SlotWrites;

            Assert.Equal(InventoryEquipOutcome.SlotEmpty, inventory.TryEquip(Player, 9));
            Assert.Equal(InventoryEquipOutcome.SlotEmpty, inventory.TryMove(Player, 9, 10));
            Assert.Equal(InventoryEquipOutcome.SlotEmpty, inventory.TryDestroy(Player, 9, 1));
            Assert.Equal(InventoryEquipOutcome.SlotEmpty, inventory.TryUnequip(Player, 103));
            // Cell 10 is covered by the blade (a 2x3 anchored at 2) without being its anchor: no item starts there.
            Assert.Equal(InventoryEquipOutcome.SlotEmpty, inventory.TryEquip(Player, 10));
            Assert.Equal(InventoryEquipOutcome.SlotEmpty, inventory.TryMove(Player, 10, 40));
            Assert.Equal(InventoryEquipOutcome.SlotEmpty, inventory.TryDestroy(Player, 10, 2));
            Assert.Equal(writesBefore, world.SlotWrites);
        }
    }

    // Only a hand-edited row can hold the worn item in the bag as well (no action leaves one), so the
    // fixture writes both rows. Equipping what the target holds, moving a cell onto itself and "moving"
    // a worn item to its own slot are accepted and write nothing.
    [Fact]
    public void ReEquipOfWhatTheTargetAlreadyHolds_IsAccepted_AndWritesNothing()
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
            int writesBefore = world.SlotWrites;

            Assert.Equal(InventoryEquipOutcome.Equipped, inventory.TryEquip(Player, 0));
            Assert.Equal(InventoryEquipOutcome.Equipped, inventory.TryEquip(Player, 100, 100));
            Assert.Equal(InventoryEquipOutcome.Equipped, inventory.TryMove(Player, 0, 0));

            Assert.Equal(writesBefore, world.SlotWrites);
        }
    }

    [Fact]
    public void RolledItem_UsesItsRollInsteadOfTheDefinition_AndKeepsItWhenTheSlotIsNotAllowed()
    {
        EquipmentTestWorld world = new();
        ItemRoll roll = new(ItemRarity.Rare, [new StatModifier(StatId.Strength, StatOp.Flat, 6), new StatModifier(StatId.Armor, StatOp.Flat, 4)]);
        world.PutItem(EquipmentTestWorld.MakeItem(50, Player, "gear.iron-blade", roll));
        world.PutSlot(Player, 0, 50);
        world.PutGrant(Player, ToolCatalog.CurrentStarterKitVersion);
        PlayerInventoryService inventory = Create(world, out PersistenceWriteQueue writes);
        using (writes)
        {
            Assert.Equal(InventoryEquipOutcome.Equipped, inventory.TryEquip(Player, 0));
            Assert.Equal(roll.Modifiers.ToArray(), Mods(Loadout(inventory), EquipmentSlot.MainHand));

            InventoryItemSnapshot item = inventory.BuildSnapshot(Player).EquippedSlot!.Value.Item!.Value;
            Assert.True(item.IsRolled);
            Assert.Equal(ItemRarity.Rare, item.Rarity);
            Assert.Equal(roll.Modifiers.ToArray(), item.Modifiers!.ToArray());
            Assert.Equal(EquipmentSlots.MaskOf(EquipmentSlot.MainHand), item.EquipSlots);
        }
    }

    [Fact]
    public void RetiredKind_IsInert_AndItsRowIsKept_EvenWhenRolled()
    {
        EquipmentTestWorld world = new();
        PersistedItemInstance retired = EquipmentTestWorld.MakeItem(60, Player, "tool.torch") with
        {
            ItemKind = "tool.retired-thing",
            Roll = new ItemRoll(ItemRarity.Rare, [new StatModifier(StatId.Strength, StatOp.Flat, 9)]),
        };
        world.PutItem(retired);
        world.PutSlot(Player, 100, 60);
        world.PutGrant(Player, ToolCatalog.CurrentStarterKitVersion);
        PlayerInventoryService inventory = Create(world, out PersistenceWriteQueue writes);
        using (writes)
        {
            Assert.True(inventory.Ensure(Player));

            EquipmentLoadout loadout = Loadout(inventory);
            Assert.Empty(Mods(loadout, EquipmentSlot.MainHand));
            Assert.Equal(HeldLightKind.None, inventory.ResolveEquippedHeldLight(Player));
            Assert.Equal(0UL, inventory.ResolveModifierDigest(Player));
            InventoryItemSnapshot shown = inventory.BuildSnapshot(Player).EquippedSlot!.Value.Item!.Value;
            Assert.Null(shown.Modifiers);
            Assert.Equal(0, shown.EquipSlots);
            // A worn item of a kind the catalog does not know cannot be moved to another slot either.
            Assert.Equal(InventoryEquipOutcome.NotEquippable, inventory.TryEquip(Player, 100, 101));
            Assert.Equal(60L, world.SlotsOf(Player)[100]);
        }
    }

    [Fact]
    public void DamagedRoll_GivesNoStats_AndNothingThrows()
    {
        EquipmentTestWorld world = new();
        world.PutItem(EquipmentTestWorld.MakeItem(70, Player, "gear.iron-blade", ItemRoll.Damaged));
        world.PutSlot(Player, 100, 70);
        world.PutGrant(Player, ToolCatalog.CurrentStarterKitVersion);
        PlayerInventoryService inventory = Create(world, out PersistenceWriteQueue writes);
        using (writes)
        {
            Assert.True(inventory.Ensure(Player));

            Assert.Empty(Mods(Loadout(inventory), EquipmentSlot.MainHand));
            Assert.Equal(0UL, inventory.ResolveModifierDigest(Player));
            InventoryItemSnapshot shown = inventory.BuildSnapshot(Player).EquippedSlot!.Value.Item!.Value;
            Assert.True(shown.IsRolled);
            Assert.Null(shown.Modifiers);
        }
    }

    [Fact]
    public void ChangingTheDefinition_NeverChangesARolledItem_UnrolledItemsReadTheDefinition()
    {
        // A rolled item reads only its roll: its modifiers differ from the definition's and a second
        // read gives the same values (the definition is not consulted for them).
        EquipmentTestWorld world = new();
        ItemRoll roll = new(ItemRarity.Common, [new StatModifier(StatId.Vitality, StatOp.Flat, 3)]);
        world.PutItem(EquipmentTestWorld.MakeItem(80, Player, "tool.field-blade", roll));
        world.PutSlot(Player, 100, 80);
        world.PutGrant(Player, ToolCatalog.CurrentStarterKitVersion);
        PlayerInventoryService inventory = Create(world, out PersistenceWriteQueue writes);
        using (writes)
        {
            Assert.True(inventory.Ensure(Player));
            StatModifier only = Assert.Single(Mods(Loadout(inventory), EquipmentSlot.MainHand));
            Assert.Equal(new StatModifier(StatId.Vitality, StatOp.Flat, 3), only);
            Assert.NotEqual(ToolCatalog.Resolve("tool.field-blade").Modifiers.ToArray(), new[] { only });
        }
    }
}
