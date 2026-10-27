using Dream.Simulation.Players;

namespace Dream.Server.Persistence;

// In-memory copy of the volatile authoritative state saved every checkpoint. It is captured on
// the tick thread without any database access and written later by an IPersistenceCheckpointWriter.
public sealed record PersistenceCheckpoint(
    IReadOnlyList<PersistedPlayerWorldState> PlayerWorldStates,
    IReadOnlyList<PlayerCheckpointVitals> PlayerVitals,
    IReadOnlyList<PersistedEnemyInstance> Enemies,
    IReadOnlyList<PersistedPlacedCube> PlacedCubes,
    IReadOnlyList<PersistedCubeFracturePiece> FracturePieces)
{
    public static PersistenceCheckpoint Empty { get; } = new([], [], [], [], []);
}

// Only the profile fields that change without an immediate save. Level, experience and base
// stats are saved when they change, so a late checkpoint write must not overwrite them.
public readonly record struct PlayerCheckpointVitals(
    string PlayerName,
    PlayerInteractionMode InteractionMode,
    int CurrentHealth,
    int MaxHealth);
