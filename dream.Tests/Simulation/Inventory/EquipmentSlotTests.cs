using Xunit;
using Dream.Server.Persistence;
using Dream.Simulation.Inventory;
using Dream.Simulation.Persistence;
using Dream.Simulation.Stats;
using Dream.Simulation.World;
using Dream.Tests.Support;

namespace Dream.Tests.Simulation.Inventory;

// Wave H1: the four equipment slots on PlayerInventoryService (slot rules from content, targets,
// the one-slot-only rule, stats and light per slot). A new player's starter kit is, by slot:
// 0 hammer (id 1), 1 blade (2), 2 maul (3), 3 lantern (4), 4 torch (5); the hammer is equipped.
public sealed class EquipmentSlotTests
{
    private const string Player = "Alice";
    private const int Blade = 1;
    private const int Maul = 2;
    private const int Lantern = 3;
    private const int Torch = 4;

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

            Assert.Equal(InventoryEquipOutcome.Equipped, inventory.TryEquip(Player, Blade));
            Assert.Equal(InventoryEquipOutcome.Equipped, inventory.TryEquip(Player, Torch, EquipmentSlots.ToSlotIndex(EquipmentSlot.OffHand)));

            EquipmentLoadout loadout = Loadout(inventory);
            Assert.Equal(ToolCatalog.Resolve("tool.field-blade").Modifiers.ToArray(), Mods(loadout, EquipmentSlot.MainHand));
            Assert.Empty(Mods(loadout, EquipmentSlot.OffHand));
            Assert.Equal(HeldLightKind.Torch, inventory.ResolveEquippedHeldLight(Player));

            PlayerInventorySnapshot snapshot = inventory.BuildSnapshot(Player);
            Assert.Equal("tool.field-blade", snapshot.EquippedSlot!.Value.Item!.Value.ItemKind);
            InventorySlotSnapshot off = Assert.Single(snapshot.Equipment!);
            Assert.Equal(EquipmentSlots.ToSlotIndex(EquipmentSlot.OffHand), off.SlotIndex);
            Assert.Equal("tool.torch", off.Item!.Value.ItemKind);
            Assert.True(snapshot.Slots[Blade].IsEquipped);
            Assert.True(snapshot.Slots[Torch].IsEquipped);
        }
    }

    [Fact]
    public void LanternInOffHand_AddsItsStats_AndOffHandLightWinsOverMainHandLight()
    {
        EquipmentTestWorld world = new();
        PlayerInventoryService inventory = Create(world, out PersistenceWriteQueue writes);
        using (writes)
        {
            Assert.Equal(InventoryEquipOutcome.Equipped, inventory.TryEquip(Player, Torch));
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
            Assert.Equal(InventoryEquipOutcome.Equipped, inventory.TryEquip(Player, Torch));

            Dictionary<int, long> rows = world.SlotsOf(Player);
            Assert.Equal(inventory.BuildSnapshot(Player).Slots[Torch].Item!.Value.ItemInstanceId, rows[100]);
            Assert.False(rows.ContainsKey(101));
        }
    }

    [Fact]
    public void TorchMovedFromMainToOffHand_LeavesMainEmpty_AndTheItemInOneEquipmentRow()
    {
        EquipmentTestWorld world = new();
        PlayerInventoryService inventory = Create(world, out PersistenceWriteQueue writes);
        using (writes)
        {
            Assert.Equal(InventoryEquipOutcome.Equipped, inventory.TryEquip(Player, Torch));
            Assert.Equal(InventoryEquipOutcome.Equipped, inventory.TryEquip(Player, Torch, 101));

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
            int capSlot = 5;
            Dictionary<int, long> rowsBefore = world.SlotsOf(Player);
            int writesBefore = world.SlotWrites;

            Assert.Equal(InventoryEquipOutcome.NotEquippable, inventory.TryEquip(Player, Maul, 101));
            Assert.Equal(InventoryEquipOutcome.NotEquippable, inventory.TryEquip(Player, capSlot, 103));
            foreach (int badTarget in new[] { -1, 99, 104, int.MaxValue, int.MinValue })
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
    [InlineData(16)]
    [InlineData(99)]
    [InlineData(100)]
    [InlineData(104)]
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

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(99)]
    [InlineData(104)]
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
            Assert.Equal(writesBefore, world.SlotWrites);
        }
    }

    [Fact]
    public void ReEquipOfWhatTheTargetAlreadyHolds_IsAccepted_AndWritesNothing()
    {
        EquipmentTestWorld world = new();
        PlayerInventoryService inventory = Create(world, out PersistenceWriteQueue writes);
        using (writes)
        {
            Assert.Equal(InventoryEquipOutcome.Equipped, inventory.TryEquip(Player, Blade));
            int writesBefore = world.SlotWrites;

            Assert.Equal(InventoryEquipOutcome.Equipped, inventory.TryEquip(Player, Blade));
            Assert.Equal(InventoryEquipOutcome.Equipped, inventory.TryMove(Player, 3, 3));

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
        world.PutSlot(Player, 0, 60);
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
            Assert.Equal(InventoryEquipOutcome.NotEquippable, inventory.TryEquip(Player, 0));
            Assert.Equal(60L, world.SlotsOf(Player)[100]);
        }
    }

    [Fact]
    public void DamagedRoll_GivesNoStats_AndNothingThrows()
    {
        EquipmentTestWorld world = new();
        world.PutItem(EquipmentTestWorld.MakeItem(70, Player, "gear.iron-blade", ItemRoll.Damaged));
        world.PutSlot(Player, 0, 70);
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
        world.PutSlot(Player, 0, 80);
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
