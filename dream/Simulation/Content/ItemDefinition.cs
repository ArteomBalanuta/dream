using Dream.Simulation.Inventory;

namespace Dream.Simulation.Content;

// The stats an item modifier can touch today. Matches what ToolStatModifier can carry.
public enum ContentStat : byte
{
    Strength = 0,
    AttackSpeed = 1,
    Vitality = 2,
}

// One flat modifier of an item definition, in file order.
public readonly record struct ItemModifierDefinition(ContentStat Stat, float Value);

// An item as authored in items.json. Instances reference it by Key (item_kind), with no foreign key.
public sealed record ItemDefinition(
    string Key,
    string DisplayName,
    string Description,
    string VisualId,
    IReadOnlyList<string> DetailLines,
    HeldLightKind HeldLight,
    IReadOnlyList<ItemModifierDefinition> Modifiers,
    ToolStatModifier StatModifier);
