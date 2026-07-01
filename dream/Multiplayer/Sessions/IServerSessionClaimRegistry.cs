namespace Dream.Multiplayer.Sessions;

public interface IServerSessionClaimRegistry
{
    bool TryClaimSession(string connectionId, string sessionId, out string? rejectionReason);

    void ReleaseConnection(string connectionId);
}
