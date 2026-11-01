using Xunit;
using Dream.Rendering.Hud;
using Dream.Simulation.Inventory;
using Dream.Simulation.Players;
using Dream.Simulation.Stats;

namespace Dream.Tests.Rendering.Hud;

// Wave H3: the character panel's logic without ImGui. Layout and colours on screen need a person.
public sealed class CharacterPanelModelTests
{
    private static HudStatusSnapshot Status(
        int strength, float attackSpeed, int vitality, int armor,
        int baseStrength, float baseAttackSpeed, int baseVitality) =>
        new(100, 100, PlayerInteractionMode.Combat, 1, 2, strength, attackSpeed, vitality, 1, 0, 100,
            baseStrength, baseAttackSpeed, baseVitality, armor);

    private static InventoryItemSnapshot Item(
        long id, string name, int slotMask = 0, ItemRarity rarity = ItemRarity.Common, bool rolled = false,
        params StatModifier[] modifiers) =>
        new(id, "kind", name, "held.debug", 1, "desc", [], modifiers.Length == 0 ? null : modifiers,
            HeldLightKind.None, slotMask, rarity, rolled);

    private static PlayerInventorySnapshot FullInventory() => new(
        "p", 4, 4,
        [
            new InventorySlotSnapshot(0, Item(1, "Blade", 1, ItemRarity.Rare, true), IsEquipped: true),
            new InventorySlotSnapshot(3, Item(5, "Torch", 3)),
            new InventorySlotSnapshot(5, Item(6, "Rolled Cap", 4, ItemRarity.Uncommon, true,
                new StatModifier(StatId.Armor, StatOp.Flat, 3f)))
        ],
        new InventorySlotSnapshot(100, Item(1, "Blade", 1, ItemRarity.Rare, true), IsEquipped: true),
        [
            new InventorySlotSnapshot(101, Item(2, "Lantern", 3), IsEquipped: true),
            new InventorySlotSnapshot(102, Item(3, "Cap", 4), IsEquipped: true),
            new InventorySlotSnapshot(103, Item(4, "Vest", 8), IsEquipped: true)
        ]);

    [Fact]
    public void StatRows_BasePlusBonusEqualsEffective_ForAFourSlotFixture()
    {
        // Base 10/2.0/10 plus gear +5, +0.75, +3 and Armor 9.
        HudStatusSnapshot status = Status(15, 2.75f, 13, 9, 10, 2.0f, 10);

        CharacterStatRow[] rows = CharacterPanelModel.BuildStatRows(status);

        Assert.Equal(4, rows.Length);
        Assert.Equal(["Strength", "Attack Speed", "Vitality", "Armor"], rows.Select(r => r.Label));
        foreach (CharacterStatRow row in rows)
        {
            Assert.Equal(row.Effective, row.Base + row.Bonus, 4);
        }

        Assert.Equal("10", rows[0].BaseText);
        Assert.Equal("+5", rows[0].BonusText);
        Assert.Equal("15", rows[0].EffectiveText);
        Assert.Equal("2", rows[1].BaseText);
        Assert.Equal("+0.75", rows[1].BonusText);
        Assert.Equal("2.75", rows[1].EffectiveText);
        Assert.Equal("+3", rows[2].BonusText);
        Assert.Equal("9", rows[3].EffectiveText);
    }

    [Fact]
    public void StatRows_ACappedStatShowsTheCappedEffective()
    {
        // Base 48 plus a +6 item is capped at 50: the bonus shown is what actually applied.
        HudStatusSnapshot status = Status(50, 2f, 10, 0, 48, 2f, 10);

        CharacterStatRow strength = CharacterPanelModel.BuildStatRows(status)[0];

        Assert.Equal("50", strength.EffectiveText);
        Assert.Equal("+2", strength.BonusText);
        Assert.Equal(strength.Effective, strength.Base + strength.Bonus);
    }

