using Dream.Simulation.Rules;

namespace Dream.Simulation.Players;

public sealed class PlayerCombatStats
{
    // The attribute ceiling sits an order of magnitude above what a new character is created with
    // (PlayerStatRules.NewCharacterAttribute, 1000), and that gap is the point of it. While the
    // default and the ceiling were the same number, every flat Strength, Agility, Vitality and
    // Energy modifier on a piece of gear clamped away to nothing and no point could be spent: the
    // whole itemisation layer still computed and then moved no number at all. Keep this well above
    // the default, or that returns.
    public const int AttributeCeiling = 10000;

    public const int MinStrength = 1;
    public const int MaxStrength = AttributeCeiling;
    public const float MinAttackSpeed = 0.5f;
    public const float MaxAttackSpeed = 5.0f;
    public const int MinVitality = 1;
    public const int MaxVitality = AttributeCeiling;
    public const int MinAgility = 0;
    public const int MaxAgility = AttributeCeiling;
    public const int MinEnergy = 0;
    public const int MaxEnergy = AttributeCeiling;

    // The unseeded sheet, which is NOT what a new character gets. A player's starting stats are
    // PlayerStatRules.Start* (1000 each), written by PersistedPlayerProfile.CreateDefault and the
    // player_profiles column defaults; the server applies that profile to the combatant on join.
    // These values are only what a sheet holds before any profile or equipment reaches it, so they
    // stay at the old modest baseline: a transient combatant should not arrive at the stat ceiling,
    // and the combat tests measure damage, armor and health against these numbers.
    public const int UnseededAttribute = 10;

    private int _strength = UnseededAttribute;
    private float _attackSpeed = 2.0f;
    private int _vitality = UnseededAttribute;
    private int _agility = UnseededAttribute;
    private int _energy = UnseededAttribute;

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
