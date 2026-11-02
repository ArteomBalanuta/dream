using Dream.Simulation.Stats;

namespace Dream.Simulation.Players;

// Stat points. Points are derived, never stored: the budget is what the level earns, and what is
// spent is how far the base stats sit above their starting values. The server validates a spend
// and the client panel reads the same rule, so both sides agree on what is available.
public static class PlayerStatRules
{
    public const int PointsPerLevel = 5;

    // The values a new character starts with (the PlayerCombatStats defaults). Spending raises a
    // stat above these; a stat below its start has no refund and counts as nothing spent.
    public const int StartStrength = 10;
    public const float StartAttackSpeed = 2.0f;
    public const int StartVitality = 10;

    // One Attack Speed point buys this much.
    public const float AttackSpeedPerPoint = 0.05f;

    // Level 1 has no points; each level after it earns PointsPerLevel.
    public static int PointsEarned(int level)
    {
        return level <= 1 ? 0 : (int)Math.Min((long)(level - 1) * PointsPerLevel, int.MaxValue);
    }

    // Points already put into the stats. Never negative.
    public static int PointsSpent(PlayerCombatStats stats)
    {
        return PointsSpent(stats.Strength, stats.AttackSpeed, stats.Vitality);
    }

    public static int PointsSpent(int strength, float attackSpeed, int vitality)
    {
        long spent = Math.Max(0, strength - StartStrength)
            + Math.Max(0, vitality - StartVitality)
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
    public static int PointsAvailable(int level, int strength, float attackSpeed, int vitality)
    {
        long free = (long)PointsEarned(level) - PointsSpent(strength, attackSpeed, vitality);
        return free <= 0 ? 0 : (int)free;
    }

    public static bool IsSpendable(StatId stat) => stat is StatId.Strength or StatId.Vitality or StatId.AttackSpeed;

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

        int available = PointsAvailable(level, current.Strength, current.AttackSpeed, current.Vitality);
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
            default:
                float speed = MathF.Round(current.AttackSpeed + (points * AttackSpeedPerPoint), 2);
                if (!float.IsFinite(speed) || speed > PlayerCombatStats.MaxAttackSpeed + 0.0001f)
                {
                    reason = $"Attack speed cannot pass {PlayerCombatStats.MaxAttackSpeed}.";
                    return false;
                }

                newStats = current with { AttackSpeed = Math.Min(speed, PlayerCombatStats.MaxAttackSpeed) };
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
