using System.Globalization;
using Dream.Server.Hosting;
using Dream.Server.Persistence;
using Dream.Server.Persistence.Migrations;
using Dream.Simulation.Characters;
using Microsoft.Data.Sqlite;

namespace Dream.Server.Characters;

// The `--characters` command line: list | release <name> | rename <old> <new>. Output is tab-separated.
// `list` and `release` run while a server runs and take no lease; `rename` is offline and needs every
// world's lease, because a running server's cached profile would write the old display_name back. A
// token hash is never read into the output: `list` prints account_id only.
public static class CharacterCli
{
    public const int ExitOk = 0;
    public const int ExitUsage = 1;
    public const int ExitNotFoundOrConflict = 2;
    public const int ExitSchemaTooNew = 3;
    public const int ExitWorldHeld = 7;

    public const string Usage = "Usage: --characters list | release <name> | rename <old name> <new name>";

    private const string FindByDisplayNameSql = """
        SELECT player_name, display_name, account_id FROM player_profiles
        WHERE display_name = $name COLLATE NOCASE;
        """;

    private const string FindByKeySql = """
        SELECT player_name, display_name, account_id FROM player_profiles WHERE player_name = $name;
        """;

    private const string ListSql = """
        SELECT p.display_name, p.player_name, p.level, p.account_id, a.last_seen_utc
        FROM player_profiles AS p
        LEFT JOIN accounts AS a ON a.account_id = p.account_id
        ORDER BY p.display_name COLLATE NOCASE, p.player_name;
        """;

    // Runs against the default database (migrating it first). args are the arguments after --characters.
    // The maintenance lock is held until the command ends, as WorldCli does.
    public static int Run(string[] args)
    {
        DatabaseMaintenanceLock? maintenance = null;
        try
        {
            return Run(
                args,
                () =>
                {
                    SqliteSchemaBootstrapper schema = new(ServerBootstrap.DefaultDatabasePath, ServerBootstrap.DefaultSchemaScriptPath);
                    maintenance = DatabaseMaintenanceLock.EnsureSchemaGuarded(schema, ServerBootstrap.DefaultDatabasePath, log: null);
                    return schema;
                },
                Console.Out,
                Console.Error,
                HoldEveryWorld);
        }
        finally
        {
            maintenance?.Dispose();
        }
    }

    // holdEveryWorld returns the leases of every world, or null when one is held; null (the default)
    // takes none (a test seam).
    public static int Run(
        string[] args,
        Func<SqliteSchemaBootstrapper> openSchema,
        TextWriter output,
        TextWriter error,
        Func<IDisposable?>? holdEveryWorld = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(openSchema);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        string? usageError = Parse(args, out string verb, out string[] rest);
        if (usageError != null)
        {
            error.WriteLine(usageError);
            error.WriteLine(Usage);
            return ExitUsage;
        }

        if (verb == "rename")
        {
            string newName = CharacterNames.Normalize(rest[1]);
            if (!CharacterNames.IsValidNewName(newName))
            {
                error.WriteLine(JoinRejectionReasons.NameInvalid);
                error.WriteLine(Usage);
                return ExitUsage;
            }
        }

        try
        {
            SqliteSchemaBootstrapper schema = openSchema();
            return verb switch
            {
                "list" => List(schema, output),
                "release" => Release(schema, rest[0], output, error),
                _ => Rename(schema, rest[0], rest[1], output, error, holdEveryWorld),
            };
        }
        catch (SchemaTooNewException ex)
        {
            error.WriteLine(
                $"The database schema is at version {ex.StoredVersion}, newer than the version " +
                $"{ex.LatestVersion} this build knows. Run a newer build.");
            return ExitSchemaTooNew;
        }
        catch (WorldHeldException ex)
        {
            error.WriteLine($"{ex.Message} Stop the server hosting it first.");
            return ExitWorldHeld;
        }
        catch (DatabaseBusyException ex)
        {
            error.WriteLine($"{ex.Message} Try again when it has finished.");
            return ExitWorldHeld;
        }
        catch (LockFileUnavailableException ex)
        {
            error.WriteLine($"Cannot create the lock file {ex.LockPath}: {ex.InnerException?.Message ?? ex.Message}");
            return ExitNotFoundOrConflict;
        }
        catch (SqliteException ex)
        {
            error.WriteLine($"The database could not be used: {ex.Message}. If the file is damaged, restore a backup (--db restore <path> --yes).");
            return ExitNotFoundOrConflict;
        }
    }

    private static string? Parse(string[] args, out string verb, out string[] rest)
    {
        verb = string.Empty;
        rest = [];
        if (args.Length == 0)
        {
            return "Missing --characters command.";
        }

        verb = args[0].ToLowerInvariant();
        rest = args[1..];
        switch (verb)
        {
            case "list":
                return rest.Length == 0 ? null : "'list' takes no arguments.";
            case "release":
                return rest.Length == 1 && !string.IsNullOrWhiteSpace(rest[0]) ? null : "'release' needs exactly one character name.";
            case "rename":
                return rest.Length == 2 && !string.IsNullOrWhiteSpace(rest[0]) ? null : "'rename' needs the current name and a new name.";
            default:
                return $"Unknown --characters command '{Sanitize(args[0])}'.";
        }
    }

