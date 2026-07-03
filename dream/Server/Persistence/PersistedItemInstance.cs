using OpenTK.Mathematics;

namespace Dream.Server.Persistence;

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
    DateTime UpdatedUtc);
