using Microsoft.Data.Sqlite;

namespace Dream.Server.Persistence;

public sealed class SqliteCombatFormulaRepository : ICombatFormulaRepository, IDisposable
{
    private readonly SqliteSchemaBootstrapper _schema;

    public SqliteCombatFormulaRepository(SqliteSchemaBootstrapper schema)
    {
        _schema = schema;
    }

    public IReadOnlyList<CombatFormulaDefinition> LoadAllEnabled()
    {
        using SqliteConnection connection = _schema.CreateOpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
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

        using SqliteDataReader reader = command.ExecuteReader();
        List<CombatFormulaDefinition> definitions = [];
        while (reader.Read())
        {
            definitions.Add(ReadDefinition(reader));
        }

        return definitions;
    }

    public CombatFormulaDefinition? TryGet(string formulaKey)
    {
        using SqliteConnection connection = _schema.CreateOpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                formula_key,
                expression_text,
                description,
                enabled,
                updated_utc
            FROM combat_formulas
            WHERE formula_key = $formulaKey;
            """;
        command.Parameters.AddWithValue("$formulaKey", formulaKey.Trim());

        using SqliteDataReader reader = command.ExecuteReader();
        return reader.Read() ? ReadDefinition(reader) : null;
    }

    public void Upsert(CombatFormulaDefinition definition)
    {
        using SqliteConnection connection = _schema.CreateOpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
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
        command.Parameters.AddWithValue("$formulaKey", definition.FormulaKey.Trim());
        command.Parameters.AddWithValue("$expressionText", definition.ExpressionText.Trim());
        command.Parameters.AddWithValue("$description", definition.Description.Trim());
        command.Parameters.AddWithValue("$enabled", definition.Enabled ? 1 : 0);
        command.Parameters.AddWithValue("$updatedUtc", SqliteSchemaBootstrapper.FormatUtc(
            definition.UpdatedUtc == default ? DateTime.UtcNow : definition.UpdatedUtc));
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
    }

    private static CombatFormulaDefinition ReadDefinition(SqliteDataReader reader)
    {
        return new CombatFormulaDefinition(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetInt64(3) != 0,
            SqliteSchemaBootstrapper.ParseUtc(reader.GetString(4)));
    }
}
