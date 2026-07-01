namespace Dream.Simulation.Players;

public readonly record struct PlayerCombatDerivedStats(
    int Strength,
    float AttackSpeed,
    int Vitality,
    int MinDamage,
    int MaxDamage,
    int CriticalDamage,
    int MaxHealth,
    float CriticalChance,
    float AttackIntervalSeconds);
