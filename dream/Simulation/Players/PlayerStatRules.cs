using Dream.Simulation.Stats;

namespace Dream.Simulation.Players;

// Stat points. Points are derived, never stored: the budget is what the level earns, and what is
// spent is how far the base stats sit above their starting values. The server validates a spend
// and the client panel reads the same rule, so both sides agree on what is available.
// Strength, Agility, Vitality and Energy take points. Attack speed is no longer bought directly (it
// comes from Agility); Attack Speed steps a character bought earlier still count as spent.
// The values the four attributes derive in code live here too (the formula keys for Strength and
// Vitality stay in the database).
public static class PlayerStatRules
{
    public const int PointsPerLevel = 5;

    // The values a new character starts with (the PlayerCombatStats defaults). Spending raises a
    // stat above these; a stat below its start has no refund and counts as nothing spent.
    //
    // These are the ORIGIN OF THE POINT COUNT, not what a new character is handed. A new character
    // is created at 1000 in every attribute (PersistedPlayerProfile.CreateDefault and the
    // player_profiles column defaults), which is each attribute's ceiling; it therefore reads as
    // far over budget and gets 0 points available, which PointsAvailable already handles without
    // going negative. Keeping the origin here at the old modest values is what leaves the point
    // arithmetic meaningful for a character that sits below the ceiling.
    // What a NEW character is created with, in every attribute: the ceiling of each
    // (PlayerCombatStats.MaxStrength and friends). PersistedPlayerProfile.CreateDefault writes it,
    // and the player_profiles column defaults match. Deliberately separate from the Start* values
    // below, which are only the origin the point count measures from -- a character created at the
    // ceiling reads as far over budget and gets 0 points available, which PointsAvailable already
    // handles without going negative. Characters stored before this keep the stats they have;
    // this is the value for new ones, not a migration.
    public const int NewCharacterAttribute = 1000;

    public const int StartStrength = 10;
    public const float StartAttackSpeed = 2.0f;
    public const int StartVitality = 10;
    // The point origin for Agility and Energy, likewise.
    public const int StartAgility = 10;
    public const int StartEnergy = 10;

    // One Attack Speed point bought this much (legacy spends only).
    public const float AttackSpeedPerPoint = 0.05f;

    // What the attributes derive.
    public const float AttackSpeedPerAgility = 0.01f;
    public const int AgilityPerDefense = 4;
    public const int ManaPerEnergy = 5;

    public static float EffectiveAttackSpeed(float attackSpeed, int agility)
        => Math.Clamp(attackSpeed + AttackSpeedPerAgility * Math.Max(0, agility), PlayerCombatStats.MinAttackSpeed, PlayerCombatStats.MaxAttackSpeed);

    public static int Defense(int agility) => Math.Max(0, agility) / AgilityPerDefense;

    public static int MagicMinDamage(int energy) => Math.Max(0, energy);

    public static int MagicMaxDamage(int energy) => 2 * Math.Max(0, energy);

    // Level 1 has no points; each level after it earns PointsPerLevel.
    public static int PointsEarned(int level)
    {
        return level <= 1 ? 0 : (int)Math.Min((long)(level - 1) * PointsPerLevel, int.MaxValue);
    }

    // Points already put into the stats. Never negative.
    public static int PointsSpent(PlayerCombatStats stats)
    {
        return PointsSpent(stats.Strength, stats.AttackSpeed, stats.Vitality, stats.Agility, stats.Energy);
    }

    public static int PointsSpent(int strength, float attackSpeed, int vitality, int agility = StartAgility, int energy = StartEnergy)
    {
        long spent = (long)Math.Max(0, strength - StartStrength)
            + Math.Max(0, vitality - StartVitality)
            + Math.Max(0, agility - StartAgility)
            + Math.Max(0, energy - StartEnergy)
            + AttackSpeedSteps(attackSpeed);
        return (int)Math.Min(spent, int.MaxValue);
    }

    // Unspent points. A character already over budget (the old free sliders) keeps its stats and gets
    // 0 here, never a negative number.
    public static int PointsAvailable(int level, in PlayerCombatStats stats)
    {
        long free = (long)PointsEarned(level) - PointsSpent(stats);
        return free <= 0 ? 0 : (int)free;
    }

    // The same rule for the client, which holds the base stats as plain numbers.
    public static int PointsAvailable(int level, int strength, float attackSpeed, int vitality, int agility = StartAgility, int energy = StartEnergy)
    {
        long free = (long)PointsEarned(level) - PointsSpent(strength, attackSpeed, vitality, agility, energy);
        return free <= 0 ? 0 : (int)free;
    }

    public static bool IsSpendable(StatId stat) => stat is StatId.Strength or StatId.Agility or StatId.Vitality or StatId.Energy;

    // Checks a spend against the level and the current base stats. On success newStats holds the
    // result and the caller applies it. Nothing is clamped: a spend that would pass a stat's cap is
    // refused, so no point is lost.
    public static bool TryValidateSpend(
        int level,
        PlayerCombatStatsSnapshot current,
        StatId stat,
        int points,
        out PlayerCombatStatsSnapshot newStats,
        out string? reason)
    {
        newStats = current;
        if (!IsSpendable(stat))
        {
            reason = $"{stat} cannot take stat points.";
            return false;
        }

        if (points <= 0)
        {
            reason = "Points to spend must be positive.";
            return false;
        }

        int available = PointsAvailable(level, current.Strength, current.AttackSpeed, current.Vitality, current.Agility, current.Energy);
        if (points > available)
        {
            reason = $"Spending {points} points exceeds the {available} available.";
            return false;
        }

        switch (stat)
        {
            case StatId.Strength:
                if (current.Strength + (long)points > PlayerCombatStats.MaxStrength)
                {
                    reason = $"Strength cannot pass {PlayerCombatStats.MaxStrength}.";
                    return false;
                }

                newStats = current with { Strength = current.Strength + points };
                break;
            case StatId.Vitality:
                if (current.Vitality + (long)points > PlayerCombatStats.MaxVitality)
                {
                    reason = $"Vitality cannot pass {PlayerCombatStats.MaxVitality}.";
                    return false;
                }

                newStats = current with { Vitality = current.Vitality + points };
                break;
            case StatId.Agility:
                if (current.Agility + (long)points > PlayerCombatStats.MaxAgility)
                {
                    reason = $"Agility cannot pass {PlayerCombatStats.MaxAgility}.";
                    return false;
                }

                newStats = current with { Agility = current.Agility + points };
                break;
            default:
                if (current.Energy + (long)points > PlayerCombatStats.MaxEnergy)
                {
                    reason = $"Energy cannot pass {PlayerCombatStats.MaxEnergy}.";
                    return false;
                }

                newStats = current with { Energy = current.Energy + points };
                break;
        }

        reason = null;
        return true;
    }

    private static long AttackSpeedSteps(float attackSpeed)
    {
        if (!float.IsFinite(attackSpeed) || attackSpeed <= StartAttackSpeed)
        {
            return 0;
        }

        // Float noise (2.1499999 for 2.15) must not drop a step, so round to the nearest.
        return (long)MathF.Round((attackSpeed - StartAttackSpeed) / AttackSpeedPerPoint);
    }
}
