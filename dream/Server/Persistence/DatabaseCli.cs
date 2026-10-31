using System.Globalization;
using Dream.Server.Hosting;
using Dream.Server.Persistence.Migrations;
using Dream.Simulation.World;
using Microsoft.Data.Sqlite;

namespace Dream.Server.Persistence;

// The `--db` command line: backup <path> | restore <path> --yes. The whole database file, never
// one world (profiles are global, so a per-world restore would orphan them).
//
// backup copies with SqliteConnection.BackupDatabase and is safe while a server runs: SQLite
// copies a consistent snapshot. restore is offline: it refuses while any world's lease is held,
// validates the source first (schema version not newer than this build, integrity_check ok),
// keeps the current file as "{db}.pre-restore.{timestamp}.bak", and swaps the file in.
public static class DatabaseCli
{
    public const int ExitOk = 0;
    public const int ExitUsage = 1;
    public const int ExitSourceOrTargetInvalid = 2;
    public const int ExitSchemaTooNew = 3;
    public const int ExitIntegrity = 6;
    public const int ExitWorldHeld = 7;

    public const string Usage = "Usage: --db backup <path> | restore <path> --yes";

    public static int Run(string[] args)
    {
        return Run(args, ServerBootstrap.DefaultDatabasePath, ServerBootstrap.DefaultSchemaScriptPath, Console.Out, Console.Error);
    }

    public static int Run(string[] args, string databasePath, string schemaScriptPath, TextWriter output, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        if (args.Length == 0)
        {
            return Usage1(error, "Missing --db command.");
        }

        string verb = args[0].ToLowerInvariant();
        string[] rest = args[1..];
        bool yes = rest.Contains("--yes", StringComparer.Ordinal);
        string[] positional = rest.Where(a => a != "--yes").ToArray();
        switch (verb)
        {
            case "backup":
                if (positional.Length != 1 || yes)
                {
                    return Usage1(error, "'backup' needs exactly one destination path.");
                }

                return Backup(databasePath, positional[0], output, error);

            case "restore":
                if (positional.Length != 1)
                {
                    return Usage1(error, "'restore' needs exactly one source path.");
                }

                if (!yes)
                {
                    return Usage1(error, "'restore' replaces the whole database; pass --yes to confirm.");
                }

                return Restore(databasePath, schemaScriptPath, positional[0], output, error);

            default:
                return Usage1(error, $"Unknown --db command '{args[0]}'.");
        }
    }

    private static int Usage1(TextWriter error, string message)
    {
        error.WriteLine(message);
        error.WriteLine(Usage);
        return ExitUsage;
    }

