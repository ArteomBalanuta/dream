using Dream.Simulation.Players;

namespace Dream.Simulation.Persistence;

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
            PlayerInteractionModeExtensions.Default,
            // Every attribute starts at PlayerStatRules.NewCharacterAttribute, and the health seed
            // is what the player.max_health formula ("50 + vitality * 5") makes of that vitality;
            // the formula service recomputes it on load, so a seed out of step would only show as
            // a wrong bar for one frame.
            new PersistedCreatureStats(
                CurrentHealth: 50 + (PlayerStatRules.NewCharacterAttribute * 5),
                MaxHealth: 50 + (PlayerStatRules.NewCharacterAttribute * 5),
                Strength: PlayerStatRules.NewCharacterAttribute,
                Agility: PlayerStatRules.NewCharacterAttribute,
                Vitality: PlayerStatRules.NewCharacterAttribute,
                Energy: PlayerStatRules.NewCharacterAttribute,
                MovementSpeed: 6f,
                AttackSpeed: 2f,
                Level: 1,
                Experience: 0),
            nowUtc,
            nowUtc);
    }
}
