using Dream.Simulation.Persistence;

namespace Dream.Tests.Support;

// The four formulas wave I (migration 0009) added to the required set, as the shipped expressions.
// Tests that build their own formula list append these so CombatFormulaSet finds every required key.
internal static class WaveIFormulas
{
    public static readonly (string Key, string Expression)[] Pairs =
    [
        ("enemy.attack_damage", "strength + 2"),
        ("enemy.max_health", "50 + vitality * 5"),
        ("enemy.attack_interval_seconds", "1.0 / attack_speed"),
        ("player.damage_taken_factor", "100 / (100 + armor)")
    ];

    public static CombatFormulaDefinition[] Definitions() =>
        [.. Pairs.Select(pair => new CombatFormulaDefinition(pair.Key, pair.Expression, "test", true, DateTime.UtcNow))];

    // definitions plus the wave I four.
    public static CombatFormulaDefinition[] With(IEnumerable<CombatFormulaDefinition> definitions) =>
        [.. definitions, .. Definitions()];
}
