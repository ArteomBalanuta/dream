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

    // ---- wave I: enemy stats and the armor factor ----

    [Fact]
    public void EvaluateEnemyDerivedStats_UsesTheEnemyFormulas_AndTheShippedWolfAndDireNumbers()
    {
        CombatFormulaService service = CombatFormulaService.Default;

        EnemyCombatDerivedStats wolf = service.EvaluateEnemyDerivedStats(10, 0.952381f, 10, level: 1, intervalOverride: null);
        EnemyCombatDerivedStats dire = service.EvaluateEnemyDerivedStats(16, 1.0f, 30, level: 3, intervalOverride: null);

        Assert.Equal(12, wolf.AttackDamage);
        Assert.Equal(100, wolf.MaxHealth);
        Assert.Equal(18, dire.AttackDamage);
        Assert.Equal(200, dire.MaxHealth);
        Assert.Equal(1.0f, dire.AttackIntervalSeconds);
    }

    [Fact]
    public void EvaluateEnemyDerivedStats_AnIntervalOverrideSkipsItsFormula_AndFloorsApply()
    {
        CombatFormulaService service = CreateService(
            ("player.min_damage", "5"), ("player.max_damage", "10"), ("player.max_health", "50"),
            ("player.critical_damage", "10"), ("player.critical_chance", "0.1"),
            ("player.attack_interval_seconds", "1"), ("player.experience_to_next_level", "1000"),
            ("enemy.experience_reward", "1"),
            ("enemy.attack_damage", "strength - 100"),
            ("enemy.max_health", "0 - vitality"),
            ("enemy.attack_interval_seconds", "0.001 * attack_speed"));

        EnemyCombatDerivedStats floored = service.EvaluateEnemyDerivedStats(10, 1f, 10, level: 1, intervalOverride: null);
        EnemyCombatDerivedStats overridden = service.EvaluateEnemyDerivedStats(10, 1f, 10, level: 1, intervalOverride: 1.05f);

        Assert.Equal(1, floored.AttackDamage);
        Assert.Equal(1, floored.MaxHealth);
        Assert.Equal(0.05f, floored.AttackIntervalSeconds);
        Assert.Equal(1.05f, overridden.AttackIntervalSeconds);
    }

    [Fact]
    public void EvaluateEnemyDerivedStats_TheWolfIntervalIsExactlyTodaysBits()
    {
        EnemyCombatDerivedStats wolf = CombatFormulaService.Default.EvaluateEnemyDerivedStats(10, 0.952381f, 10, level: 1, intervalOverride: null);

        Assert.Equal(0x3F866666u, BitConverter.SingleToUInt32Bits(wolf.AttackIntervalSeconds));
        // The stored attack speed 0.952381 is today's 1f / 1.05f, bit for bit.
        Assert.Equal(BitConverter.SingleToUInt32Bits(1f / 1.05f), BitConverter.SingleToUInt32Bits(0.952381f));
        Assert.Equal(BitConverter.SingleToUInt32Bits(1.05f), BitConverter.SingleToUInt32Bits(wolf.AttackIntervalSeconds));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(int.MinValue)]
    public void EvaluateDamageTakenFactor_ArmorZeroOrLess_IsExactlyOne(int armor)
    {
        Assert.Equal(1.0, CombatFormulaService.Default.EvaluateDamageTakenFactor(armor));
    }

    [Theory]
    [InlineData(1, 100.0 / 101.0)]
    [InlineData(25, 0.8)]
    [InlineData(100, 0.5)]
    [InlineData(1000, 100.0 / 1100.0)]
    public void EvaluateDamageTakenFactor_FollowsTheFormula(int armor, double expected)
    {
        Assert.Equal(expected, CombatFormulaService.Default.EvaluateDamageTakenFactor(armor), 12);
    }

    [Fact]
    public void EvaluateDamageTakenFactor_IsClampedToZeroToOne()
    {
        CombatFormulaService high = CreateService(
            ("player.min_damage", "5"), ("player.max_damage", "10"), ("player.max_health", "50"),
            ("player.critical_damage", "10"), ("player.critical_chance", "0.1"),
            ("player.attack_interval_seconds", "1"), ("player.experience_to_next_level", "1000"),
            ("enemy.experience_reward", "1"),
            ("player.damage_taken_factor", "2 + armor"));
        CombatFormulaService negative = CreateService(
            ("player.min_damage", "5"), ("player.max_damage", "10"), ("player.max_health", "50"),
            ("player.critical_damage", "10"), ("player.critical_chance", "0.1"),
            ("player.attack_interval_seconds", "1"), ("player.experience_to_next_level", "1000"),
            ("enemy.experience_reward", "1"),
            ("player.damage_taken_factor", "0 - armor"));

        Assert.Equal(1.0, high.EvaluateDamageTakenFactor(3));
        Assert.Equal(0.0, negative.EvaluateDamageTakenFactor(3));
    }

    [Fact]
    public void Create_RejectsAnArmorFormulaThatUsesAnotherVariable()
    {
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => CreateService(
            ("player.min_damage", "5"), ("player.max_damage", "10"), ("player.max_health", "50"),
            ("player.critical_damage", "10"), ("player.critical_chance", "0.1"),
            ("player.attack_interval_seconds", "1"), ("player.experience_to_next_level", "1000"),
            ("enemy.experience_reward", "1"),
            ("player.damage_taken_factor", "100 / (100 + strength)")));

        Assert.Contains("strength", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Create_RejectsAMissingEnemyFormula()
    {
        // A list with only the original eight keys no longer starts: the new four are required.
        InMemoryCombatFormulaRepository repository = new(
        [
            .. new[]
            {
                ("player.min_damage", "5"), ("player.max_damage", "10"), ("player.max_health", "50"),
                ("player.critical_damage", "10"), ("player.critical_chance", "0.1"),
                ("player.attack_interval_seconds", "1"), ("player.experience_to_next_level", "1000"),
                ("enemy.experience_reward", "1")
            }.Select(pair => new CombatFormulaDefinition(pair.Item1, pair.Item2, "test", true, DateTime.UtcNow))
        ]);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => CombatFormulaService.Create(repository));

        Assert.Contains("enemy.attack_damage", exception.Message, StringComparison.Ordinal);
        Assert.Contains("player.damage_taken_factor", exception.Message, StringComparison.Ordinal);
    }

    private static CombatFormulaService CreateService(params (string Key, string Expression)[] formulas)
    {
        // Wave I's four required keys are appended unless the test supplies its own.
        IEnumerable<(string Key, string Expression)> withWaveI = formulas.Concat(
            Dream.Tests.Support.WaveIFormulas.Pairs.Where(pair =>
                !formulas.Any(formula => string.Equals(formula.Key, pair.Key, StringComparison.OrdinalIgnoreCase))));
        InMemoryCombatFormulaRepository repository = new(withWaveI.Select(formula =>
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
