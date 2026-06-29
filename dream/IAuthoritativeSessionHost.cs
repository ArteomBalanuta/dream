public interface IAuthoritativeSessionHost
{
    JoinAcceptedMessage JoinSession(string requestedSessionId, string displayName);

    AuthoritativeActionResult ProcessAction(PlayerActionMessage message);

    ServerSnapshotEnvelope ExportSnapshot(bool includeWorldSnapshot = false);
}
