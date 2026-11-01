using Microsoft.Data.Sqlite;
using Xunit;
using Dream.Server;
using Dream.Server.Persistence;
using Dream.Simulation.Content;
using Dream.Simulation.Persistence;
using Dream.Simulation.Rules;
using Dream.Tests.Server.Persistence.Migrations;

namespace Dream.Tests.Server;

// Wave I1, security rule 14: a bad enemy or armor formula stops the start with exit code 8 (naming the
// formula), before the world opens. It never first fails on a spawn or hit tick.
public sealed class StartupFormulaProbeTests
{
    private static IReadOnlyList<CombatFormulaDefinition> WithExpression(string key, string expression)
    {
        using MigrationTestDatabase database = new();
        database.Schema.EnsureSchema();
        return [.. new SqliteCombatFormulaRepository(database.Schema).LoadAllEnabled()
            .Select(definition => string.Equals(definition.FormulaKey, key, StringComparison.OrdinalIgnoreCase)
                ? definition with { ExpressionText = expression }
                : definition)];
    }

    [Fact]
    public void TheShippedFormulas_PassTheProbe_ForEveryShippedTemplate()
    {
        ServerBootstrap.ProbeFormulas(CombatFormulaService.Default, ContentCatalog.Shipped);
    }

    [Theory]
    [InlineData("enemy.attack_damage", "strength / (vitality - 10)", "enemy.attack_damage")]
    [InlineData("enemy.max_health", "50 / (vitality - 10)", "enemy.max_health")]
    [InlineData("enemy.attack_interval_seconds", "1 / (strength - 10)", "enemy.attack_interval_seconds")]
    [InlineData("player.damage_taken_factor", "100 / (armor - 500)", "player.damage_taken_factor")]
    public void AFormulaThatDividesByZero_FailsTheProbe_NamingTheFormula(string key, string expression, string expectedInMessage)
    {
        CombatFormulaService service = CombatFormulaService.CreateForDefinitions(WithExpression(key, expression));

        ContentValidationException ex = Assert.Throws<ContentValidationException>(
            () => ServerBootstrap.ProbeFormulas(service, ContentCatalog.Shipped));

        Assert.Contains(expectedInMessage, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AProductionStart_WithABadStoredFormula_StopsWithExitCode8_BeforeAnyTick()
    {
        using MigrationTestDatabase db = new();
        using (ServerBootstrap.CreatePersistenceBundle(db.DatabasePath, MigrationTestDatabase.InitScriptPath))
        {
        }

        SqliteConnection.ClearAllPools();
        db.ExecuteScript("UPDATE combat_formulas SET expression_text = '100 / (armor - 500)' WHERE formula_key = 'player.damage_taken_factor';");

        List<string> log = [];
        using DedicatedServerHost? host = ServerBootstrap.TryCreateServerModeHost(
            () =>
            {
                using ServerPersistenceBundle bundle = ServerBootstrap.CreatePersistenceBundle(db.DatabasePath, MigrationTestDatabase.InitScriptPath);
                throw new Xunit.Sdk.XunitException("the start went on past a bad formula");
            },
            log.Add,
            out int exitCode);

        Assert.Null(host);
        Assert.Equal(ServerBootstrap.ContentInvalidExitCode, exitCode);
        Assert.Contains(log, line => line.Contains("player.damage_taken_factor", StringComparison.Ordinal));
    }
}
