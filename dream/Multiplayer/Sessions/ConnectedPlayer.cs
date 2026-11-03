namespace Dream.Multiplayer.Sessions;

public sealed class ConnectedPlayer
{
    public ConnectedPlayer(
        PlayerSessionId sessionId,
        int actorId,
        bool isLocal,
        string displayName,
        bool isSpawned = true)
    {
        SessionId = sessionId;
        ActorId = actorId;
        IsLocal = isLocal;
        DisplayName = NormalizeDisplayName(displayName, sessionId);
        CharacterKey = new Dream.Simulation.Characters.CharacterKey(DisplayName);
        IsSpawned = isSpawned;
    }

    public PlayerSessionId SessionId { get; }

    public int ActorId { get; }

    public bool IsLocal { get; }

    public string DisplayName { get; private set; }

    // The persistence key. Follows the display name until BindCharacter fixes it.
    public Dream.Simulation.Characters.CharacterKey CharacterKey { get; private set; }

    private bool _characterBound;

    public bool IsSpawned { get; private set; }

    public ulong? LastAcceptedCommandSequence { get; private set; }

    public ulong? LastAcceptedActionSequence { get; private set; }

    // Spent by inventory actions that would write (wave H, H-D18). A field so the runtime spends it in
    // place; it is counted in server ticks, never wall time.
    internal InventoryActionBudget InventoryBudget;

    // Wave I: the server tick this seat respawns at, or 0 when no timer runs. Set by the runtime's
    // respawn scan when it first sees the seat's player dead, cleared on respawn and on detach. It is
    // not persisted (current_hp = 0 is the persisted state), and it lives on the session because the
    // primary Player is reused by successive sessions and a secondary is disposed on detach.
    internal ulong RespawnAtTick;

    // The last server tick whose enemy step ignores this player (spawn protection after a respawn), or 0
    // for none. Set on a first spawn, every join and every respawn; not persisted; cleared on detach and when
    // the player attacks.
    internal ulong ProtectedThroughTick;

    // M4: the running skill and consumable cooldowns of this session. On the session, not the combatant,
    // because the primary seat's combatant is reused by successive sessions.
    internal readonly Dream.Simulation.Players.AbilityCooldowns Cooldowns = new();

    // M4: set whenever mana, max mana or a cooldown changed, so the host sends this session's
    // ServerPlayerVitals. LastSentMana and LastSentMaxMana are what the owner last received (-1: never).
    internal bool VitalsDirty = true;

    internal int LastSentMana = -1;

    internal int LastSentMaxMana = -1;

    // The registry this player is registered in (null once it forgets the player); told when the
    // spawn state changes so it can keep its active and departed sets (PlayerSessionRegistry).
    internal PlayerSessionRegistry? Registry { get; set; }

    internal long RegistrationOrder { get; set; }

    internal bool CountedAsSpawnedRemote { get; set; }

    internal LinkedListNode<ConnectedPlayer>? DepartedNode { get; set; }

    public void MarkSpawned() => SetSpawned(true);

    public void MarkDespawned() => SetSpawned(false);

    private void SetSpawned(bool spawned)
    {
        if (IsSpawned == spawned)
        {
            return;
        }

        IsSpawned = spawned;
        Registry?.OnSpawnStateChanged(this);
    }

    public void UpdateDisplayName(string? displayName)
    {
        DisplayName = NormalizeDisplayName(displayName, SessionId);
        if (!_characterBound)
        {
            CharacterKey = new Dream.Simulation.Characters.CharacterKey(DisplayName);
        }
    }

    // Fixes the persistence key and the display name shown to clients (from the character's row).
    public void BindCharacter(Dream.Simulation.Characters.CharacterKey key, string displayName)
    {
        CharacterKey = key;
        _characterBound = true;
        DisplayName = NormalizeDisplayName(displayName, SessionId);
    }

    public void ResetAcceptedSequences()
    {
        LastAcceptedCommandSequence = null;
        LastAcceptedActionSequence = null;
    }

    public bool TryAcceptSequence(ulong sequence)
    {
        if (LastAcceptedCommandSequence.HasValue && sequence <= LastAcceptedCommandSequence.Value)
        {
            return false;
        }

        LastAcceptedCommandSequence = sequence;
        return true;
    }

    public bool TryAcceptActionSequence(ulong sequence)
    {
        if (LastAcceptedActionSequence.HasValue && sequence <= LastAcceptedActionSequence.Value)
        {
            return false;
        }

        LastAcceptedActionSequence = sequence;
        return true;
    }

    private static string NormalizeDisplayName(string? displayName, PlayerSessionId sessionId)
    {
        return string.IsNullOrWhiteSpace(displayName)
            ? sessionId.Value
            : displayName.Trim();
    }
}

// A token bucket counted in server ticks: a burst of 16 actions, refilling 8 per second. Tokens are
// kept in tick units (one action costs TicksPerAction), so the arithmetic is integer and exact.
internal struct InventoryActionBudget
{
    public const int Burst = 16;
    public const int RefillPerSecond = 8;
    public const int TicksPerAction = Dream.Simulation.Runtime.SimulationClock.AuthoritativeTicksPerSecond / RefillPerSecond;
    private const long Capacity = (long)Burst * TicksPerAction;

    private long _units;
    private ulong _lastTick;
    private bool _started;

    // Spends one action at the tick; false (and nothing spent) when the bucket holds less than one.
    public bool TrySpend(ulong currentTick)
    {
        if (!_started)
        {
            _started = true;
            _units = Capacity;
        }
        else if (currentTick > _lastTick)
        {
            ulong elapsed = currentTick - _lastTick;
            _units = elapsed >= (ulong)Capacity ? Capacity : Math.Min(Capacity, _units + (long)elapsed);
        }

        _lastTick = Math.Max(_lastTick, currentTick);
        if (_units < TicksPerAction)
        {
            return false;
        }

        _units -= TicksPerAction;
        return true;
    }
}
