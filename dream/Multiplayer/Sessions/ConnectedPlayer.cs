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
