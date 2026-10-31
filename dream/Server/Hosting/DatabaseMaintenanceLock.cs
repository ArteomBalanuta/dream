using System.Globalization;
using Dream.Server.Persistence;
using Dream.Server.Persistence.Migrations;
using Dream.Simulation.World;
using Microsoft.Data.Sqlite;

namespace Dream.Server.Hosting;

// "{db}.maintenance.lock": taken exclusively by --db restore for the whole restore and by a start
// (or an offline --worlds command) around the schema migration, the world resolve and the world's
// lease acquisition, so a world cannot be created or opened in the gap between restore's lease
// check and its file swap, and two processes never migrate at once. A start holds it briefly; the
// operating system drops it when the holder dies. A database that is not a file takes no lock.
public sealed class DatabaseMaintenanceLock : IDisposable
{
    // How long a start waits for a restore or another start's migration to finish.
    public static readonly TimeSpan StartWait = TimeSpan.FromSeconds(20);

    private static readonly TimeSpan RetryInterval = TimeSpan.FromMilliseconds(25);

    private FileStream? _stream;

    private DatabaseMaintenanceLock(FileStream stream, string lockPath)
    {
        _stream = stream;
        LockPath = lockPath;
    }

    public string LockPath { get; }

    public static string GetLockPath(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        return databasePath + ".maintenance.lock";
    }

    // The lock, or null when another process still holds it after wait.
    public static DatabaseMaintenanceLock? TryAcquire(string databasePath, TimeSpan wait)
    {
        string lockPath = GetLockPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(lockPath)) ?? ".");
        DateTime deadline = DateTime.UtcNow + wait;
        while (true)
        {
            FileStream? stream = WorldHostLease.TryOpenExclusive(lockPath);
            if (stream != null)
            {
                return new DatabaseMaintenanceLock(stream, lockPath);
            }

            if (DateTime.UtcNow >= deadline)
            {
                return null;
            }

            Thread.Sleep(RetryInterval);
        }
    }

    // The lock; throws DatabaseBusyException when it stays held for wait.
    public static DatabaseMaintenanceLock Acquire(string databasePath, TimeSpan wait)
    {
        return TryAcquire(databasePath, wait) ?? throw new DatabaseBusyException(GetLockPath(databasePath));
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _stream, null)?.Dispose();
    }

    // Migrates the database under the maintenance lock. When migrations are pending, every world's
    // lease is held for the migration too, so a server that holds a world never has its database
    // migrated under it (WorldHeldException when one does). A database that is not a file migrates
    // without either. The lock is returned still held (null when none applies) so the caller can keep
    // it through its world resolve and lease acquisition; dispose it afterwards.
    public static DatabaseMaintenanceLock? EnsureSchemaGuarded(
        SqliteSchemaBootstrapper schema,
        string databasePath,
        Action<string>? log)
    {
        ArgumentNullException.ThrowIfNull(schema);
        if (!WorldHostLease.AppliesTo(databasePath))
        {
            schema.EnsureSchema(log);
            return null;
        }

        DatabaseMaintenanceLock gate = Acquire(databasePath, StartWait);
        List<WorldHostLease> leases = [];
        try
        {
            if (File.Exists(databasePath) && schema.HasPendingMigrations()
                && !TryHoldEveryWorld(databasePath, leases, out WorldId held, out string? heldPath))
            {
                throw new WorldHeldException(held, heldPath!);
            }

            schema.EnsureSchema(log);
            return gate;
        }
        catch
        {
            gate.Dispose();
            throw;
        }
        finally
        {
            foreach (WorldHostLease lease in leases)
            {
                lease.Dispose();
            }
        }
    }

    // Takes the lease of every world the database lists plus every existing world lock file, so a
    // world that a server holds (or is about to open) stops the caller. False, with the held world,
    // when one is held; the leases taken so far stay in leases for the caller to dispose.
    public static bool TryHoldEveryWorld(
        string databasePath,
        List<WorldHostLease> leases,
        out WorldId heldWorld,
        out string? heldPath)
    {
        heldWorld = default;
        heldPath = null;
        HashSet<long> ids = [];
        if (File.Exists(databasePath))
        {
            try
            {
                using SqliteConnection connection = new(new SqliteConnectionStringBuilder
                {
                    DataSource = databasePath,
                    Pooling = false,
                    DefaultTimeout = 5
                }.ToString());
                connection.Open();
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
            WorldId world = new(id);
            WorldHostLease? lease = WorldHostLease.TryAcquire(databasePath, world);
            if (lease == null)
            {
                heldWorld = world;
                heldPath = WorldHostLease.GetLockPath(databasePath, world);
                return false;
            }

            leases.Add(lease);
        }

        return true;
    }
}
