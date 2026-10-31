namespace Dream.Multiplayer.Sessions;

public interface IServerSessionClaimRegistry
{
    bool TryClaimSession(string connectionId, string sessionId, out string? rejectionReason)
    {
        return TryClaimSession(connectionId, sessionId, displayName: null, out rejectionReason);
    }

    // displayName is the join's requested display name, or null when the caller has none. A
    // registry may use it to find a stale connection that holds the same player under another
    // session id (see DedicatedServerHost.TryClaimSession).
    bool TryClaimSession(string connectionId, string sessionId, string? displayName, out string? rejectionReason);

    // holder is the character and account the join was authorised for, or null when the join was not
    // checked (no accounts). The default ignores it and forwards to the overload above.
    bool TryClaimSession(
        string connectionId,
        string sessionId,
        string? displayName,
        Dream.Simulation.Characters.JoinHolderIdentity? holder,
        out string? rejectionReason)
    {
        return TryClaimSession(connectionId, sessionId, displayName, out rejectionReason);
    }

    void ReleaseConnection(string connectionId);
}
