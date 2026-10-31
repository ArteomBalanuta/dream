using Dream.Simulation.Players;
using Dream.Simulation.Stats;

namespace Dream.Tests.Support;

// The three-stat tool modifier and PlayerCombatStats.Combine that wave H C0b retired from the
// product code. Kept for tests only: StatSheetTests compares the StatSheet-based resolution against
// it, bit for bit, over the full base range.
public readonly record struct LegacyToolStats(
    int StrengthBonus = 0,
    float AttackSpeedBonus = 0f,
    int VitalityBonus = 0)
{
    // The legacy view of a definition's modifiers: only Strength, Attack Speed and Vitality count.
    public static LegacyToolStats From(IReadOnlyList<StatModifier> modifiers)
    {
        int strength = 0;
        float attackSpeed = 0f;
        int vitality = 0;
        foreach (StatModifier modifier in modifiers)
        {
            switch (modifier.Stat)
            {
                case StatId.Strength:
                    strength = (int)modifier.Value;
                    break;
                case StatId.AttackSpeed:
                    attackSpeed = modifier.Value;
                    break;
                case StatId.Vitality:
                    vitality = (int)modifier.Value;
                    break;
            }
        }

        return new LegacyToolStats(strength, attackSpeed, vitality);
    }

    public static PlayerCombatStats Combine(PlayerCombatStats baseStats, LegacyToolStats modifier)
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
