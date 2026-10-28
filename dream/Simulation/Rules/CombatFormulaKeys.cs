namespace Dream.Simulation.Rules;

public static class CombatFormulaKeys
{
    public const string PlayerMinDamage = "player.min_damage";
    public const string PlayerMaxDamage = "player.max_damage";
    public const string PlayerMaxHealth = "player.max_health";
    public const string PlayerCriticalDamage = "player.critical_damage";
    public const string PlayerCriticalChance = "player.critical_chance";
    public const string PlayerAttackIntervalSeconds = "player.attack_interval_seconds";
    public const string PlayerExperienceToNextLevel = "player.experience_to_next_level";
    public const string EnemyExperienceReward = "enemy.experience_reward";

    public static readonly string[] RequiredPlayerKeys =
    [
        PlayerMinDamage,
        PlayerMaxDamage,
        PlayerMaxHealth,
        PlayerCriticalDamage,
        PlayerCriticalChance,
        PlayerAttackIntervalSeconds,
        PlayerExperienceToNextLevel,
        EnemyExperienceReward
    ];
}
