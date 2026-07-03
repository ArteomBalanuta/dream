using OpenTK.Mathematics;

namespace Dream.Server.Persistence;

public readonly record struct PersistedPlayerWorldState(
    string PlayerName,
    Vector3 Position,
    Vector3 Facing,
    int CurrentHealth,
    int CurrentEnergy,
    DateTime UpdatedUtc);
