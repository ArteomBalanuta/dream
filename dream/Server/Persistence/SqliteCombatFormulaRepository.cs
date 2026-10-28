using Dream.Server.Persistence.Store;
using Dream.Simulation.Persistence;
using Microsoft.Data.Sqlite;

namespace Dream.Server.Persistence;

public sealed class SqliteCombatFormulaRepository
    : SqliteRepositoryBase, ICombatFormulaRepository, IDisposable
{
    private const string LoadAllEnabledSql = """
        SELECT
            formula_key,
            expression_text,
            description,
            enabled,
            updated_utc
        FROM combat_formulas
        WHERE enabled = 1
        ORDER BY formula_key;
        """;

    private const string TryGetSql = """
        SELECT
            formula_key,
            expression_text,
            description,
            enabled,
            updated_utc
        FROM combat_formulas
        WHERE formula_key = $formulaKey;
        """;

    private const string UpsertSql = """
        INSERT INTO combat_formulas (
            formula_key,
            expression_text,
            description,
            enabled,
            updated_utc
        )
        VALUES (
            $formulaKey,
            $expressionText,
            $description,
            $enabled,
            $updatedUtc
        )
        ON CONFLICT(formula_key) DO UPDATE SET
            expression_text = excluded.expression_text,
            description = excluded.description,
            enabled = excluded.enabled,
            updated_utc = excluded.updated_utc;
        """;

    public SqliteCombatFormulaRepository(SqliteSchemaBootstrapper schema)
        : base(new SqliteWorldStore(schema))
    {
    }

    public IReadOnlyList<CombatFormulaDefinition> LoadAllEnabled()
    {
        return QueryList(LoadAllEnabledSql, ReadDefinition);
    }

    public CombatFormulaDefinition? TryGet(string formulaKey)
    {
        return QuerySingle(
            TryGetSql,
            formulaKey,
            static (command, key) => command.BindText("$formulaKey", key.Trim()),
            ReadDefinition);
    }

    public void Upsert(CombatFormulaDefinition definition)
    {
        Execute(UpsertSql, definition, static (command, value) => BindDefinition(command, value));
    }

    public void Dispose()
    {
    }

    private static void BindDefinition(SqliteCommand command, CombatFormulaDefinition definition)
    {
        command.BindText("$formulaKey", definition.FormulaKey.Trim());
        command.BindText("$expressionText", definition.ExpressionText.Trim());
        command.BindText("$description", definition.Description.Trim());
        command.BindInt64("$enabled", definition.Enabled ? 1 : 0);
        command.BindUtc(
            "$updatedUtc",
            definition.UpdatedUtc == default ? DateTime.UtcNow : definition.UpdatedUtc);
    }

    private static CombatFormulaDefinition ReadDefinition(SqliteDataReader reader)
    {
        return new CombatFormulaDefinition(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetInt64(3) != 0,
            reader.GetUtc(4));
    }
}
