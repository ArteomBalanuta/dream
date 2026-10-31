using Dream.Simulation.Players;

namespace Dream.Simulation.Stats;

/// <summary>
/// Static limits per stat. Base and effective ranges are separate so raising the effective cap
/// (gear) never loosens what a player may pick for a base stat. Both equal today's values.
/// </summary>
public static class StatRules
{
    public const float DefaultMaxUnused = 1000f;

    public static float MinBase(StatId stat) => stat switch
    {
        StatId.Strength => PlayerCombatStats.MinStrength,
        StatId.Vitality => PlayerCombatStats.MinVitality,
        StatId.AttackSpeed => PlayerCombatStats.MinAttackSpeed,
        _ => 0f
    };

    public static float MaxBase(StatId stat) => stat switch
    {
        StatId.Strength => PlayerCombatStats.MaxStrength,
        StatId.Vitality => PlayerCombatStats.MaxVitality,
        StatId.AttackSpeed => PlayerCombatStats.MaxAttackSpeed,
        _ => DefaultMaxUnused
    };

    public static float MinEffective(StatId stat) => MinBase(stat);

    public static float MaxEffective(StatId stat) => MaxBase(stat);

    // Math.Clamp keeps a NaN, so it is mapped to the minimum.
    public static float ClampBase(StatId stat, float value) => float.IsNaN(value) ? MinBase(stat) : Math.Clamp(value, MinBase(stat), MaxBase(stat));

    public static float ClampEffective(StatId stat, float value) => float.IsNaN(value) ? MinEffective(stat) : Math.Clamp(value, MinEffective(stat), MaxEffective(stat));
}
