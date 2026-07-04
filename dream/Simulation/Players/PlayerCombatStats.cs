using Dream.Simulation.Inventory;
using Dream.Server.Rules;

namespace Dream.Simulation.Players;

public sealed class PlayerCombatStats
{
    public const int MinStrength = 1;
    public const int MaxStrength = 50;
    public const float MinAttackSpeed = 0.5f;
    public const float MaxAttackSpeed = 5.0f;
    public const int MinVitality = 1;
    public const int MaxVitality = 50;

    private int _strength = 10;
    private float _attackSpeed = 2.0f;
    private int _vitality = 10;

    public int Strength
    {
        get => _strength;
        set => _strength = Math.Clamp(value, MinStrength, MaxStrength);
    }

    public float AttackSpeed
    {
        get => _attackSpeed;
        set => _attackSpeed = Math.Clamp(value, MinAttackSpeed, MaxAttackSpeed);
    }

    public int Vitality
    {
        get => _vitality;
        set => _vitality = Math.Clamp(value, MinVitality, MaxVitality);
    }

    public PlayerCombatDerivedStats Derive(CombatFormulaService? formulaService = null, int level = 1)
    {
        return (formulaService ?? CombatFormulaService.Default)
            .EvaluatePlayerDerivedStats(this, level);
    }

    public static PlayerCombatStats Combine(PlayerCombatStats baseStats, ToolStatModifier modifier)
    {
        ArgumentNullException.ThrowIfNull(baseStats);
        return new PlayerCombatStats
        {
            Strength = baseStats.Strength + modifier.StrengthBonus,
            AttackSpeed = baseStats.AttackSpeed + modifier.AttackSpeedBonus,
            Vitality = baseStats.Vitality + modifier.VitalityBonus
        };
    }
}
