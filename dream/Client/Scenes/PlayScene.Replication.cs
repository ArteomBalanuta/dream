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
                ClientJoinAcceptedApplier.ApplyToRuntime(_runtime, joinAcceptedMessage, _remoteClient.WorldTransferLimits);
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

            // A resync the client cannot host (for example a stored world of other dimensions) ends the
            // session with a readable reason instead of the exception escaping the frame.
            if (!RemoteWorldMutationApplier.TryApplySnapshotToRuntime(
                    _runtime,
                    snapshotMessage.Snapshot,
                    _endSessionAsProtocolError ??= reason => _remoteClient.EndSessionWithReason(reason, RemoteClientSession.ProtocolErrorLogEvent)))
            {
                return;
            }

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

    private readonly EquipmentLoadout _equipmentLoadout = new();

    // What the local player's inventory snapshot says to show: the visual of each hand and the one
    // held light. Built from the snapshot alone; the client reads no item catalog.
    internal readonly record struct EquipmentPresentation(
        string MainHandVisualId,
        string? OffHandVisualId,
        HeldLightKind HeldLight);

    // Fills the loadout (modifiers per slot) from the snapshot and returns the visuals and light.
    // The snapshot is the server's word and may be hostile: EquipmentLoadout skips non-finite values
    // and unknown stats and stops at 8 modifiers per slot, an unknown light is None, and a missing
    // or odd entry is an empty slot. It never throws.
    internal static EquipmentPresentation ReadEquipment(PlayerInventorySnapshot? inventory, EquipmentLoadout loadout)
    {
        loadout.Clear();
        if (inventory is not PlayerInventorySnapshot snapshot)
        {
            // Before the first snapshot the default loadout shows.
            return new EquipmentPresentation(ViewModelAssets.DefaultLoadout.HeldItemId, null, HeldLightKind.None);
        }

        HeldLightKind mainLight = HeldLightKind.None;
        HeldLightKind offLight = HeldLightKind.None;
        string mainVisual = ViewModelAssets.EmptyHeldItemId;
        string? offVisual = null;

        if (snapshot.EquippedSlot?.Item is InventoryItemSnapshot mainItem)
        {
            mainVisual = mainItem.VisualId ?? string.Empty;
            loadout.Set(EquipmentSlot.MainHand, mainItem.Modifiers!);
            mainLight = KnownLight(mainItem.HeldLight);
        }

        IReadOnlyList<InventorySlotSnapshot>? equipment = snapshot.Equipment;
        if (equipment is not null)
        {
            for (int i = 0; i < equipment.Count; i++)
            {
                InventorySlotSnapshot entry = equipment[i];
                // 100 is the main hand's own field; anything outside 101..103 is not a slot.
                if (entry.Item is not InventoryItemSnapshot item
                    || !EquipmentSlots.TryFromSlotIndex(entry.SlotIndex, out EquipmentSlot slot)
                    || slot == EquipmentSlot.MainHand)
                {
                    continue;
                }

                loadout.Set(slot, item.Modifiers!);
                if (slot == EquipmentSlot.OffHand)
                {
                    offVisual = item.VisualId ?? string.Empty;
                    offLight = KnownLight(item.HeldLight);
                }
            }
        }

        return new EquipmentPresentation(mainVisual, offVisual, EquipmentSlots.ResolveHeldLight(mainLight, offLight));
    }

    private static HeldLightKind KnownLight(HeldLightKind light) =>
        Enum.IsDefined(light) ? light : HeldLightKind.None;

    private void ApplyInventorySnapshot(PlayerInventorySnapshot? inventory)
    {
        _inventory = inventory;
        EquipmentPresentation presentation = ReadEquipment(inventory, _equipmentLoadout);

        _runtime.Player.Combatant.ApplyEquipment(_equipmentLoadout);
        _viewModelController.SetHeldItem(presentation.MainHandVisualId);
        _viewModelController.SetOffHandItem(presentation.OffHandVisualId);
        // Read by CollectHeldLights (PlayScene.HeldLights.cs) each frame.
        _localHeldLight = presentation.HeldLight;
    }
}
