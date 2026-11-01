using Dream.Simulation.Persistence;
using Dream.Simulation.Rules.Expression;

namespace Dream.Simulation.Rules;

public sealed class CombatFormulaSet
{
    private static readonly string[] BaseContextVariables =
    [
        "strength",
        "attack_speed",
        "vitality",
        "level"
    ];

    private static readonly string[] ProgressionVariables =
    [
        "level",
        "experience"
    ];

    private static readonly string[] EnemyRewardVariables =
    [
        "enemy_level",
        "player_level"
    ];

    private static readonly string[] DamageTakenVariables =
    [
        "armor"
    ];

    private static readonly IReadOnlyDictionary<string, string[]> VariableSetsByFormulaKey =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            [CombatFormulaKeys.PlayerMinDamage] = BaseContextVariables,
            [CombatFormulaKeys.PlayerMaxDamage] = BaseContextVariables,
            [CombatFormulaKeys.PlayerMaxHealth] = BaseContextVariables,
            [CombatFormulaKeys.PlayerCriticalDamage] = BaseContextVariables.Concat(["min_damage", "max_damage", "max_health"]).ToArray(),
            [CombatFormulaKeys.PlayerCriticalChance] = BaseContextVariables.Concat(["min_damage", "max_damage", "max_health"]).ToArray(),
            [CombatFormulaKeys.PlayerAttackIntervalSeconds] = BaseContextVariables.Concat(["min_damage", "max_damage", "max_health"]).ToArray(),
            [CombatFormulaKeys.PlayerExperienceToNextLevel] = ProgressionVariables,
            [CombatFormulaKeys.EnemyExperienceReward] = EnemyRewardVariables,
            [CombatFormulaKeys.EnemyAttackDamage] = BaseContextVariables,
            [CombatFormulaKeys.EnemyMaxHealth] = BaseContextVariables,
            [CombatFormulaKeys.EnemyAttackIntervalSeconds] = BaseContextVariables,
            [CombatFormulaKeys.PlayerDamageTakenFactor] = DamageTakenVariables
        };

    private readonly Dictionary<string, Func<IReadOnlyDictionary<string, double>, double>> _compiledExpressions;

    public CombatFormulaSet(IEnumerable<CombatFormulaDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);

        Dictionary<string, CombatFormulaDefinition> byKey = definitions
            .Where(definition => definition.Enabled)
            .ToDictionary(definition => definition.FormulaKey, StringComparer.OrdinalIgnoreCase);

        string[] missingKeys = CombatFormulaKeys.RequiredPlayerKeys
            .Where(requiredKey => !byKey.ContainsKey(requiredKey))
            .ToArray();
        if (missingKeys.Length > 0)
        {
            throw new InvalidOperationException(
                $"Missing required combat formulas: {string.Join(", ", missingKeys)}.");
        }

        _compiledExpressions = new Dictionary<string, Func<IReadOnlyDictionary<string, double>, double>>(StringComparer.OrdinalIgnoreCase);
        foreach (string formulaKey in CombatFormulaKeys.RequiredPlayerKeys)
        {
            CombatFormulaDefinition definition = byKey[formulaKey];
            string[] allowedVariables = VariableSetsByFormulaKey[formulaKey];
            _compiledExpressions[formulaKey] = CombatExpressionCompiler.Compile(
                definition.ExpressionText,
                allowedVariables);
        }
    }

    public double Evaluate(string formulaKey, IReadOnlyDictionary<string, double> variables)
    {
        if (!_compiledExpressions.TryGetValue(formulaKey, out Func<IReadOnlyDictionary<string, double>, double>? evaluator))
        {
            throw new InvalidOperationException($"Missing compiled combat formula '{formulaKey}'.");
        }

        return evaluator(variables);
    }
}
