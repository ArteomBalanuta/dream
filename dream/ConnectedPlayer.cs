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
        IsSpawned = isSpawned;
    }

    public PlayerSessionId SessionId { get; }

    public int ActorId { get; }

    public bool IsLocal { get; }

    public string DisplayName { get; private set; }

    public bool IsSpawned { get; private set; }

    public ulong? LastAcceptedCommandSequence { get; private set; }

    public void MarkSpawned() => IsSpawned = true;

    public void MarkDespawned() => IsSpawned = false;

    public void UpdateDisplayName(string? displayName)
    {
        DisplayName = NormalizeDisplayName(displayName, SessionId);
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

    private static string NormalizeDisplayName(string? displayName, PlayerSessionId sessionId)
    {
        return string.IsNullOrWhiteSpace(displayName)
            ? sessionId.Value
            : displayName.Trim();
    }
}
