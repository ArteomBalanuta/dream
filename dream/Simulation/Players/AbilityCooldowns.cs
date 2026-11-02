using Dream.Multiplayer.Protocol;

namespace Dream.Simulation.Players;

// The running cooldowns of one session: a few (Key, ReadyTick) pairs, keys compared ordinal-ignore-case
// like content lookup. Lives on ConnectedPlayer because the primary seat's combatant is reused.
public sealed class AbilityCooldowns
{
    private readonly List<AbilityCooldown> _entries = [];

    public bool IsReady(string key, ulong tick)
    {
        for (int i = 0; i < _entries.Count; i++)
        {
            if (string.Equals(_entries[i].Key, key, StringComparison.OrdinalIgnoreCase))
            {
                return tick >= _entries[i].ReadyTick;
            }
        }

        return true;
    }

    // Starts or restarts the cooldown of key. Entries already expired at nowTick (ReadyTick at or below
    // it) are pruned, so the list stays as short as the cooldowns actually running.
    public void Start(string key, ulong readyTick, ulong nowTick = 0)
    {
        for (int i = _entries.Count - 1; i >= 0; i--)
        {
            AbilityCooldown entry = _entries[i];
            if (entry.ReadyTick <= nowTick || string.Equals(entry.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                _entries.RemoveAt(i);
            }
        }

        _entries.Add(new AbilityCooldown(key, readyTick));
    }

    // Appends the cooldowns still running at tick (ReadyTick above it).
    public void CopyRunning(ulong tick, List<AbilityCooldown> into)
    {
        ArgumentNullException.ThrowIfNull(into);
        for (int i = 0; i < _entries.Count; i++)
        {
            if (_entries[i].ReadyTick > tick)
            {
                into.Add(_entries[i]);
            }
        }
    }

    public void Clear() => _entries.Clear();
}
