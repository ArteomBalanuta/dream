namespace Dream.Simulation.Inventory;

public readonly record struct InventoryItemSnapshot(
    long ItemInstanceId,
    string ItemKind,
    string DisplayName,
    string VisualId,
    int StackCount,
    string Description,
    IReadOnlyList<string> DetailLines);
