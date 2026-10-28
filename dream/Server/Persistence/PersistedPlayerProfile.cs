using Dream.Simulation.Players;

namespace Dream.Server.Persistence;

public readonly record struct PersistedPlayerProfile(
    string PlayerName,
    string DisplayName,
    PlayerInteractionMode InteractionMode,
    PersistedCreatureStats Stats,
    DateTime CreatedUtc,
    DateTime UpdatedUtc)
{
    // A new player's profile: what SqlitePlayerProfileRepository.GetOrCreate stores for a missing
    // row, and what a join applies (and then saves) when its read-only prefetch found none.
    public static PersistedPlayerProfile CreateDefault(string playerName, DateTime nowUtc)
    {
        string name = playerName.Trim();
        return new PersistedPlayerProfile(
            name,
            name,
            PlayerInteractionMode.Building,
            new PersistedCreatureStats(
                CurrentHealth: 100,
                MaxHealth: 100,
                Strength: 10,
                Agility: 10,
                Vitality: 10,
                Energy: 10,
                MovementSpeed: 6f,
                AttackSpeed: 2f,
                Level: 1,
                Experience: 0),
            nowUtc,
            nowUtc);
    }
}
