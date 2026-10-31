using Dream.Simulation.Stats;

namespace Dream.Simulation.Inventory;

public readonly record struct ToolDefinition(
    string ItemKind,
    string DisplayName,
    string Description,
    string VisualId,
    IReadOnlyList<string> DetailLines,
    IReadOnlyList<StatModifier> Modifiers,
    HeldLightKind HeldLight = HeldLightKind.None);
