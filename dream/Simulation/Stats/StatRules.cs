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
        StatId.Strength => 1f,
        StatId.Vitality => 1f,
        StatId.AttackSpeed => 0.5f,
        _ => 0f
    };

    public static float MaxBase(StatId stat) => stat switch
    {
        StatId.Strength => 50f,
        StatId.Vitality => 50f,
        StatId.AttackSpeed => 5.0f,
        _ => DefaultMaxUnused
    };

    public static float MinEffective(StatId stat) => MinBase(stat);

    public static float MaxEffective(StatId stat) => MaxBase(stat);

    public static float ClampBase(StatId stat, float value) => Math.Clamp(value, MinBase(stat), MaxBase(stat));

    public static float ClampEffective(StatId stat, float value) => Math.Clamp(value, MinEffective(stat), MaxEffective(stat));
}
