using Vector3 = OpenTK.Mathematics.Vector3;
using Dream.Client.Networking;
using Dream.Client.Replication;
using Dream.Multiplayer.Diagnostics;
using Dream.Multiplayer.Protocol;
using Dream.Rendering.ViewModels;
using Dream.Simulation.Combat;
using Dream.Simulation.Inventory;
using Dream.Simulation.World;

namespace Dream.Client.Scenes;

public partial class PlayScene
{
    public void ApplyReplicatedMessage(IServerToClientMessage message)
    {
        _clientProtocol.ApplyServerMessage(message);
        if (message is JoinAcceptedMessage joinAcceptedMessage)
        {
            NetworkDiagnosticsLog.Write(
                "client.apply.join",
                $"local={_remoteClient.SessionId} {NetworkDiagnosticsLog.DescribeJoin(joinAcceptedMessage)}");
            try
            {
                ClientJoinAcceptedApplier.ApplyToRuntime(_runtime, joinAcceptedMessage);
            }
            catch (UnsupportedWorldException ex)
            {
                // A world this client cannot host: the session ends with a readable reason.
                _remoteClient.EndSessionWithReason(ex.Message, RemoteClientSession.UnsupportedWorldLogEvent);
                return;
            }
            catch (InvalidDataException ex)
            {
                // A payload that contradicts its own header (for example a size mismatch).
                _remoteClient.EndSessionWithReason(ex.Message, RemoteClientSession.ProtocolErrorLogEvent);
                return;
            }

            _predictionReconciler.Reset();
            _serverTickClock.Reset(joinAcceptedMessage.CurrentServerTick);
            if (joinAcceptedMessage.WorldClock is { } worldClockAnchor)
            {
                _worldClock.Apply(worldClockAnchor);
            }
            else
            {
                _worldClock.Clear();
            }

            ClientJoinAcceptedApplier.ApplyToPresenter(_scenePresenter, joinAcceptedMessage);
            ApplyInventorySnapshot(joinAcceptedMessage.Inventory);
            LogPresenterDiagnostics("join");
        }
        else if (message is ServerSnapshotMessage snapshotMessage)
        {
            if (NetworkDiagnosticsLog.IsEnabled)
            {
                RefreshDiagnosticsKeys();
                NetworkDiagnosticsLog.WriteEvery(
                    _snapshotLogKey,
                    DiagnosticsLogInterval,
                    "client.apply.snapshot",
                    (SessionId: _remoteClient.SessionId, snapshotMessage.Snapshot),
                    static state => $"local={state.SessionId} {NetworkDiagnosticsLog.DescribeSnapshot(state.Snapshot)}");
            }

            RemoteWorldMutationApplier.ApplySnapshotToRuntime(_runtime, snapshotMessage.Snapshot);
            RequestWorldResyncIfOutOfSync(snapshotMessage.Snapshot);
            ClientCorrectionApplier.ApplySnapshotToRuntime(
                _runtime,
                snapshotMessage.Snapshot,
                _remoteClient.SessionId,
                _predictionReconciler);
            RemoteEnemyStateApplier.ApplyActorSnapshots(_runtime.Enemies, snapshotMessage.Snapshot.Actors);
            ClientSnapshotDamageApplier.ApplyToRuntime(_runtime, _events, snapshotMessage.Snapshot, _scenePresenter);
            _diagnostics.RecordSnapshotApplied(snapshotMessage.Snapshot, _remoteClient.SessionId);

            _serverTickClock.Observe(snapshotMessage.Snapshot.ServerTick);
            _scenePresenter.ApplySnapshot(snapshotMessage.Snapshot);
            LogPresenterDiagnostics("snapshot");
        }
        else if (message is ServerActionMessage actionMessage)
        {
            if (NetworkDiagnosticsLog.IsEnabled)
            {
                NetworkDiagnosticsLog.Write(
                    "client.apply.action",
                    $"local={_remoteClient.SessionId} resultSession={actionMessage.Result.SessionId} " +
                    $"seq={actionMessage.Result.ActionSequence} accepted={actionMessage.Result.Accepted} " +
                    $"payload={actionMessage.Result.Payload?.GetType().Name ?? "none"}");
            }

            _actionSequences.ObserveActionResult(actionMessage.Result);
            bool resultTargetsLocalSession = string.Equals(
                actionMessage.Result.SessionId,
                _remoteClient.SessionId,
                StringComparison.Ordinal);
            ClientAuthoritativeActionApplier.ApplyActionResult(
                _runtime,
                _events,
                actionMessage.Result,
                _remoteClient.SessionId,
                _diagnostics);
            if (resultTargetsLocalSession && actionMessage.Result.Correction?.CorrectedPosition != null)
            {
                // A rejected-action correction hard-snaps the player, so earlier predictions no longer apply.
                _predictionReconciler.Reset();
            }

            if (resultTargetsLocalSession && actionMessage.Result.Payload is InventoryChangedResult inventoryChanged)
            {
                ApplyInventorySnapshot(inventoryChanged.Inventory);
            }
        }
    }

