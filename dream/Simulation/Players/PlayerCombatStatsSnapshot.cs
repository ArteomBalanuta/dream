namespace Dream.Simulation.Players;

// Wire group order: [strength, attackSpeed, vitality, agility, energy, statPoints, mana, maxMana].
public readonly record struct PlayerCombatStatsSnapshot(
    int Strength,
    float AttackSpeed,
    int Vitality,
    int Agility = 0,
    int Energy = 0,
    int StatPoints = 0,
    int Mana = 0,
    int MaxMana = 0)
{
    public static PlayerCombatStatsSnapshot From(PlayerCombatStats stats)
    {
        ArgumentNullException.ThrowIfNull(stats);
        return new PlayerCombatStatsSnapshot(
            stats.Strength,
            stats.AttackSpeed,
            stats.Vitality,
            stats.Agility,
            stats.Energy,
            stats.StatPoints,
            stats.Mana,
            stats.MaxMana);
    }

    public PlayerCombatStats ToCombatStats()
    {
        return new PlayerCombatStats
        {
            Strength = Strength,
            AttackSpeed = AttackSpeed,
            Vitality = Vitality,
            Agility = Agility,
            Energy = Energy,
            StatPoints = StatPoints,
            Mana = Mana,
            MaxMana = MaxMana
        };
    }
}
