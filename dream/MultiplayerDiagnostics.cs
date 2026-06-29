public sealed class MultiplayerDiagnostics : IMultiplayerDiagnosticsSink
{
    public MultiplayerDiagnostics(AuthoritativeFlowTrace? actionFlow = null)
    {
        ActionFlow = actionFlow ?? new AuthoritativeFlowTrace();
    }

    public AuthoritativeFlowTrace ActionFlow { get; }

    public void RecordSnapshotApplied(ServerSnapshotEnvelope snapshot, string sessionId)
    {
        ArgumentNullException.ThrowIfNull(sessionId);

        ulong? acceptedSequence = snapshot.LastAcceptedCommandSequences.TryGetValue(sessionId, out ulong sequence)
            ? sequence
            : null;

        ActionFlow.Add(new AuthoritativeFlowTraceEntry(
            Category: "snapshot.applied",
            SessionId: sessionId,
            Sequence: acceptedSequence,
            ServerTick: snapshot.ServerTick,
            Accepted: true,
            CorrectionApplied: false,
            Detail: $"actors={snapshot.Actors.Count}, worldMutations={snapshot.WorldMutations.Count}, damageEvents={snapshot.DamageEvents.Count}"));
    }

    public void RecordActionResult(AuthoritativeActionResult result, bool correctionApplied)
    {
        ArgumentNullException.ThrowIfNull(result);

        ActionFlow.Add(new AuthoritativeFlowTraceEntry(
            Category: result.Accepted ? "action.accepted" : "action.rejected",
            SessionId: result.SessionId,
            Sequence: result.ActionSequence,
            ServerTick: result.ServerTick,
            Accepted: result.Accepted,
            CorrectionApplied: correctionApplied,
            Detail: Describe(result)));
    }

    private static string Describe(AuthoritativeActionResult result)
    {
        if (!result.Accepted)
        {
            return result.Rejection?.Reason ?? "rejected";
        }

        return result.Payload switch
        {
            WorldMutationResult mutation => $"{mutation.Kind}@{mutation.CellPosition}",
            CombatResolutionResult combat => $"damage={combat.DamageApplied}, target={combat.TargetActorId?.ToString() ?? "none"}",
            PlayerStatsUpdateResult stats => $"str={stats.Stats.Strength}, aspd={stats.Stats.AttackSpeed:0.##}, vit={stats.Stats.Vitality}",
            null => "accepted-no-payload",
            _ => result.Payload.GetType().Name
        };
    }
}