    private static int List(SqliteSchemaBootstrapper schema, TextWriter output)
    {
        using SqliteConnection connection = schema.CreateOpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = ListSql;
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            output.WriteLine(string.Join(
                '\t',
                Sanitize(reader.GetString(0)),
                Sanitize(reader.GetString(1)),
                reader.GetInt64(2).ToString(CultureInfo.InvariantCulture),
                reader.IsDBNull(3) ? "-" : reader.GetInt64(3).ToString(CultureInfo.InvariantCulture),
                reader.IsDBNull(4) ? "-" : Sanitize(reader.GetString(4))));
        }

        return ExitOk;
    }

    private static int Release(SqliteSchemaBootstrapper schema, string name, TextWriter output, TextWriter error)
    {
        using SqliteConnection connection = schema.CreateOpenConnection();
        Row? found = Find(connection, name.Trim());
        if (found is not { } row)
        {
            error.WriteLine($"No character matches '{Sanitize(name)}'.");
            return ExitNotFoundOrConflict;
        }

        if (row.AccountId is null)
        {
            output.WriteLine($"already-unowned\t{Sanitize(row.Key)}\t{Sanitize(row.DisplayName)}");
            return ExitOk;
        }

        using SqliteCommand update = connection.CreateCommand();
        update.CommandText = "UPDATE player_profiles SET account_id = NULL WHERE player_name = $key;";
        update.Parameters.AddWithValue("$key", row.Key);
        update.ExecuteNonQuery();
        output.WriteLine($"released\t{Sanitize(row.Key)}\t{Sanitize(row.DisplayName)}");
        return ExitOk;
    }

    private static int Rename(
        SqliteSchemaBootstrapper schema,
        string oldName,
        string newNameText,
        TextWriter output,
        TextWriter error,
        Func<IDisposable?>? holdEveryWorld)
    {
        string newName = CharacterNames.Normalize(newNameText);
        using IDisposable? leases = holdEveryWorld?.Invoke();
        if (holdEveryWorld != null && leases == null)
        {
            error.WriteLine("A world is in use by a running server or another command; stop it first. Nothing was changed.");
            return ExitWorldHeld;
        }

        using SqliteConnection connection = schema.CreateOpenConnection();
        Row? found = Find(connection, oldName.Trim());
        if (found is not { } row)
        {
            error.WriteLine($"No character matches '{Sanitize(oldName)}'.");
            return ExitNotFoundOrConflict;
        }

        // The new name must be free: no other row with that display name (ignoring ASCII case), and no
        // other row whose retired key it equals (player_name never changes; a save would overwrite it).
        using (SqliteCommand conflict = connection.CreateCommand())
        {
            conflict.CommandText = """
                SELECT EXISTS(
                    SELECT 1 FROM player_profiles
                    WHERE player_name <> $key AND (display_name = $new COLLATE NOCASE OR player_name = $new));
                """;
            conflict.Parameters.AddWithValue("$key", row.Key);
            conflict.Parameters.AddWithValue("$new", newName);
            if (Convert.ToInt64(conflict.ExecuteScalar(), CultureInfo.InvariantCulture) != 0)
            {
                error.WriteLine($"The name '{Sanitize(newName)}' is already taken.");
                return ExitNotFoundOrConflict;
            }
        }

        using SqliteCommand update = connection.CreateCommand();
        update.CommandText = "UPDATE player_profiles SET display_name = $new WHERE player_name = $key;";
        update.Parameters.AddWithValue("$new", newName);
        update.Parameters.AddWithValue("$key", row.Key);
        update.ExecuteNonQuery();
        output.WriteLine($"renamed\t{Sanitize(row.Key)}\t{Sanitize(row.DisplayName)}\t{Sanitize(newName)}");
        return ExitOk;
    }

    // `<name>` matches display_name ignoring ASCII case, then the exact key.
    private static Row? Find(SqliteConnection connection, string name)
    {
        foreach (string sql in new[] { FindByDisplayNameSql, FindByKeySql })
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("$name", name);
            using SqliteDataReader reader = command.ExecuteReader();
            if (reader.Read())
            {
                return new Row(reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetInt64(2));
            }
        }

        return null;
    }

    // Control characters (and tabs and line breaks) print as '?' so a name cannot forge columns or lines.
    internal static string Sanitize(string text)
    {
        if (!text.Any(char.IsControl))
        {
            return text;
        }

        return string.Create(text.Length, text, static (span, source) =>
        {
            for (int i = 0; i < span.Length; i++)
            {
                span[i] = char.IsControl(source[i]) ? '?' : source[i];
            }
        });
    }

    private static IDisposable? HoldEveryWorld()
    {
        List<WorldHostLease> leases = [];
        if (DatabaseMaintenanceLock.TryHoldEveryWorld(ServerBootstrap.DefaultDatabasePath, leases, out _, out _))
        {
            return new LeaseSet(leases);
        }

        foreach (WorldHostLease lease in leases)
        {
            lease.Dispose();
        }

        return null;
    }

    private readonly record struct Row(string Key, string DisplayName, long? AccountId);

    private sealed class LeaseSet(List<WorldHostLease> leases) : IDisposable
    {
        public void Dispose()
        {
            foreach (WorldHostLease lease in leases)
            {
                lease.Dispose();
            }
        }
    }
}
