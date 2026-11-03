using Dream.Simulation.Inventory;
using Dream.Simulation.Stats;

namespace Dream.Simulation.Content;

// One stat range of a roll table. Strength, Agility, Vitality, Energy and Armor roll integers inclusive; Attack Speed
// rolls min + k * 0.05 up to max.
public readonly record struct ItemRollRange(StatId Stat, float Min, float Max);

// The ranges a rolled item draws, per rarity, in file order (which is the order of the rolled modifiers).
public sealed record ItemRollTable(
    IReadOnlyList<ItemRollRange> Common,
    IReadOnlyList<ItemRollRange> Uncommon,
    IReadOnlyList<ItemRollRange> Rare)
{
    public IReadOnlyList<ItemRollRange> For(ItemRarity rarity) => rarity switch
    {
        ItemRarity.Common => Common,
        ItemRarity.Uncommon => Uncommon,
        ItemRarity.Rare => Rare,
        _ => throw new ArgumentOutOfRangeException(nameof(rarity), rarity, "Unknown item rarity."),
    };
}

// An item as authored in items.json. Instances reference it by Key (item_kind), with no foreign key.
// Modifiers are the flat modifiers of an unrolled instance, in file order. EquipSlots is in file
// order and its first entry is the default target; EquipSlotMask is the same set as a bit mask
// (EquipmentSlots.MaskOf). Rolls is null for an item that cannot be rolled. Footprint is the size in
// bag cells (1..4 by 1..4; the file's optional "footprint": [cols, rows], 1x1 when absent). SellPrice is
// the gold a vendor pays for a common copy (the optional "sellPrice"; 0, the default, means the item
// cannot be sold); VendorRules.SellPrice applies the rarity multiplier. Use is set for a consumable
// (the optional "use" object); such an item may list no equipment slots (mask 0, never equippable).
public sealed record ItemDefinition(
    string Key,
    string DisplayName,
    string Description,
    string VisualId,
    IReadOnlyList<string> DetailLines,
    HeldLightKind HeldLight,
    IReadOnlyList<StatModifier> Modifiers,
    int EquipSlotMask,
    IReadOnlyList<EquipmentSlot> EquipSlots,
    ItemRollTable? Rolls,
    ItemFootprint Footprint,
    int SellPrice = 0,
    ItemUseDefinition? Use = null);
