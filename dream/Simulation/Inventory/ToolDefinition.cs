namespace Dream.Simulation.Inventory;

public readonly record struct ToolDefinition(
    string ItemKind,
    string DisplayName,
    string Description,
    string VisualId,
    IReadOnlyList<string> DetailLines,
    ToolStatModifier StatModifier);
