using Dream.Simulation.World;

namespace Dream.Server.Hosting;

// Thrown when the lock file of a world (or of the database) cannot be created or opened for a
// reason other than another holder: no permission, a read-only file, a missing directory.
public sealed class LockFileUnavailableException : InvalidOperationException
{
    public LockFileUnavailableException(string lockPath, Exception inner)
        : base($"Cannot create or open the lock file {lockPath}: {inner.Message}", inner)
    {
        LockPath = lockPath;
    }

    public string LockPath { get; }
}

// Thrown when another process holds the database's maintenance lock (a restore, or a migration in
// progress) for longer than a start is willing to wait.
public sealed class DatabaseBusyException : InvalidOperationException
{
    public DatabaseBusyException(string lockPath)
        : base($"The database is being restored or migrated by another process; lock file {lockPath}.")
    {
        LockPath = lockPath;
    }

    public string LockPath { get; }
}

// Thrown when a world is already hosted (or being edited by an offline command) by another holder.
public sealed class WorldHeldException : InvalidOperationException
{
    public WorldHeldException(WorldId worldId, string lockPath)
        : base($"World {worldId} is already in use by another process; lock file {lockPath}.")
    {
        WorldId = worldId;
        LockPath = lockPath;
    }

    public WorldId WorldId { get; }

    public string LockPath { get; }
}

// One holder per world on a database file: an exclusive, OS-locked file next to the database,
// "{db}.world-{id}.lock". The operating system drops the lock when the holder dies, so a crash
// frees the world and a stale file does no harm; the file is left in place on release (deleting
// it would race with a second opener). No schema is involved. A server takes its world's lease
// at start; the offline --worlds commands take it for the duration of the command.
public sealed class WorldHostLease : IDisposable
{
    private FileStream? _stream;

    private WorldHostLease(FileStream stream, WorldId worldId, string lockPath)
    {
        _stream = stream;
        WorldId = worldId;
        LockPath = lockPath;
    }

    public WorldId WorldId { get; }

    public string LockPath { get; }

    public static string GetLockPath(string databasePath, WorldId worldId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        return $"{databasePath}.world-{worldId}.lock";
    }

    // False for a database that is not a file (in-memory), which takes no lease.
    public static bool AppliesTo(string? databasePath)
    {
        return !string.IsNullOrWhiteSpace(databasePath)
            && !databasePath.Contains(":memory:", StringComparison.OrdinalIgnoreCase)
            && !databasePath.Contains("mode=memory", StringComparison.OrdinalIgnoreCase);
    }

    // The lease, or null when another holder has the world. Anything else that stops the lock file
    // from opening (a read-only file, a missing directory, a denied path) is not "held": it throws
    // LockFileUnavailableException, which the entry points report as a clear error.
    public static WorldHostLease? TryAcquire(string databasePath, WorldId worldId)
    {
        string lockPath = GetLockPath(databasePath, worldId);
        FileStream? stream = TryOpenExclusive(lockPath);
        return stream == null ? null : new WorldHostLease(stream, worldId, lockPath);
    }

    // The exclusively opened lock file, or null on a sharing violation (a live holder).
    internal static FileStream? TryOpenExclusive(string lockPath)
    {
        try
        {
            return new FileStream(
                lockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.None);
        }
        catch (IOException ex) when (IsSharingViolation(ex))
        {
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new LockFileUnavailableException(lockPath, ex);
        }
    }

    // Windows reports ERROR_SHARING_VIOLATION (32) or ERROR_LOCK_VIOLATION (33) in the HResult's low
    // word. On Unix .NET takes an flock and reports a refused one as an IOException whose HResult is
    // the raw errno EWOULDBLOCK (11 on Linux, 35 on macOS); not verifiable on this Windows machine.
    private static bool IsSharingViolation(IOException ex)
    {
        if (OperatingSystem.IsWindows())
        {
            return ((uint)ex.HResult >> 16) == 0x8007 && (ex.HResult & 0xFFFF) is 32 or 33;
        }

        return ex.HResult is 11 or 35;
    }

    // The lease; throws WorldHeldException when another holder has the world.
    public static WorldHostLease Acquire(string databasePath, WorldId worldId)
    {
        return TryAcquire(databasePath, worldId)
            ?? throw new WorldHeldException(worldId, GetLockPath(databasePath, worldId));
    }

    // Whether another holder has the world right now. Informational: the answer can be stale
    // the moment it returns; take the lease to hold a world.
    public static bool IsHeld(string databasePath, WorldId worldId)
    {
        using WorldHostLease? probe = TryAcquire(databasePath, worldId);
        return probe == null;
    }

    public void Dispose()
    {
        FileStream? stream = Interlocked.Exchange(ref _stream, null);
        stream?.Dispose();
    }
}
