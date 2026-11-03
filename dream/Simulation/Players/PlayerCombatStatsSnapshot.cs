namespace Dream.Simulation.Players;

// Wire group order (protocol 19): [strength, attackSpeed, vitality, agility, energy]. Stat points are
// derived (PlayerStatRules) and mana travels in ServerPlayerVitals, so neither is here.
public readonly record struct PlayerCombatStatsSnapshot(
    int Strength,
    float AttackSpeed,
    int Vitality,
    int Agility = 0,
    int Energy = 0)
{
    public static PlayerCombatStatsSnapshot From(PlayerCombatStats stats)
    {
        ArgumentNullException.ThrowIfNull(stats);
        return new PlayerCombatStatsSnapshot(
            stats.Strength,
            stats.AttackSpeed,
            stats.Vitality,
            stats.Agility,
            stats.Energy);
    }

    public PlayerCombatStats ToCombatStats()
    {
        return new PlayerCombatStats
        {
            Strength = Strength,
            AttackSpeed = AttackSpeed,
            Vitality = Vitality,
            Agility = Agility,
            Energy = Energy
        };
    }
}
