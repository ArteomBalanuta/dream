namespace Dream.Simulation.Inventory;

public readonly record struct ToolStatModifier(
    int StrengthBonus = 0,
    float AttackSpeedBonus = 0f,
    int VitalityBonus = 0);
