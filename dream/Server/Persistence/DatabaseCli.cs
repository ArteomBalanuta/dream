using System.Globalization;
using Dream.Server.Hosting;
using Dream.Server.Persistence.Migrations;
using Microsoft.Data.Sqlite;

namespace Dream.Server.Persistence;

// The `--db` command line: backup <path> | restore <path> --yes. The whole database file, never
// one world (profiles are global, so a per-world restore would orphan them).
//
// backup copies with SqliteConnection.BackupDatabase and is safe while a server runs: SQLite
// copies a consistent snapshot. restore is offline: it takes the database's maintenance lock (so
// no start can create or open a world meanwhile), refuses while any world's lease is held,
// validates the source first (a Dream database, schema version not newer than this build,
// integrity_check ok), keeps the current file as "{db}.pre-restore.{timestamp}.bak", and swaps the
// file in. The safety copy never depends on the health of the file being replaced: a damaged live
// file (the very reason to restore) is copied raw, with its -wal and -shm.
public static class DatabaseCli
{
    public const int ExitOk = 0;
    public const int ExitUsage = 1;
    public const int ExitSourceOrTargetInvalid = 2;
    public const int ExitSchemaTooNew = 3;
    public const int ExitIntegrity = 6;
    public const int ExitWorldHeld = 7;

    public const string Usage = "Usage: --db backup <path> | restore <path> --yes";

    private const int SqliteCorrupt = 11;
    private const int SqliteNotADatabase = 26;

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
                TryDelete(temporary);
                error.WriteLine($"The backup failed its integrity check: {problem}");
                return ExitIntegrity;
            }

            File.Move(temporary, destination);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is SqliteCorrupt or SqliteNotADatabase)
        {
            TryDelete(temporary);
            error.WriteLine($"The backup failed: the database is damaged ({ex.Message}). Restore an earlier backup.");
            return ExitIntegrity;
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            TryDelete(temporary);
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

        StringComparison pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (string.Equals(source, Path.GetFullPath(databasePath), pathComparison))
        {
            error.WriteLine("The source is the live database itself.");
            return ExitSourceOrTargetInvalid;
        }

        List<WorldHostLease> leases = [];
        DatabaseMaintenanceLock? maintenance = null;
        try
        {
            // The maintenance lock keeps every start from creating or opening a world between the
            // lease check below and the swap. A start holds it only briefly.
            maintenance = DatabaseMaintenanceLock.TryAcquire(databasePath, TimeSpan.FromSeconds(2));
            if (maintenance == null)
            {
                error.WriteLine(
                    "Not restored: another process is restoring or starting this database " +
                    $"(lock file {DatabaseMaintenanceLock.GetLockPath(databasePath)}). Try again.");
                return ExitWorldHeld;
            }

            if (!DatabaseMaintenanceLock.TryHoldEveryWorld(databasePath, leases, out _, out string? heldPath))
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
            for (int attempt = 1; File.Exists(safety); attempt++)
            {
                safety = $"{databasePath}.pre-restore.{stamp}-{attempt}.bak";
            }

            string staged = $"{databasePath}.restore.tmp";
            bool safetyWritten = false;
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
                    SafetyCopy(databasePath, safety);
                    safetyWritten = true;

                    // The swap needs the live file free; find out before the WAL is touched, so a
                    // locked file leaves the live database exactly as it was.
                    using (new FileStream(databasePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    {
                    }
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
                TryDelete(staged);
                if (!safetyWritten)
                {
                    DeleteSafetyCopy(safety);
                }

                error.WriteLine(
                    $"The restore failed and the live database was left as it was: {ex.Message}" +
                    (safetyWritten ? $" (a copy of it was kept as {safety})" : string.Empty));
                return ExitSourceOrTargetInvalid;
            }
        }
        catch (LockFileUnavailableException ex)
        {
            error.WriteLine($"Not restored: cannot create the lock file {ex.LockPath}: {ex.InnerException?.Message ?? ex.Message}");
            return ExitSourceOrTargetInvalid;
        }
        finally
        {
            foreach (WorldHostLease lease in leases)
            {
                lease.Dispose();
            }

            maintenance?.Dispose();
        }
    }

    // A copy of the live database that does not depend on its health: the SQLite backup API first
    // (a consistent snapshot), and when that fails (a damaged header is error 26, a truncated file
    // 11) a raw copy of the file with its -wal and -shm. When even that fails the partial copy is
    // removed and the exception goes to the caller, which aborts the restore.
    private static void SafetyCopy(string databasePath, string safety)
    {
        try
        {
            using SqliteConnection current = Open(databasePath);
            using SqliteConnection copy = Open(safety);
            current.BackupDatabase(copy);
            return;
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            DeleteSafetyCopy(safety);
        }

        try
        {
            File.Copy(databasePath, safety, overwrite: false);
            foreach (string suffix in new[] { "-wal", "-shm" })
            {
                if (File.Exists(databasePath + suffix))
                {
                    File.Copy(databasePath + suffix, safety + suffix, overwrite: false);
                }
            }
        }
        catch
        {
            DeleteSafetyCopy(safety);
            throw;
        }
    }

    private static void DeleteSafetyCopy(string safety)
    {
        TryDelete(safety);
        TryDelete(safety + "-wal");
        TryDelete(safety + "-shm");
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

            // A zero-byte or foreign SQLite file passes integrity_check and reads as version 0, and
            // would restore into an empty world. A real database has the world or the profile table
            // (a legacy v0 database, from before schema_version, has player_profiles).
            if (!HasDreamTable(connection))
            {
                error.WriteLine(
                    $"Not restored: {source} is not a Dream database (it has neither a worlds nor a player_profiles table).");
                return ExitSourceOrTargetInvalid;
            }

            string? problem = IntegrityProblem(connection);
            if (problem != null)
            {
                error.WriteLine($"Not restored: the backup failed its integrity check: {problem}");
                return ExitIntegrity;
            }

            return ExitOk;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == SqliteCorrupt)
        {
            error.WriteLine($"Not restored: the backup failed its integrity check: {ex.Message}");
            return ExitIntegrity;
        }
        catch (SqliteException ex)
        {
            error.WriteLine($"Not restored: {source} is not a readable database: {ex.Message}");
            return ExitSourceOrTargetInvalid;
        }
    }

    private static bool HasDreamTable(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name IN ('worlds', 'player_profiles');";
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
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

    // For cleanup inside a catch: a failure to delete must not replace the error being reported.
    private static void TryDelete(string path)
    {
        try
        {
            DeleteIfExists(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