    [Fact]
    public void StatRows_NoGear_ShowNoBonus_AndNegativeBonusesKeepTheirSign()
    {
        CharacterStatRow[] plain = CharacterPanelModel.BuildStatRows(Status(10, 2f, 10, 0, 10, 2f, 10));
        CharacterStatRow[] cursed = CharacterPanelModel.BuildStatRows(Status(8, 1.5f, 10, 0, 10, 2f, 10));

        Assert.All(plain, row => Assert.Equal(string.Empty, row.BonusText));
        Assert.Equal("-2", cursed[0].BonusText);
        Assert.Equal("-0.5", cursed[1].BonusText);
    }

    [Fact]
    public void ArmorRow_SaysItReducesDamageTaken_AndOnlyArmorDoes()
    {
        CharacterStatRow[] rows = CharacterPanelModel.BuildStatRows(Status(10, 2f, 10, 12, 10, 2f, 10));

        Assert.Equal("reduces damage taken", rows[3].Note);
        Assert.Equal(CharacterStat.Armor, rows[3].Stat);
        Assert.All(rows.Take(3), row => Assert.Null(row.Note));
    }

    [Fact]
    public void StatRows_DoNotShowAgilityEnergyOrMovementSpeed()
    {
        string[] labels = CharacterPanelModel.BuildStatRows(default).Select(r => r.Label).ToArray();

        Assert.DoesNotContain("Agility", labels);
        Assert.DoesNotContain("Energy", labels);
        Assert.DoesNotContain("Movement Speed", labels);
    }

    [Fact]
    public void SlotRows_AreTheTwelveSlotsInOrder_WithTheirItems()
    {
        CharacterSlotRow[] rows = CharacterPanelModel.BuildSlotRows(FullInventory());

        Assert.Equal(Enum.GetValues<EquipmentSlot>(), rows.Select(r => r.Slot));
        Assert.Equal(Enumerable.Range(100, 12), rows.Select(r => r.SlotIndex));
        Assert.Equal(["Blade", "Lantern", "Cap", "Vest"], rows.Take(4).Select(r => r.ItemLabel));
        Assert.All(rows.Skip(4), row => Assert.Null(row.Item));
        Assert.Equal(
            ["Main Hand", "Off Hand", "Head", "Body", "Hands", "Feet", "Legs", "Neck", "Left Ring", "Right Ring", "Back", "Companion"],
            rows.Select(r => r.SlotLabel));
    }

    [Fact]
    public void SlotRows_EmptyAndHostileEntriesAreEmptySlots()
    {
        PlayerInventorySnapshot hostile = new(
            "p", 4, 4, [], null,
            [
                new InventorySlotSnapshot(100, Item(1, "Sneaky main")),
                new InventorySlotSnapshot(99, Item(2, "Below")),
                new InventorySlotSnapshot(112, Item(3, "Above")),
                new InventorySlotSnapshot(int.MinValue, Item(4, "Min")),
                new InventorySlotSnapshot(102, null)
            ]);

        CharacterSlotRow[] rows = CharacterPanelModel.BuildSlotRows(hostile);

        Assert.All(rows, row => Assert.Null(row.Item));
        Assert.All(rows, row => Assert.Equal("Empty", row.ItemLabel));
        Assert.Equal(EquipmentSlots.Count, CharacterPanelModel.BuildSlotRows(null).Length);
    }

    [Theory]
    [InlineData(ItemRarity.Common, RarityColorKey.Common)]
    [InlineData(ItemRarity.Uncommon, RarityColorKey.Uncommon)]
    [InlineData(ItemRarity.Rare, RarityColorKey.Rare)]
    [InlineData((ItemRarity)200, RarityColorKey.Common)]
    public void RarityColor_Keys(ItemRarity rarity, RarityColorKey expected)
    {
        Assert.Equal(expected, CharacterPanelModel.ColorKey(rarity));
    }

