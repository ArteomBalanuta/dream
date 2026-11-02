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
    DateTime UpdatedUtc,
    // The spawn camp the enemy belongs to (camp_key); null for a row from before camps.
    string? CampKey = null);
