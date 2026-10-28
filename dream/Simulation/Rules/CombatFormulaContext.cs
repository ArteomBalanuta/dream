namespace Dream.Simulation.Rules;

public readonly record struct CombatFormulaContext(
    int Strength,
    float AttackSpeed,
    int Vitality,
    int Level);
