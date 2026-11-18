using Dream.Simulation.Characters;

namespace Dream.Simulation.Persistence;

// Where a joining player's persisted-state load stands (WorldSessionRuntime.GetPersistedPlayerLoadState).
public enum PersistedPlayerLoadState
{
    // Loaded (or the runtime has no profiles): the join can be applied without database I/O.
    Ready,
    // Still queued or running on the persistence worker.
    Pending,
    // The load failed (or could not be queued).
    Failed,
    // No load exists for this name.
    Missing
}

// A joining player's persisted profile, world state and inventory, loaded on the persistence worker before the
// join is applied on the tick (see WorldSessionRuntime.BeginPersistedPlayerPrefetch). The load is
// read-only: a player with no saved profile completes with no profile, and the row is created only
// when the join is applied. Completed once by the worker; read by the tick (IsCompleted, Error,
// TryGetLoadResult).
public sealed class PersistedPlayerPrefetch
{
    private readonly ManualResetEventSlim _done = new(initialState: false);
    private PersistedPlayerProfile? _profile;
    private PersistedPlayerWorldState? _worldState;
    private PersistedPlayerInventory? _inventory;
    private Exception? _error;

    private CharacterLookup? _lookup;

    public PersistedPlayerPrefetch(string playerName, AccountKey? account = null)
    {
        PlayerName = playerName;
        Account = account;
    }

    public string PlayerName { get; }

    // When the prefetch was started (before its read was queued). A profile save this runtime queued
    // after that may be missing from the row it reads.
    public DateTime StartedUtc { get; internal set; } = DateTime.UtcNow;

    public AccountKey? Account { get; }

    // What the account lookup read; null until completed, and when the runtime has no accounts.
    public CharacterLookup? Lookup => _done.IsSet ? _lookup : null;

    // The character key whose presence lease this prefetch's load took, published as soon as the claim
    // commits -- before the load completes, which is what Lookup waits for. The release path reads it, so
    // a join abandoned while its load was still running still lets the lease go instead of leaving it to
    // time out. Null while this prefetch holds no lease.
    internal volatile string? ClaimedPresenceKey;

    public bool IsCompleted => _done.IsSet;

    // Finished without an error: its result can be applied.
    public bool IsLoaded => _done.IsSet && _error == null;

    public Exception? Error => _done.IsSet ? _error : null;

    // Blocks until the load finished or the timeout passed; true when it finished (or failed).
    public bool Wait(TimeSpan timeout)
    {
        return _done.Wait(timeout);
    }

    // The loaded state, when the load finished without an error; the profile is null when the
    // player has no saved profile yet.
    public bool TryGetLoadResult(out PersistedPlayerProfile? profile, out PersistedPlayerWorldState? worldState)
    {
        if (!_done.IsSet || _error != null)
        {
            profile = null;
            worldState = null;
            return false;
        }

        profile = _profile;
        worldState = _worldState;
        return true;
    }

    // The loaded state, when the load finished without an error and found a saved profile.
    public bool TryGetResult(out PersistedPlayerProfile profile, out PersistedPlayerWorldState? worldState)
    {
        if (!TryGetLoadResult(out PersistedPlayerProfile? loaded, out worldState) || loaded == null)
        {
            profile = default;
            worldState = null;
            return false;
        }

        profile = loaded.Value;
        return true;
    }

    // The loaded inventory, when the load finished without an error and read one (null when the
    // runtime has no world persistence, or the load predates per-player inventories).
    public bool TryGetInventory(out PersistedPlayerInventory? inventory)
    {
        if (!_done.IsSet || _error != null)
        {
            inventory = null;
            return false;
        }

        inventory = _inventory;
        return inventory != null;
    }

    // First result wins. Reads run outside the write queue's transactions (see
    // PersistenceWriteQueue.EnqueueRead), so a completed result only ever holds committed rows.
    internal void Complete(
        PersistedPlayerProfile? profile,
        PersistedPlayerWorldState? worldState,
        PersistedPlayerInventory? inventory = null,
        CharacterLookup? lookup = null)
    {
        if (_done.IsSet)
        {
            return;
        }

        _lookup = lookup;
        _profile = profile;
        _worldState = worldState;
        _inventory = inventory;
        _done.Set();
    }

    internal void Fail(Exception error)
    {
        if (_done.IsSet)
        {
            return;
        }

        _error = error;
        _done.Set();
    }
}
