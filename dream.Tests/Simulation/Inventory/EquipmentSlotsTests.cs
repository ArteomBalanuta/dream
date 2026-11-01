using Dream.Simulation.Inventory;
using Dream.Simulation.Stats;
using Xunit;

namespace Dream.Tests.Simulation.Inventory;

// Wave H C0: the equipment slot indices (100..111 since classic HUD iteration 2) and the per-slot modifier store.
public sealed class EquipmentSlotsTests
{
    [Theory]
    [InlineData(100, EquipmentSlot.MainHand)]
    [InlineData(101, EquipmentSlot.OffHand)]
    [InlineData(102, EquipmentSlot.Head)]
    [InlineData(103, EquipmentSlot.Body)]
    [InlineData(104, EquipmentSlot.Hands)]
    [InlineData(105, EquipmentSlot.Feet)]
    [InlineData(106, EquipmentSlot.Legs)]
    [InlineData(107, EquipmentSlot.Neck)]
    [InlineData(108, EquipmentSlot.RingLeft)]
    [InlineData(109, EquipmentSlot.RingRight)]
    [InlineData(110, EquipmentSlot.Back)]
    [InlineData(111, EquipmentSlot.Companion)]
    public void TheTwelveSlotIndices_MapBothWays(int index, EquipmentSlot expected)
    {
        Assert.True(EquipmentSlots.TryFromSlotIndex(index, out EquipmentSlot slot));
        Assert.Equal(expected, slot);
        Assert.Equal(index, EquipmentSlots.ToSlotIndex(slot));
    }

    [Theory]
    [InlineData(99)]
    [InlineData(112)]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(255)]
    [InlineData(356)]
    [InlineData(int.MaxValue)]
    [InlineData(int.MinValue)]
    public void AnyOtherIndex_IsNotAnEquipmentSlot(int index)
    {
        Assert.False(EquipmentSlots.TryFromSlotIndex(index, out _));
    }

    [Fact]
    public void TheConstants_AreThePlanned()
    {
        Assert.Equal(12, EquipmentSlots.Count);
        Assert.Equal(100, EquipmentSlots.FirstSlotIndex);
        Assert.Equal(111, EquipmentSlots.LastSlotIndex);
        Assert.Equal(InventoryConstants.EquippedSlotIndex, EquipmentSlots.ToSlotIndex(EquipmentSlot.MainHand));
        Assert.Equal([1, 2, 4, 8, 16, 32, 64, 128, 256, 512, 1024, 2048], Enum.GetValues<EquipmentSlot>().Select(EquipmentSlots.MaskOf));
        // The enum values are the StatSheet source slots: the original four keep sources 0..3 and the
        // appended eight take 4..11, all inside the sheet's capacity.
        Assert.Equal([0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11], Enum.GetValues<EquipmentSlot>().Select(slot => (int)slot));
        Assert.Equal(EquipmentSlots.Count, Enum.GetValues<EquipmentSlot>().Length);
        Assert.True(EquipmentSlots.Count <= StatSheet.SourceCapacity);
    }

    [Theory]
    [InlineData(HeldLightKind.None, HeldLightKind.None, HeldLightKind.None)]
    [InlineData(HeldLightKind.Torch, HeldLightKind.None, HeldLightKind.Torch)]
    [InlineData(HeldLightKind.None, HeldLightKind.Lantern, HeldLightKind.Lantern)]
    [InlineData(HeldLightKind.Torch, HeldLightKind.Lantern, HeldLightKind.Lantern)]
    [InlineData(HeldLightKind.Lantern, HeldLightKind.Torch, HeldLightKind.Torch)]
    public void TheOffHandLightWins_ElseTheMainHands(HeldLightKind main, HeldLightKind off, HeldLightKind expected)
    {
        Assert.Equal(expected, EquipmentSlots.ResolveHeldLight(main, off));
    }

    [Fact]
    public void ALoadout_StartsEmpty_KeepsSlotsApart_AndClears()
    {
        EquipmentLoadout loadout = new();
        foreach (EquipmentSlot slot in Enum.GetValues<EquipmentSlot>())
        {
            Assert.True(loadout.Get(slot).IsEmpty);
        }

        loadout.Set(EquipmentSlot.OffHand, [new StatModifier(StatId.Strength, StatOp.Flat, 2f)]);
        loadout.Set(EquipmentSlot.Body, [new StatModifier(StatId.Armor, StatOp.Flat, 5f), new StatModifier(StatId.Vitality, StatOp.Flat, 1f)]);

        Assert.True(loadout.Get(EquipmentSlot.MainHand).IsEmpty);
        Assert.Equal(1, loadout.Get(EquipmentSlot.OffHand).Length);
        Assert.Equal(StatId.Strength, loadout.Get(EquipmentSlot.OffHand)[0].Stat);
        Assert.True(loadout.Get(EquipmentSlot.Head).IsEmpty);
        Assert.Equal(2, loadout.Get(EquipmentSlot.Body).Length);
        Assert.Equal(StatId.Vitality, loadout.Get(EquipmentSlot.Body)[1].Stat);

        loadout.Clear();
        foreach (EquipmentSlot slot in Enum.GetValues<EquipmentSlot>())
        {
            Assert.True(loadout.Get(slot).IsEmpty);
        }
    }

    [Fact]
    public void ALoadout_SkipsInvalidModifiers_AndStopsAtEight()
    {
        EquipmentLoadout loadout = new();
        loadout.Set(
            EquipmentSlot.MainHand,
            [
                new StatModifier(StatId.Strength, StatOp.Flat, float.NaN),
                new StatModifier(StatId.Strength, StatOp.Flat, float.PositiveInfinity),
                new StatModifier(StatId.Count, StatOp.Flat, 1f),
                new StatModifier((StatId)200, StatOp.Flat, 1f),
                new StatModifier(StatId.Strength, (StatOp)9, 1f),
                new StatModifier(StatId.Armor, StatOp.Flat, 3f),
            ]);

        Assert.Equal(1, loadout.Get(EquipmentSlot.MainHand).Length);
        Assert.Equal(StatId.Armor, loadout.Get(EquipmentSlot.MainHand)[0].Stat);

        loadout.Set(EquipmentSlot.Head, [.. Enumerable.Range(0, 40).Select(i => new StatModifier(StatId.Strength, StatOp.Flat, i))]);
        Assert.Equal(StatSheet.MaxModifiersPerSource, loadout.Get(EquipmentSlot.Head).Length);
        Assert.Equal(7f, loadout.Get(EquipmentSlot.Head)[7].Value);
        Assert.True(loadout.Get((EquipmentSlot)9).IsEmpty);

        // Whatever Set keeps, a StatSheet accepts.
        StatSheet sheet = new();
        sheet.SetSource(0, loadout.Get(EquipmentSlot.MainHand));
        sheet.SetSource(2, loadout.Get(EquipmentSlot.Head));
    }
}
