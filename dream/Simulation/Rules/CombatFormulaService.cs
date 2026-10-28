using Dream.Simulation.Persistence;
using Dream.Simulation.Players;

namespace Dream.Simulation.Rules;

public sealed class CombatFormulaService
{
    private readonly CombatFormulaSet _formulaSet;

    private CombatFormulaService(CombatFormulaSet formulaSet)
    {
        _formulaSet = formulaSet;
    }

    public static CombatFormulaService Default { get; } = CreateForDefinitions(CreateDefaultDefinitions());

    public static CombatFormulaService Create(ICombatFormulaRepository repository)
    {
        ArgumentNullException.ThrowIfNull(repository);
        return new CombatFormulaService(new CombatFormulaSet(repository.LoadAllEnabled()));
    }

    public static CombatFormulaService CreateForDefinitions(IEnumerable<CombatFormulaDefinition> definitions)
    {
        return new CombatFormulaService(new CombatFormulaSet(definitions));
    }

    public PlayerCombatDerivedStats EvaluatePlayerDerivedStats(PlayerCombatStats stats, int level)
    {
        ArgumentNullException.ThrowIfNull(stats);

        Dictionary<string, double> variables = new(StringComparer.OrdinalIgnoreCase)
        {
            ["strength"] = stats.Strength,
            ["attack_speed"] = stats.AttackSpeed,
            ["vitality"] = stats.Vitality,
            ["level"] = level
        };

        int minDamage = EvaluateInt(CombatFormulaKeys.PlayerMinDamage, variables);
        variables["min_damage"] = minDamage;

        int maxDamage = EvaluateInt(CombatFormulaKeys.PlayerMaxDamage, variables);
        variables["max_damage"] = maxDamage;

        int maxHealth = EvaluateInt(CombatFormulaKeys.PlayerMaxHealth, variables);
        variables["max_health"] = maxHealth;

        int criticalDamage = EvaluateInt(CombatFormulaKeys.PlayerCriticalDamage, variables);
        float criticalChance = EvaluateFloat(CombatFormulaKeys.PlayerCriticalChance, variables);
        float attackIntervalSeconds = EvaluateFloat(CombatFormulaKeys.PlayerAttackIntervalSeconds, variables);

        if (attackIntervalSeconds <= 0f)
        {
            throw new InvalidOperationException("Combat formula attack interval must be greater than zero.");
        }

        return new PlayerCombatDerivedStats(
            stats.Strength,
            stats.AttackSpeed,
            stats.Vitality,
            minDamage,
            maxDamage,
            criticalDamage,
            maxHealth,
            criticalChance,
            attackIntervalSeconds);
    }

    public int EvaluateExperienceToNextLevel(int level, int experience)
    {
        Dictionary<string, double> variables = new(StringComparer.OrdinalIgnoreCase)
        {
            ["level"] = level,
            ["experience"] = experience
        };

        int required = EvaluateInt(CombatFormulaKeys.PlayerExperienceToNextLevel, variables);
        if (required <= 0)
        {
            throw new InvalidOperationException("Experience-to-next-level formula must resolve to a positive integer.");
        }

        return required;
    }

    public int EvaluateEnemyExperienceReward(int enemyLevel, int playerLevel)
    {
        Dictionary<string, double> variables = new(StringComparer.OrdinalIgnoreCase)
        {
            ["enemy_level"] = enemyLevel,
            ["player_level"] = playerLevel
        };

        return Math.Max(0, EvaluateInt(CombatFormulaKeys.EnemyExperienceReward, variables));
    }

    private int EvaluateInt(string formulaKey, IReadOnlyDictionary<string, double> variables)
    {
        double value = _formulaSet.Evaluate(formulaKey, variables);
        return (int)Math.Round(value, MidpointRounding.AwayFromZero);
    }

    private float EvaluateFloat(string formulaKey, IReadOnlyDictionary<string, double> variables)
    {
        double value = _formulaSet.Evaluate(formulaKey, variables);
        return (float)value;
    }

    private static IEnumerable<CombatFormulaDefinition> CreateDefaultDefinitions()
    {
        DateTime now = DateTime.UtcNow;
        return
        [
            new CombatFormulaDefinition(CombatFormulaKeys.PlayerMinDamage, "5 + strength * 2", "Player minimum melee damage.", true, now),
            new CombatFormulaDefinition(CombatFormulaKeys.PlayerMaxDamage, "10 + strength * 4", "Player maximum melee damage.", true, now),
            new CombatFormulaDefinition(CombatFormulaKeys.PlayerMaxHealth, "50 + vitality * 5", "Player maximum health.", true, now),
            new CombatFormulaDefinition(CombatFormulaKeys.PlayerCriticalDamage, "max_damage * 1.5", "Critical hit damage.", true, now),
            new CombatFormulaDefinition(CombatFormulaKeys.PlayerCriticalChance, "0.15", "Critical hit chance.", true, now),
            new CombatFormulaDefinition(CombatFormulaKeys.PlayerAttackIntervalSeconds, "1.0 / attack_speed", "Authoritative attack interval.", true, now),
            new CombatFormulaDefinition(CombatFormulaKeys.PlayerExperienceToNextLevel, "1000 + level * 23", "Experience required to reach the next level.", true, now),
            new CombatFormulaDefinition(CombatFormulaKeys.EnemyExperienceReward, "enemy_level * 14", "Experience granted for killing an enemy.", true, now)
        ];
    }
}
