using System.Text.Json.Serialization;
using Dream.Simulation.Stats;

namespace Dream.Simulation.Inventory;

// One item as the owner sees it. The trailing fields are wave H's (protocol v10) and are left off the
// wire at their defaults: Modifiers are the effective modifiers of the instance (a rolled item's roll,
// else its definition's; null, never empty, when there are none, and for an inert item), HeldLight
// the light it casts, EquipSlots the EquipmentSlots mask it may occupy, Rarity and IsRolled how it
// was made.
public readonly record struct InventoryItemSnapshot(
    long ItemInstanceId,
    string ItemKind,
    string DisplayName,
    string VisualId,
    int StackCount,
    string Description,
    IReadOnlyList<string> DetailLines,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<StatModifier>? Modifiers = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] HeldLightKind HeldLight = HeldLightKind.None,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] int EquipSlots = 0,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] ItemRarity Rarity = ItemRarity.Common,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool IsRolled = false);
