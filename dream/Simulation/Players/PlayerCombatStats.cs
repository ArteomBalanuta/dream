using Dream.Simulation.Rules;

namespace Dream.Simulation.Players;

public sealed class PlayerCombatStats
{
    public const int MinStrength = 1;
    public const int MaxStrength = 1000;
    public const float MinAttackSpeed = 0.5f;
    public const float MaxAttackSpeed = 5.0f;
    public const int MinVitality = 1;
    public const int MaxVitality = 1000;
    public const int MinAgility = 0;
    public const int MaxAgility = 1000;
    public const int MinEnergy = 0;
    public const int MaxEnergy = 1000;

    private int _strength = 10;
    private float _attackSpeed = 2.0f;
    private int _vitality = 10;
    private int _agility;
    private int _energy;

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

    public int Agility
    {
        get => _agility;
        set => _agility = Math.Clamp(value, MinAgility, MaxAgility);
    }

    public int Energy
    {
        get => _energy;
        set => _energy = Math.Clamp(value, MinEnergy, MaxEnergy);
    }

    // The rules the client stat editor enforces (the Strength / Attack Speed / Vitality sliders in
    // Render.cs and DesktopClientConfig.Normalize): each base stat is picked freely inside its
    // [Min, Max] range, inclusive, with no point budget and no level gate. The server applies the same
    // rules to a client's PlayerStatsUpdateIntent and rejects anything the editor cannot produce,
    // rather than silently clamping it: a non-finite attack speed (NaN slips through Math.Clamp) or a
    // value outside its range.
    public static bool TryValidateBaseStats(PlayerCombatStatsSnapshot stats, out string? reason)
    {
        if (stats.Strength is < MinStrength or > MaxStrength)
        {
            reason = $"Strength {stats.Strength} is outside [{MinStrength}, {MaxStrength}].";
            return false;
        }

        if (!float.IsFinite(stats.AttackSpeed))
        {
            reason = "Attack speed must be a finite number.";
            return false;
        }

        if (stats.AttackSpeed is < MinAttackSpeed or > MaxAttackSpeed)
        {
            reason = $"Attack speed {stats.AttackSpeed} is outside [{MinAttackSpeed}, {MaxAttackSpeed}].";
            return false;
        }

        if (stats.Vitality is < MinVitality or > MaxVitality)
        {
            reason = $"Vitality {stats.Vitality} is outside [{MinVitality}, {MaxVitality}].";
            return false;
        }

        reason = null;
        return true;
    }

    public PlayerCombatDerivedStats Derive(CombatFormulaService? formulaService = null, int level = 1)
    {
        return (formulaService ?? CombatFormulaService.Default)
            .EvaluatePlayerDerivedStats(this, level);
    }
}
