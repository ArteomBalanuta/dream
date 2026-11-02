using Dream.Simulation.Stats;

namespace Dream.Simulation.Players;

// The attribute point economy and the values the four attributes derive in code (the formula keys
// for Strength and Vitality stay in the database).
public static class PlayerAttributeRules
{
    public const int PointsPerLevel = 5;
    public const float AttackSpeedPerAgility = 0.01f;
    public const int AgilityPerDefense = 4;
    public const int BaseMana = 20;
    public const int ManaPerEnergy = 5;
    public const float ManaRegenPerSecond = 1f;

    public static int PointsForLevelsGained(int levelsGained) => Math.Max(0, levelsGained) * PointsPerLevel;

    // The points a character of this level has earned in total (the migration backfill).
    public static int TotalPointsForLevel(int level) => PointsForLevelsGained(level - 1);

    public static float EffectiveAttackSpeed(float attackSpeed, int agility)
        => Math.Clamp(attackSpeed + AttackSpeedPerAgility * Math.Max(0, agility), PlayerCombatStats.MinAttackSpeed, PlayerCombatStats.MaxAttackSpeed);

    public static int Defense(int agility) => Math.Max(0, agility) / AgilityPerDefense;

    public static int MaxMana(int energy) => BaseMana + ManaPerEnergy * Math.Max(0, energy);

    public static int MagicMinDamage(int energy) => Math.Max(0, energy);

    public static int MagicMaxDamage(int energy) => 2 * Math.Max(0, energy);

    public static bool IsSpendable(StatId stat)
        => stat is StatId.Strength or StatId.Agility or StatId.Vitality or StatId.Energy;
}
