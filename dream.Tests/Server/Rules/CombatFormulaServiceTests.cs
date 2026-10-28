using Dream.Server.Persistence;
using Dream.Simulation.Persistence;
using Dream.Simulation.Rules;
using Dream.Simulation.Players;
using Xunit;

namespace Dream.Tests.Server.Rules;

public sealed class CombatFormulaServiceTests
{
    [Fact]
    public void EvaluatePlayerDerivedStats_UsesFormulaPrecedenceAndIntermediateValues()
    {
        CombatFormulaService service = CreateService(
            ("player.min_damage", "5 + strength * 2"),
            ("player.max_damage", "10 + strength * 4"),
            ("player.max_health", "50 + vitality * 5"),
            ("player.critical_damage", "max_damage * 1.5"),
            ("player.critical_chance", "0.15"),
            ("player.attack_interval_seconds", "1.0 / attack_speed"),
            ("player.experience_to_next_level", "1000 + level * 23"),
            ("enemy.experience_reward", "enemy_level * 14"));

        PlayerCombatDerivedStats derived = service.EvaluatePlayerDerivedStats(
            new PlayerCombatStats { Strength = 10, AttackSpeed = 2f, Vitality = 10 },
            level: 1);

        Assert.Equal(25, derived.MinDamage);
        Assert.Equal(50, derived.MaxDamage);
        Assert.Equal(75, derived.CriticalDamage);
        Assert.Equal(100, derived.MaxHealth);
        Assert.Equal(0.15f, derived.CriticalChance, 3);
        Assert.Equal(0.5f, derived.AttackIntervalSeconds, 3);
    }

    [Fact]
    public void EvaluatePlayerDerivedStats_SupportsParenthesesInConfiguredFormulaStrings()
    {
        CombatFormulaService service = CreateService(
            ("player.min_damage", "(5 + strength) * 2"),
            ("player.max_damage", "(10 + strength) * 4"),
            ("player.max_health", "50 + vitality * 5"),
            ("player.critical_damage", "max_damage * 1.5"),
            ("player.critical_chance", "0.15"),
            ("player.attack_interval_seconds", "1.0 / attack_speed"),
            ("player.experience_to_next_level", "1000 + level * 23"),
            ("enemy.experience_reward", "enemy_level * 14"));

        PlayerCombatDerivedStats derived = service.EvaluatePlayerDerivedStats(
            new PlayerCombatStats { Strength = 10, AttackSpeed = 2f, Vitality = 10 },
            level: 1);

        Assert.Equal(30, derived.MinDamage);
        Assert.Equal(80, derived.MaxDamage);
    }

    [Fact]
    public void Create_RejectsUnknownVariables()
    {
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => CreateService(
            ("player.min_damage", "5 + stamina * 2"),
            ("player.max_damage", "10 + strength * 4"),
            ("player.max_health", "50 + vitality * 5"),
            ("player.critical_damage", "max_damage * 1.5"),
            ("player.critical_chance", "0.15"),
            ("player.attack_interval_seconds", "1.0 / attack_speed"),
            ("player.experience_to_next_level", "1000 + level * 23"),
            ("enemy.experience_reward", "enemy_level * 14")));

        Assert.Contains("stamina", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EvaluatePlayerDerivedStats_UsesUpdatedAttackIntervalFormulaWithoutCodeChanges()
    {
        CombatFormulaService service = CreateService(
            ("player.min_damage", "5 + strength * 2"),
            ("player.max_damage", "10 + strength * 4"),
            ("player.max_health", "50 + vitality * 5"),
            ("player.critical_damage", "max_damage * 1.5"),
            ("player.critical_chance", "0.15"),
            ("player.attack_interval_seconds", "2.0 / attack_speed"),
            ("player.experience_to_next_level", "1000 + level * 23"),
            ("enemy.experience_reward", "enemy_level * 14"));

        PlayerCombatDerivedStats derived = service.EvaluatePlayerDerivedStats(
            new PlayerCombatStats { Strength = 10, AttackSpeed = 2f, Vitality = 10 },
            level: 1);

        Assert.Equal(1.0f, derived.AttackIntervalSeconds, 3);
    }

    [Fact]
    public void EvaluateExperienceToNextLevel_UsesDbBackedProgressionFormula()
    {
        CombatFormulaService service = CreateService(
            ("player.min_damage", "5 + strength * 2"),
            ("player.max_damage", "10 + strength * 4"),
            ("player.max_health", "50 + vitality * 5"),
            ("player.critical_damage", "max_damage * 1.5"),
            ("player.critical_chance", "0.15"),
            ("player.attack_interval_seconds", "1.0 / attack_speed"),
            ("player.experience_to_next_level", "1000 + level * 23"),
            ("enemy.experience_reward", "enemy_level * 14"));

        int required = service.EvaluateExperienceToNextLevel(level: 1, experience: 14);

        Assert.Equal(1023, required);
    }

    [Fact]
    public void EvaluateEnemyExperienceReward_UsesDbBackedRewardFormula()
    {
        CombatFormulaService service = CreateService(
            ("player.min_damage", "5 + strength * 2"),
            ("player.max_damage", "10 + strength * 4"),
            ("player.max_health", "50 + vitality * 5"),
            ("player.critical_damage", "max_damage * 1.5"),
            ("player.critical_chance", "0.15"),
            ("player.attack_interval_seconds", "1.0 / attack_speed"),
            ("player.experience_to_next_level", "1000 + level * 23"),
            ("enemy.experience_reward", "enemy_level * 14"));

        int reward = service.EvaluateEnemyExperienceReward(enemyLevel: 1, playerLevel: 1);

        Assert.Equal(14, reward);
    }

    private static CombatFormulaService CreateService(params (string Key, string Expression)[] formulas)
    {
        InMemoryCombatFormulaRepository repository = new(formulas.Select(formula =>
            new CombatFormulaDefinition(
                formula.Key,
                formula.Expression,
                $"{formula.Key} test formula",
                Enabled: true,
                UpdatedUtc: new DateTime(2026, 7, 4, 0, 0, 0, DateTimeKind.Utc))));
        return CombatFormulaService.Create(repository);
    }

    private sealed class InMemoryCombatFormulaRepository : ICombatFormulaRepository
    {
        private readonly Dictionary<string, CombatFormulaDefinition> _definitions;

        public InMemoryCombatFormulaRepository(IEnumerable<CombatFormulaDefinition> definitions)
        {
            _definitions = definitions.ToDictionary(definition => definition.FormulaKey, StringComparer.OrdinalIgnoreCase);
        }

        public IReadOnlyList<CombatFormulaDefinition> LoadAllEnabled()
        {
            return _definitions.Values.Where(definition => definition.Enabled).ToArray();
        }

        public CombatFormulaDefinition? TryGet(string formulaKey)
        {
            return _definitions.TryGetValue(formulaKey, out CombatFormulaDefinition definition)
                ? definition
                : null;
        }

        public void Upsert(CombatFormulaDefinition definition)
        {
            _definitions[definition.FormulaKey] = definition;
        }
    }
}
