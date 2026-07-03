using Dream.Simulation.Players;

namespace Dream.Server.Persistence;

public readonly record struct PersistedPlayerProfile(
    string PlayerName,
    string DisplayName,
    PlayerInteractionMode InteractionMode,
    PersistedCreatureStats Stats,
    DateTime CreatedUtc,
    DateTime UpdatedUtc);
