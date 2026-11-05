using Dream.Simulation.Players;

namespace Dream.Simulation.Stats;

/// <summary>
/// Static limits per stat. Base and effective ranges are separate so raising the effective cap
/// (gear) never loosens what a player may pick for a base stat. Both equal today's values.
/// </summary>
public static class StatRules
{
    // The fallback ceiling for a stat with no explicit limit: StatId.Armor, StatId.MovementSpeed and
    // any future stat that does not name its own. Strength, Vitality, Agility, Energy and AttackSpeed
    // have explicit cases below. Do not tie this to the attribute ceiling: that put the cap on *gear*
    // armour at 10000, i.e. damage_taken_factor = 100 / (100 + 10000) = 0.99% damage taken from gear alone.
    // This caps gear armour only. Mitigation divides by PlayerCombatant.MitigationArmor =
    // EffectiveArmor + Defense, and Defense (Agility / 4, so up to 2500 at MaxAgility 10000) is a
    // separate contribution that nothing clamps: the sum is unbounded by this constant. At the shipping
    // default (Agility 1000) a naked character already has Defense 250 (28.6% damage taken).
    public const float DefaultMaxUnused = 1000f;

    /// <summary>
    /// The base ceiling for an enemy's Strength and Vitality. Enemies do not share the player ceiling
    /// (<see cref="PlayerCombatStats.MaxStrength"/>, the attribute ceiling the character sheet spends
    /// toward): the shipped templates top out at Strength 40, and `enemy.attack_damage = strength + 2`
    /// turns the ceiling straight into the worst bite a corrupt or tampered row can deal. At 50 that is
    /// 52; at the player ceiling it would be 10002, a one-shot on anyone whatever their vitality.
    /// Keep this well under the player ceiling, and above the content maximum of 40.
    /// </summary>
    public const float MaxEnemyBase = 50f;

    public static float MinBase(StatId stat) => stat switch
    {
        StatId.Strength => PlayerCombatStats.MinStrength,
        StatId.Vitality => PlayerCombatStats.MinVitality,
        StatId.Agility => PlayerCombatStats.MinAgility,
        StatId.Energy => PlayerCombatStats.MinEnergy,
        StatId.AttackSpeed => PlayerCombatStats.MinAttackSpeed,
        _ => 0f
    };

    public static float MaxBase(StatId stat) => stat switch
    {
        StatId.Strength => PlayerCombatStats.MaxStrength,
        StatId.Vitality => PlayerCombatStats.MaxVitality,
        StatId.Agility => PlayerCombatStats.MaxAgility,
        StatId.Energy => PlayerCombatStats.MaxEnergy,
        StatId.AttackSpeed => PlayerCombatStats.MaxAttackSpeed,
        _ => DefaultMaxUnused
    };

    /// <summary>
    /// The enemy base ceiling per stat: Strength and Vitality are capped at <see cref="MaxEnemyBase"/>,
    /// every other stat keeps the shared limit.
    /// </summary>
    public static float MaxEnemyBaseFor(StatId stat) => stat switch
    {
        StatId.Strength or StatId.Vitality => MaxEnemyBase,
        _ => MaxBase(stat)
    };

    // The enemy counterpart of ClampBase. NaN maps to the minimum, as there.
    public static float ClampEnemyBase(StatId stat, float value)
        => float.IsNaN(value) ? MinBase(stat) : Math.Clamp(value, MinBase(stat), MaxEnemyBaseFor(stat));

    public static float MinEffective(StatId stat) => MinBase(stat);

    public static float MaxEffective(StatId stat) => MaxBase(stat);

    // Math.Clamp keeps a NaN, so it is mapped to the minimum.
    public static float ClampBase(StatId stat, float value) => float.IsNaN(value) ? MinBase(stat) : Math.Clamp(value, MinBase(stat), MaxBase(stat));

    public static float ClampEffective(StatId stat, float value) => float.IsNaN(value) ? MinEffective(stat) : Math.Clamp(value, MinEffective(stat), MaxEffective(stat));
}