    private void UpdateLoopbackRemotePlayerDebugState()
    {
        if (!_enableLoopbackRemotePlayers)
        {
            return;
        }

        Vector3 localPosition = _runtime.Player.GetWorldPosition();
        float orbit = (float)_clientPresentationTimeSeconds;
        Vector3 remoteOnePosition = localPosition + new Vector3(MathF.Cos(orbit) * 3f, 0f, -5f + MathF.Sin(orbit) * 2f);
        Vector3 remoteTwoPosition = localPosition + new Vector3(-4f, 0f, -6f + MathF.Cos(orbit * 0.5f) * 1.5f);

        ServerSnapshotEnvelope snapshot = new(
            _runtime.CurrentServerTick,
            new Dictionary<string, ulong>(),
            [
                new ActorSnapshot(
                    _runtime.Player.Combatant.Actor.Id,
                    CreatureKind.Player,
                    _runtime.LocalPlayerSessionId.Value,
                    localPosition,
                    _runtime.Player.Camera.Front,
                    _runtime.Player.Combatant.Health.CurrentHealth,
                    _runtime.Player.Combatant.Health.MaxHealth,
                    _runtime.Player.Combatant.Health.IsAlive,
                    null,
                    null,
                    "local-player"),
                new ActorSnapshot(
                    9001,
                    CreatureKind.Player,
                    "loopback-remote-1",
                    remoteOnePosition,
                    (-Vector3.UnitZ).Normalized(),
                    100,
                    100,
                    true,
                    null,
                    null,
                    "Loopback One"),
                new ActorSnapshot(
                    9002,
                    CreatureKind.Player,
                    "loopback-remote-2",
                    remoteTwoPosition,
                    (-Vector3.UnitZ).Normalized(),
                    100,
                    100,
                    true,
                    null,
                    null,
                    "Loopback Two")
            ],
            _runtime.InteractionMode,
            null,
            Array.Empty<WorldMutationDelta>(),
            Array.Empty<DamageEventDelta>());

        ApplyReplicatedMessage(new ServerSnapshotMessage(snapshot));
    }

    public static bool ResolveLoopbackRemotePlayersEnabled()
    {
        string? value = Environment.GetEnvironmentVariable(LoopbackRemotePlayersEnvironmentVariable);
        return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }

    private const double WorldResyncRequestCooldownSeconds = 2d;
    private double _lastWorldResyncRequestSeconds = double.NegativeInfinity;

    // A missed or rejected delta used to leave the client's world diverged until reconnect.
    private void RequestWorldResyncIfOutOfSync(ServerSnapshotEnvelope snapshot)
    {
        if (!RemoteWorldMutationApplier.IsWorldOutOfSync(_runtime, snapshot) ||
            _clientPresentationTimeSeconds - _lastWorldResyncRequestSeconds < WorldResyncRequestCooldownSeconds)
        {
            return;
        }

        _lastWorldResyncRequestSeconds = _clientPresentationTimeSeconds;
        ulong clientChecksum = WorldStateChecksum.Compute(_runtime.Map);
        NetworkDiagnosticsLog.Write(
            "client.world.resync-request",
            $"session={_remoteClient.SessionId} expected={snapshot.WorldChecksum} actual={clientChecksum} seq={snapshot.WorldChecksumSequence}");
        _remoteClient.QueueWorldResyncRequest(clientChecksum);
    }

    private void ApplyInventorySnapshot(PlayerInventorySnapshot? inventory)
    {
        _inventory = inventory;
        string heldItemId;
        ToolStatModifier modifier = default;
        HeldLightKind heldLight = HeldLightKind.None;
        if (inventory?.EquippedSlot?.Item is InventoryItemSnapshot equippedItem)
        {
            heldItemId = equippedItem.VisualId;
            ToolDefinition definition = ToolCatalog.Resolve(equippedItem.ItemKind);
            modifier = definition.StatModifier;
            heldLight = definition.HeldLight;
        }
        else
        {
            heldItemId = ViewModelAssets.DefaultLoadout.HeldItemId;
        }

        _runtime.Player.Combatant.ApplyEquippedToolModifier(modifier);
        _viewModelController.SetHeldItem(heldItemId);
        // Read by CollectHeldLights (PlayScene.HeldLights.cs) each frame.
        _localHeldLight = heldLight;
    }
}