    public static int Backup(string databasePath, string destinationPath, TextWriter output, TextWriter error)
    {
        string destination = Path.GetFullPath(destinationPath);
        if (!File.Exists(databasePath))
        {
            error.WriteLine($"There is no database at {databasePath}.");
            return ExitSourceOrTargetInvalid;
        }

        if (File.Exists(destination) || Directory.Exists(destination))
        {
            error.WriteLine($"{destination} already exists; choose a new path.");
            return ExitSourceOrTargetInvalid;
        }

        string temporary = destination + ".partial";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination) ?? ".");
            DeleteIfExists(temporary);
            using (SqliteConnection source = Open(databasePath))
            using (SqliteConnection target = Open(temporary))
            {
                source.BackupDatabase(target);
            }

            string? problem = IntegrityProblem(temporary);
            if (problem != null)
            {
                DeleteIfExists(temporary);
                error.WriteLine($"The backup failed its integrity check: {problem}");
                return ExitIntegrity;
            }

            File.Move(temporary, destination);
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            DeleteIfExists(temporary);
            error.WriteLine($"The backup failed: {ex.Message}");
            return ExitSourceOrTargetInvalid;
        }

        output.WriteLine($"backup\t{destination}\t{new FileInfo(destination).Length.ToString(CultureInfo.InvariantCulture)} bytes");
        return ExitOk;
    }

    public static int Restore(
        string databasePath,
        string schemaScriptPath,
        string sourcePath,
        TextWriter output,
        TextWriter error)
    {
        string source = Path.GetFullPath(sourcePath);
        if (!File.Exists(source))
        {
            error.WriteLine($"There is no backup at {source}.");
            return ExitSourceOrTargetInvalid;
        }

        if (string.Equals(source, Path.GetFullPath(databasePath), StringComparison.OrdinalIgnoreCase))
        {
            error.WriteLine("The source is the live database itself.");
            return ExitSourceOrTargetInvalid;
        }

        List<WorldHostLease> leases = [];
        try
        {
            if (!TryHoldEveryWorld(databasePath, leases, out string? heldPath))
            {
                error.WriteLine(
                    $"Not restored: a server (or an offline command) holds a world of this database ({heldPath}). " +
                    "Stop it first.");
                return ExitWorldHeld;
            }

            int validation = Validate(source, databasePath, schemaScriptPath, error);
            if (validation != ExitOk)
            {
                return validation;
            }

            string stamp = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
            string safety = $"{databasePath}.pre-restore.{stamp}.bak";
            string staged = $"{databasePath}.restore.tmp";
            try
            {
                DeleteIfExists(staged);
                // The staged copy is a consistent snapshot of the source, WAL included.
                using (SqliteConnection from = Open(source))
                using (SqliteConnection to = Open(staged))
                {
                    from.BackupDatabase(to);
                }

                bool hadDatabase = File.Exists(databasePath);
                if (hadDatabase)
                {
                    DeleteIfExists(safety);
                    using SqliteConnection current = Open(databasePath);
                    using SqliteConnection copy = Open(safety);
                    current.BackupDatabase(copy);
                }

                DeleteIfExists(databasePath + "-wal");
                DeleteIfExists(databasePath + "-shm");
                File.Move(staged, databasePath, overwrite: true);
                output.WriteLine(
                    $"restored\t{source}" + (hadDatabase ? $"\tprevious database kept as {safety}" : string.Empty));
                return ExitOk;
            }
            catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
            {
                DeleteIfExists(staged);
                error.WriteLine($"The restore failed: {ex.Message}");
                return ExitSourceOrTargetInvalid;
            }
        }
        finally
        {
            foreach (WorldHostLease lease in leases)
            {
                lease.Dispose();
            }
        }
    }

    // Takes the lease of every world the database lists plus every existing lock file, so a world
    // that a server holds (or is about to open) stops the restore.
    private static bool TryHoldEveryWorld(string databasePath, List<WorldHostLease> leases, out string? heldPath)
    {
        heldPath = null;
        HashSet<long> ids = [];
        if (File.Exists(databasePath))
        {
            try
            {
                using SqliteConnection connection = Open(databasePath);
                using SqliteCommand command = connection.CreateCommand();
                command.CommandText = "SELECT world_id FROM worlds;";
                using SqliteDataReader reader = command.ExecuteReader();
                while (reader.Read())
                {
                    ids.Add(reader.GetInt64(0));
                }
            }
            catch (SqliteException)
            {
                // No worlds table (a v0 or damaged file): only existing lock files count.
            }
        }

        string directory = Path.GetDirectoryName(Path.GetFullPath(databasePath)) ?? ".";
        string prefix = Path.GetFileName(databasePath) + ".world-";
        if (Directory.Exists(directory))
        {
            foreach (string file in Directory.EnumerateFiles(directory, prefix + "*.lock"))
            {
                string name = Path.GetFileName(file);
                string number = name[prefix.Length..^".lock".Length];
                if (long.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out long id) && id >= 1)
                {
                    ids.Add(id);
                }
            }
        }

        foreach (long id in ids.Order())
        {
            WorldHostLease? lease = WorldHostLease.TryAcquire(databasePath, new WorldId(id));
            if (lease == null)
            {
                heldPath = WorldHostLease.GetLockPath(databasePath, new WorldId(id));
                return false;
            }

            leases.Add(lease);
        }

        return true;
    }

    // ExitOk, or the code for a source that cannot be restored (already reported on error).
    private static int Validate(string source, string databasePath, string schemaScriptPath, TextWriter error)
    {
        try
        {
            SqliteSchemaMigrator migrator = new(SchemaMigrations.LoadFromDirectory(
                new SqliteSchemaBootstrapper(databasePath, schemaScriptPath).MigrationsDirectory));
            using SqliteConnection connection = Open(source);
            int stored = migrator.GetCurrentVersion(connection);
            if (stored > migrator.LatestVersion)
            {
                error.WriteLine(
                    $"Not restored: the backup's schema is at version {stored}, newer than the version " +
                    $"{migrator.LatestVersion} this build knows.");
                return ExitSchemaTooNew;
            }

            string? problem = IntegrityProblem(connection);
            if (problem != null)
            {
                error.WriteLine($"Not restored: the backup failed its integrity check: {problem}");
                return ExitIntegrity;
            }

            return ExitOk;
        }
        catch (SqliteException ex)
        {
            error.WriteLine($"Not restored: {source} is not a readable database: {ex.Message}");
            return ExitSourceOrTargetInvalid;
        }
    }

    private static string? IntegrityProblem(string path)
    {
        using SqliteConnection connection = Open(path);
        return IntegrityProblem(connection);
    }

    private static string? IntegrityProblem(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check;";
        using SqliteDataReader reader = command.ExecuteReader();
        List<string> problems = [];
        while (reader.Read())
        {
            string line = reader.GetString(0);
            if (!string.Equals(line, "ok", StringComparison.OrdinalIgnoreCase) && problems.Count < 5)
            {
                problems.Add(line);
            }
        }

        return problems.Count == 0 ? null : string.Join("; ", problems);
    }

    // No pooling: the files must be free to move once the connection is disposed.
    private static SqliteConnection Open(string path)
    {
        SqliteConnection connection = new(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Pooling = false,
            DefaultTimeout = 5
        }.ToString());
        connection.Open();
        return connection;
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
