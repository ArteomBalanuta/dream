using OpenTK.Mathematics;
using Xunit;

public class MultiplayerDiagnosticsTests
{
    [Fact]
    public void RecordActionResult_Captures_Rejected_Action_Metadata()
    {
        MultiplayerDiagnostics diagnostics = new(new AuthoritativeFlowTrace(maxEntries: 4));
        AuthoritativeActionResult result = new(
            "session-a",
            21,
            99,
            Accepted: false,
            Payload: null,
            Correction: new ClientCorrectionDelta(new Vector3(7f, 1.5f, 7f), Vector3.UnitX),
            Rejection: new RejectedUseDelta("occupied"));

        diagnostics.RecordActionResult(result, correctionApplied: true);

        AuthoritativeFlowTraceEntry entry = Assert.Single(diagnostics.ActionFlow.Entries);
        Assert.Equal("action.rejected", entry.Category);
        Assert.Equal("session-a", entry.SessionId);
        Assert.Equal((ulong)21, entry.Sequence);
        Assert.Equal((ulong)99, entry.ServerTick);
        Assert.False(entry.Accepted);
        Assert.True(entry.CorrectionApplied);
        Assert.Equal("occupied", entry.Detail);
    }

    [Fact]
    public void RecordSnapshotApplied_Uses_Last_Accepted_Sequence_For_Session()
    {
        MultiplayerDiagnostics diagnostics = new(new AuthoritativeFlowTrace(maxEntries: 4));
        ServerSnapshotEnvelope snapshot = new(
            120,
            new Dictionary<string, ulong>
            {
                ["session-a"] = 44
            },
            Array.Empty<ActorSnapshot>(),
            PlayerInteractionMode.Building,
            null,
            Array.Empty<WorldMutationDelta>(),
            Array.Empty<DamageEventDelta>());

        diagnostics.RecordSnapshotApplied(snapshot, "session-a");

        AuthoritativeFlowTraceEntry entry = Assert.Single(diagnostics.ActionFlow.Entries);
        Assert.Equal("snapshot.applied", entry.Category);
        Assert.Equal("session-a", entry.SessionId);
        Assert.Equal((ulong)44, entry.Sequence);
        Assert.Equal((ulong)120, entry.ServerTick);
        Assert.True(entry.Accepted);
        Assert.False(entry.CorrectionApplied);
        Assert.Contains("actors=0", entry.Detail);
    }
}
