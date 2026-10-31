using Dream.Simulation.World;

namespace Dream.Server.Hosting;

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

    // The lease, or null when another holder has the world.
    public static WorldHostLease? TryAcquire(string databasePath, WorldId worldId)
    {
        string lockPath = GetLockPath(databasePath, worldId);
        try
        {
            FileStream stream = new(
                lockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.None);
            return new WorldHostLease(stream, worldId, lockPath);
        }
        catch (IOException)
        {
            // A sharing violation: the file is open with FileShare.None by a live holder.
            return null;
        }
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
