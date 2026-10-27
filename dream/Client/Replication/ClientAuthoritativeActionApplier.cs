using Dream.Multiplayer.Diagnostics;
using Dream.Multiplayer.Protocol;
using Dream.Rendering.Hud;
using Dream.Simulation.Combat;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;

namespace Dream.Client.Replication;

public static class ClientAuthoritativeActionApplier
{
    public static void ApplyActionResult(
        IClientWorldState runtime,
        SceneEventHub events,
        AuthoritativeActionResult result,
        IMultiplayerDiagnosticsSink? diagnostics = null)
    {
        ApplyActionResult(runtime, events, result, localSessionId: null, diagnostics);
    }

    public static void ApplyActionResult(
        IClientWorldState runtime,
        SceneEventHub events,
        AuthoritativeActionResult result,
        string? localSessionId,
        IMultiplayerDiagnosticsSink? diagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(events);

        if (!IsResultForLocalSession(result, localSessionId))
        {
            diagnostics?.RecordActionResult(result, correctionApplied: false);
            return;
        }

        if (!result.Accepted)
        {
            bool correctionApplied = false;
            if (result.Correction != null)
            {
                ClientCorrectionApplier.ApplyCorrectionDeltaToRuntimePlayer(runtime, result.Correction);
                correctionApplied = true;
            }

            if (result.Rejection != null)
            {
                events.Publish(new AuthoritativeActionRejectedEvent(result));
            }

            diagnostics?.RecordActionResult(result, correctionApplied);

            return;
        }

        if (result.Payload == null)
        {
            diagnostics?.RecordActionResult(result, correctionApplied: false);
            return;
        }

        switch (result.Payload)
        {
            case WorldMutationResult mutation when mutation.Applied:
                RemoteWorldMutationApplier.ApplyWorldMutations(
                    runtime.Map,
                    [new WorldMutationDelta(mutation.Kind, mutation.CellPosition)]);
                break;

            case CombatResolutionResult combat when combat.DamageApplied > 0 && combat.TargetActorId.HasValue:
                if (runtime.TryResolveCombatActor(combat.TargetActorId.Value, out CombatActorHandle actor))
                {
                    events.Publish(new DamageAppliedEvent(actor, combat.DamageApplied, FloatingCombatTextKind.Regular));
                }
                break;

            case PlayerStatsUpdateResult statsUpdate:
                runtime.Player.Combatant.ApplyBaseStats(statsUpdate.Stats.ToCombatStats());
                break;
        }

        diagnostics?.RecordActionResult(result, correctionApplied: false);
    }

    private static bool IsResultForLocalSession(AuthoritativeActionResult result, string? localSessionId)
    {
        return string.IsNullOrWhiteSpace(localSessionId) ||
            string.Equals(result.SessionId, localSessionId, StringComparison.Ordinal);
    }
}
