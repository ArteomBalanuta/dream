using OpenTK.Mathematics;
using Dream.Simulation.Inventory;

namespace Dream.Simulation.Persistence;

public readonly record struct PersistedItemInstance(
    long ItemInstanceId,
    string ItemKind,
    string DisplayName,
    string VisualId,
    string? OwnerPlayerName,
    Vector3? WorldPosition,
    string State,
    int StackCount,
    DateTime CreatedUtc,
    DateTime UpdatedUtc,
    ItemRoll? Roll = null);