    [Fact]
    public void SlotRow_CarriesTheItemsRarityColor()
    {
        CharacterSlotRow[] rows = CharacterPanelModel.BuildSlotRows(FullInventory());

        Assert.Equal(RarityColorKey.Rare, rows[0].Color);
        Assert.Equal(RarityColorKey.Common, rows[1].Color);
    }

    [Fact]
    public void Tooltip_ListsARolledItemsModifiers_AndNothingForUnrolledOnes()
    {
        InventoryItemSnapshot rolled = Item(1, "Blade", 1, ItemRarity.Rare, true,
            new StatModifier(StatId.Strength, StatOp.Flat, 6f),
            new StatModifier(StatId.AttackSpeed, StatOp.Flat, 1.75f),
            new StatModifier(StatId.Armor, StatOp.Flat, -4f),
            new StatModifier(StatId.Agility, StatOp.Flat, 3f),
            new StatModifier(StatId.Strength, StatOp.Percent, 3f),
            new StatModifier(StatId.Strength, StatOp.Flat, float.NaN));

        List<string> lines = CharacterPanelModel.TooltipLines(rolled);

        Assert.Equal(["Rare", "+6 Strength", "+1.75 Attack Speed", "-4 Armor"], lines);
        Assert.Empty(CharacterPanelModel.TooltipLines(Item(2, "Torch")));
    }

    [Fact]
    public void Tooltip_IsBoundedForAHostileModifierList()
    {
        StatModifier[] many = Enumerable.Repeat(new StatModifier(StatId.Strength, StatOp.Flat, 1f), 500).ToArray();

        List<string> lines = CharacterPanelModel.TooltipLines(Item(1, "x", 1, ItemRarity.Rare, true, many));

        Assert.True(lines.Count <= CharacterPanelModel.MaxTooltipLines + 1);
    }

    [Fact]
    public void AllowedSlots_DecodeTheMask_InSlotOrder_IgnoringBadBits()
    {
        Assert.Equal([EquipmentSlot.MainHand, EquipmentSlot.OffHand], CharacterPanelModel.AllowedSlots(3));
        Assert.Equal([EquipmentSlot.Body], CharacterPanelModel.AllowedSlots(8));
        Assert.Empty(CharacterPanelModel.AllowedSlots(0));
        Assert.Empty(CharacterPanelModel.AllowedSlots(-4096));
        Assert.Equal(
            [EquipmentSlot.Hands, EquipmentSlot.Feet, EquipmentSlot.Legs, EquipmentSlot.Neck, EquipmentSlot.RingLeft, EquipmentSlot.RingRight, EquipmentSlot.Back, EquipmentSlot.Companion],
            CharacterPanelModel.AllowedSlots(-16 & 0xFFF));
        Assert.Equal(EquipmentSlots.Count, CharacterPanelModel.AllowedSlots(-1).Count);
    }

    [Fact]
    public void BackpackMenu_OffersEachAllowedSlot_AsOneEquipIntentEach()
    {
        PlayerInventorySnapshot inventory = FullInventory();

        List<CharacterMenuEntry> menu = CharacterPanelModel.BackpackMenu(inventory, 3, Item(5, "Torch", 3));

        CharacterMenuEntry[] equips = menu.Where(e => e.Action.Kind == InventoryHudActionKind.EquipTo).ToArray();
        Assert.Equal(2, equips.Length);
        Assert.Equal(new InventoryHudAction(InventoryHudActionKind.EquipTo, 3, 100), equips[0].Action);
        Assert.Equal(new InventoryHudAction(InventoryHudActionKind.EquipTo, 3, 101), equips[1].Action);
        Assert.All(equips, e => Assert.False(e.RequiresConfirm));
    }

