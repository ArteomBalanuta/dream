namespace Dream.Server.Persistence;

// A joining player's persisted profile and world state, loaded on the persistence worker before the
// join is applied on the tick (see WorldSessionRuntime.BeginPersistedPlayerPrefetch). Completed
// once by the worker; read by the receive thread (Wait) and the tick (IsCompleted, TryGetResult).
public sealed class PersistedPlayerPrefetch
{
    private readonly ManualResetEventSlim _done = new(initialState: false);
    private PersistedPlayerProfile _profile;
    private PersistedPlayerWorldState? _worldState;
    private Exception? _error;

    public PersistedPlayerPrefetch(string playerName)
    {
        PlayerName = playerName;
    }

    public string PlayerName { get; }

    public bool IsCompleted => _done.IsSet;

    public Exception? Error => _done.IsSet ? _error : null;

    // Blocks until the load finished or the timeout passed; true when it finished (or failed).
    public bool Wait(TimeSpan timeout)
    {
        return _done.Wait(timeout);
    }

    public bool TryGetResult(out PersistedPlayerProfile profile, out PersistedPlayerWorldState? worldState)
    {
        if (!_done.IsSet || _error != null)
        {
            profile = default;
            worldState = null;
            return false;
        }

        profile = _profile;
        worldState = _worldState;
        return true;
    }

    // First result wins: a write group whose commit failed replays its reads, and the tick may
    // already be reading the first result.
    internal void Complete(PersistedPlayerProfile profile, PersistedPlayerWorldState? worldState)
    {
        if (_done.IsSet)
        {
            return;
        }

        _profile = profile;
        _worldState = worldState;
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
