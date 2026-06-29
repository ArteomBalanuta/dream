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

    public PlayerCombatDerivedStats Derive()
    {
        int minDamage = 5 + (Strength * 2);
        int maxDamage = 10 + (Strength * 4);
        int maxHealth = 50 + (Vitality * 5);
        int criticalDamage = (int)MathF.Round(maxDamage * 1.5f);

        return new PlayerCombatDerivedStats(
            Strength,
            AttackSpeed,
            Vitality,
            minDamage,
            maxDamage,
            criticalDamage,
            maxHealth,
            0.15f,
            1.0f / AttackSpeed);
    }
}
