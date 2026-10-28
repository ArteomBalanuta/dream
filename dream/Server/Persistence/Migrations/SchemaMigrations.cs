using System.Globalization;
using System.Text.RegularExpressions;

namespace Dream.Server.Persistence.Migrations;

public static partial class SchemaMigrations
{
    // The migrations in directory, ordered by version and validated: every file matches
    // ^(\d{4})_([a-z0-9_]+)\.sql$, versions start at 1 and are contiguous, and a gap or a duplicate
    // throws.
    public static IReadOnlyList<ISchemaMigration> LoadFromDirectory(string directory)
    {
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException($"The schema migrations directory '{directory}' does not exist.");
        }

        SortedDictionary<int, ISchemaMigration> byVersion = [];
        foreach (string path in Directory.EnumerateFiles(directory, "*.sql"))
        {
            string fileName = Path.GetFileName(path);
            Match match = FileNamePattern().Match(fileName);
            if (!match.Success)
            {
                throw new InvalidDataException(
                    $"Schema migration '{fileName}' does not match NNNN_snake_case.sql ({FileNamePattern()}).");
            }

            int version = int.Parse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture);
            string name = match.Groups[2].Value;
            string script = File.ReadAllText(path);
            ValidateScript(fileName, script);
            ISchemaMigration migration = version == BaselineMigration.BaselineVersion
                ? new BaselineMigration(name, script)
                : new SqlScriptMigration(version, name, script);
            if (!byVersion.TryAdd(version, migration))
            {
                throw new InvalidDataException(
                    $"Schema migration version {version:D4} is defined twice ('{byVersion[version].Name}' and '{name}').");
            }
        }

        List<ISchemaMigration> migrations = [.. byVersion.Values];
        Validate(migrations);
        return migrations;
    }

    // Versions start at 1 and are contiguous, in order.
    internal static void Validate(IReadOnlyList<ISchemaMigration> migrations)
    {
        if (migrations.Count == 0)
        {
            throw new InvalidDataException("No schema migrations were found; at least 0001 (the baseline) is required.");
        }

        for (int i = 0; i < migrations.Count; i++)
        {
            int expected = i + 1;
            if (migrations[i].Version != expected)
            {
                throw new InvalidDataException(
                    $"Schema migration versions must start at 1 and be contiguous: expected {expected:D4}, found {migrations[i].Version:D4} ('{migrations[i].Name}').");
            }
        }
    }

    // The migrator owns the transaction (all pending migrations commit together) and the journal
    // mode, so a script must not control either. A trigger's BEGIN ... END is fine: only a BEGIN that
    // ends its own statement is transaction control.
    private static void ValidateScript(string fileName, string script)
    {
        Match forbidden = ForbiddenStatementPattern().Match(script);
        if (forbidden.Success)
        {
            throw new InvalidDataException(
                $"Schema migration '{fileName}' contains '{forbidden.Value.Trim()}'; migration scripts must not control transactions or the journal mode.");
        }
    }

    [GeneratedRegex(@"^(\d{4})_([a-z0-9_]+)\.sql$", RegexOptions.CultureInvariant)]
    private static partial Regex FileNamePattern();

    [GeneratedRegex(
        @"^\s*(BEGIN(\s+(DEFERRED|IMMEDIATE|EXCLUSIVE))?(\s+TRANSACTION)?\s*;|COMMIT\b|END\s+TRANSACTION\b|ROLLBACK\b|PRAGMA\s+journal_mode\b)",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex ForbiddenStatementPattern();
}
