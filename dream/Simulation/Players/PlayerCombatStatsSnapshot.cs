namespace Dream.Simulation.Players;

public readonly record struct PlayerCombatStatsSnapshot(
    int Strength,
    float AttackSpeed,
    int Vitality)
{
    public static PlayerCombatStatsSnapshot From(PlayerCombatStats stats)
    {
        ArgumentNullException.ThrowIfNull(stats);
        return new PlayerCombatStatsSnapshot(
            stats.Strength,
            stats.AttackSpeed,
            stats.Vitality);
    }

    public PlayerCombatStats ToCombatStats()
    {
        return new PlayerCombatStats
        {
            Strength = Strength,
            AttackSpeed = AttackSpeed,
            Vitality = Vitality
        };
    }
}
