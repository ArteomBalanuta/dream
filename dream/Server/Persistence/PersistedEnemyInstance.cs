using OpenTK.Mathematics;

namespace Dream.Server.Persistence;

public readonly record struct PersistedEnemyInstance(
    int EnemyId,
    string EnemyKind,
    string DisplayName,
    Vector3 Position,
    Vector3 Facing,
    PersistedCreatureStats Stats,
    DateTime CreatedUtc,
    DateTime UpdatedUtc);
