namespace Dream.Simulation.Persistence;

public readonly record struct PersistedCreatureStats(
    int CurrentHealth,
    int MaxHealth,
    int Strength,
    int Agility,
    int Vitality,
    int Energy,
    float MovementSpeed,
    float AttackSpeed,
    int Level,
    int Experience = 0);
