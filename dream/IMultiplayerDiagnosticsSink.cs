public interface IMultiplayerDiagnosticsSink
{
    void RecordSnapshotApplied(ServerSnapshotEnvelope snapshot, string sessionId);

    void RecordActionResult(AuthoritativeActionResult result, bool correctionApplied);
}