    [Fact]
    public void BackpackMenu_Destroy_NamesTheItem_AndNeedsTheConfirm_ForARolledItem()
    {
        InventoryItemSnapshot rolled = Item(6, "Rolled Cap", 4, ItemRarity.Uncommon, true,
            new StatModifier(StatId.Armor, StatOp.Flat, 3f));

        List<CharacterMenuEntry> menu = CharacterPanelModel.BackpackMenu(FullInventory(), 5, rolled);

        CharacterMenuEntry destroy = Assert.Single(menu, e => e.Action.Kind == InventoryHudActionKind.Destroy);
        Assert.True(destroy.RequiresConfirm);
        Assert.Equal(new InventoryHudAction(InventoryHudActionKind.Destroy, 5, 0, 6), destroy.Action);
        Assert.Single(menu, e => e.RequiresConfirm);
    }

    [Fact]
    public void BackpackMenu_ForAnEquippedItem_OffersUnequipOfItsSlot_AndNoDestroy()
    {
        PlayerInventorySnapshot inventory = FullInventory();

        List<CharacterMenuEntry> menu = CharacterPanelModel.BackpackMenu(inventory, 0, Item(1, "Blade", 1, ItemRarity.Rare, true));

        CharacterMenuEntry unequip = Assert.Single(menu, e => e.Action.Kind == InventoryHudActionKind.Unequip);
        Assert.Equal(100, unequip.Action.SlotIndex);
        Assert.DoesNotContain(menu, e => e.Action.Kind == InventoryHudActionKind.Destroy);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(64)]
    [InlineData(100)]
    [InlineData(int.MaxValue)]
    public void BackpackMenu_AndMoveTargets_AreEmptyForASlotOutsideTheBackpack(int slot)
    {
        PlayerInventorySnapshot inventory = FullInventory();

        Assert.Empty(CharacterPanelModel.BackpackMenu(inventory, slot, Item(1, "x", 1)));
        Assert.Empty(CharacterPanelModel.MoveTargets(inventory, slot));
    }

    [Fact]
    public void EquipmentMenu_IsOneUnequipIntent_OfThatSlot()
    {
        CharacterMenuEntry entry = Assert.Single(CharacterPanelModel.EquipmentMenu(EquipmentSlot.OffHand));

        Assert.Equal(new InventoryHudAction(InventoryHudActionKind.Unequip, 101), entry.Action);
        Assert.False(entry.RequiresConfirm);
    }

    [Fact]
    public void MoveTargets_AreEveryOtherBackpackSlot_AndOneMoveIntentEach()
    {
        List<InventoryHudAction> targets = CharacterPanelModel.MoveTargets(FullInventory(), 5);

        Assert.Equal(15, targets.Count);
        Assert.DoesNotContain(targets, t => t.Target == 5);
        Assert.All(targets, t =>
        {
            Assert.Equal(InventoryHudActionKind.Move, t.Kind);
            Assert.Equal(5, t.SlotIndex);
            Assert.InRange(t.Target, 0, 15);
        });
    }

    [Theory]
    [InlineData(int.MaxValue, int.MaxValue)]
    [InlineData(-3, 4)]
    [InlineData(0, 0)]
    public void MoveTargets_ClampAHostileGridSize(int columns, int rows)
    {
        PlayerInventorySnapshot inventory = new("p", columns, rows, []);

        List<InventoryHudAction> targets = CharacterPanelModel.MoveTargets(inventory, 0);

        // The grid is clamped to the bag's cells (8x8 since classic HUD iteration 2), never past them.
        Assert.True(targets.Count <= InventoryConstants.BackpackSlotCount - 1);
        Assert.All(targets, t => Assert.InRange(t.Target, 0, InventoryConstants.BackpackSlotCount - 1));
    }

    [Fact]
    public void FindEquipmentSlotHolding_FindsTheSlot_OrMinusOne()
    {
        PlayerInventorySnapshot inventory = FullInventory();

        Assert.Equal(102, CharacterPanelModel.FindEquipmentSlotHolding(inventory, 3));
        Assert.Equal(-1, CharacterPanelModel.FindEquipmentSlotHolding(inventory, 999));
    }
}
