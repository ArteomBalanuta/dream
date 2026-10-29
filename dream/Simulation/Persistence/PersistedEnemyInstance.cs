using Dream.Simulation.World;
using OpenTK.Mathematics;

namespace Dream.Simulation.Persistence;

public readonly record struct PersistedEnemyInstance(
    ObjectId ObjectId,
    string EnemyKind,
    string DisplayName,
    Vector3 Position,
    Vector3 Facing,
    PersistedCreatureStats Stats,
    DateTime CreatedUtc,
    DateTime UpdatedUtc);
