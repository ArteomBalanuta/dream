using Vector3 = OpenTK.Mathematics.Vector3;
using Dream.Client.Audio;
using Dream.Client.Networking;
using Dream.Client.Replication;
using Dream.Multiplayer.Diagnostics;
using Dream.Multiplayer.Protocol;
using Dream.Rendering.Actors;
using Dream.Client.Input;
using Dream.Multiplayer.Sessions;
using Dream.Rendering.Ui;
using Dream.Rendering.Ui.Panels;
using Dream.Rendering.ViewModels;
using OpenTK.Windowing.GraphicsLibraryFramework;
using Dream.Simulation.Combat;
using Dream.Simulation.Inventory;
using Dream.Simulation.World;

namespace Dream.Client.Scenes;

public partial class PlayScene
{
    // M1-B chat. Each scene binds a fresh state to the classic HUD, so a new scene starts empty.
    private readonly ChatInput _chatInput = BindNewChat();

    private static ChatInput BindNewChat()
    {
        ChatHudState state = new();
        ClassicHud.Chat = state;
        return new ChatInput(state);
    }

    // The window's text event (Game.OnTextInput); dropped unless the chat line is open.
    public void QueueTextInput(uint unicode) => _chatInput.QueueTextInput(unicode);

    // Called once a frame before the global keys. True when chat used the frame's keys.
    private bool HandleChatInput(KeyboardState keyboardState)
    {
        // The party and trade slash commands send player actions (M5).
        _chatInput.SendAction = SendEconomyIntent;
        bool connected = _remoteClient.IsConnected;
        bool canOpen = connected && _settings.CurrentRuntimeState.AllowGameplayInput;
        bool used = _chatInput.Update(keyboardState, canOpen, _remoteClient.QueueChat);
        if (_chatInput.State.OnlineVisible)
        {
            FillOnlineList();
        }

        return used;
    }

    // Only for the frames Tab is held; reads the roster built from the snapshot actor cache.
    private void FillOnlineList()
    {
        List<string> online = _chatInput.State.Online;
        online.Clear();
        IReadOnlyList<PlayerRosterEntry> roster = _scenePresenter.BuildPlayerRoster();
        for (int i = 0; i < roster.Count && online.Count < ChatHudState.MaxOnlineNames; i++)
        {
            string name = ChatHudState.Sanitize(roster[i].DisplayName, ChatHudState.MaxNameChars);
            online.Add(name.Length == 0 ? "?" : name);
        }
    }

    // While chat has the keys the global toggles (Esc, V, F3) must not fire, and must not fire
    // late from a key still held when the line closes.
    private void HandleGlobalInputUnlessTyping(KeyboardState keyboardState)
    {
        UpdateMultiplayerHud();
        if (HandleChatInput(keyboardState))
        {
            _wasSettingsTogglePressed = keyboardState.IsKeyDown(Keys.Escape);
            _wasInventoryTogglePressed = keyboardState.IsKeyDown(Keys.V);
            _wasAdminHudTogglePressed = keyboardState.IsKeyDown(Keys.F3);
            _wasInteractKeyPressed = keyboardState.IsKeyDown(Keys.E);
            // A digit still held when the line closes is not a press.
            _quickbarKeysDown = QuickbarInput.ReadMask(keyboardState);
            return;
        }

        // E (interact) and 1..6 (quickbar) before the global keys; Esc goes through PressEscape.
        HandleInteractKey(keyboardState);
        HandleQuickbarKeys(keyboardState);
        HandleGlobalInput(keyboardState);
    }

    public void ApplyReplicatedMessage(IServerToClientMessage message)
    {
        // M5, M6 and M8: owner-only pushes. None is a routed kind; each only feeds its own HUD state.
        if (message is ServerPartyState party)
        {
            ApplyPartyState(party);
            return;
        }

        if (message is ServerTradeState trade)
        {
            ApplyTradeState(trade);
            return;
        }

        if (message is ServerQuestLog questLog)
        {
            ApplyQuestLog(questLog);
            return;
        }

        if (message is ServerPlayerProgress progress)
        {
            ApplyOwnerProgress(progress);
            return;
        }

        if (message is ServerChatMessage chat)
        {
            // Not a routed kind: it only feeds the chat log, as plain clamped text.
            _chatInput.State.AddServerMessage(chat.Channel, chat.From, chat.Text);
            return;
        }

        if (message is ServerGroundItems groundItems)
        {
            // Not a routed kind either: it only feeds the ground list, which validates every entry.
            _groundItems.Apply(groundItems);
            return;
        }

        if (message is ServerPlayerVitals vitals)
        {
            // Not a routed kind either: mana and cooldowns for the quickbar, clamped by the state.
            _quickbarHud.ApplyVitals(vitals);
            return;
        }

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

            // A join starts a new session: no chat lines or half-typed text from the one before.
            _chatInput.State.Clear();
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
            // A new session shows no mana, cooldowns or bar from the one before; the join's inventory
            // brings the bar, and the first vitals message the mana.
            _quickbarHud.Clear();
            ApplyInventorySnapshot(joinAcceptedMessage.Inventory);
            // A join starts a new timeline; its own actor list counts, so a join while dead shows
            // the overlay at once instead of at the first snapshot.
            _deathTracker.Reset();
            _lootFeedback.Clear();
            // A new session shows no vendor from the one before. The ground list is not reset here:
            // its own Reset message does that, and deltas may already have arrived.
            _vendorHud.Close();
            _npcs.ApplyActors(joinAcceptedMessage.EffectiveInitialActors);
            ObserveLocalDeath(joinAcceptedMessage.EffectiveInitialActors, joinAcceptedMessage.CurrentServerTick);
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

            PlayWorldMutationSounds(snapshotMessage.Snapshot);
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
            // Add or update first so an enemy that spawned and was hit in this snapshot exists, then
            // damage, then remove: the killing blow's target is still in the registry while its number
            // resolves. All three still run before the presenter.
            RemoteEnemyStateApplier.AddOrUpdateEnemies(_runtime.Enemies, snapshotMessage.Snapshot.Actors);
            ClientSnapshotDamageApplier.ApplyToRuntime(_runtime, _events, snapshotMessage.Snapshot, _scenePresenter);
            RemoteEnemyStateApplier.RemoveAbsentEnemies(_runtime.Enemies, snapshotMessage.Snapshot.Actors);
            _diagnostics.RecordSnapshotApplied(snapshotMessage.Snapshot, _remoteClient.SessionId);

            _serverTickClock.Observe(snapshotMessage.Snapshot.ServerTick);
            _scenePresenter.ApplySnapshot(snapshotMessage.Snapshot);
            _npcs.ApplyActors(snapshotMessage.Snapshot.Actors);
            ObserveLocalDeath(snapshotMessage.Snapshot.Actors, snapshotMessage.Snapshot.ServerTick);
            LogPresenterDiagnostics("snapshot");
        }
        else if (message is InventoryPushMessage pushMessage)
        {
            ApplyInventoryPush(pushMessage);
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
            else if (resultTargetsLocalSession && actionMessage.Result.Payload is VendorOpenedResult vendorOpened)
            {
                ApplyVendorOpened(vendorOpened);
            }
            else if (resultTargetsLocalSession && actionMessage.Result.Payload is NpcDialogueResult dialogue)
            {
                ApplyNpcDialogue(dialogue);
            }
        }
    }

    // Feeds the respawn countdown from the local player's actor, if the list has it. A list without
    // it (a delta that left the player out) changes nothing.
    private void ObserveLocalDeath(IReadOnlyList<ActorSnapshot> actors, ulong serverTick)
    {
        string localSessionId = _remoteClient.SessionId;
        for (int i = 0; i < actors.Count; i++)
        {
            ActorSnapshot actor = actors[i];
            if (actor.Kind == CreatureKind.Player &&
                string.Equals(actor.SessionId, localSessionId, StringComparison.Ordinal))
            {
                _serverLocalActorId = actor.ActorId;
                _deathTracker.Observe(actor.IsAlive && actor.CurrentHealth > 0, serverTick);
                return;
            }
        }
    }

    // A server push after loot: the fresh inventory (when there is one), then the notices. It carries
    // no health or position and touches neither. Wire data is hostile: the notices are capped by the
    // feed, and the inventory goes through the same reader as every other snapshot.
    private void ApplyInventoryPush(InventoryPushMessage push)
    {
        if (push.Inventory is { } inventory)
        {
            ApplyInventorySnapshot(inventory);
        }

        _lootFeedback.AddNotices(push.Notices);
        if (push.Notices.Count > 0)
        {
            CurrentGameAudio.Instance.Play(SoundId.LootPickup);
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
        Vector3 remoteOnePosition = localPosition + new Vector3(MathF.Cos(orbit) * 2.5f, 0f, 3.5f + MathF.Sin(orbit) * 1.5f);
        Vector3 remoteTwoPosition = localPosition + new Vector3(-2.5f, 0f, 3.5f + MathF.Cos(orbit * 0.5f) * 1.5f);

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
                    "Loopback One",
                    VisualKey: LoopbackGalleryVisualKey(orbit, shieldOffset: 0),
                    Progression: null),
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
                    "Loopback Two",
                    VisualKey: LoopbackGalleryVisualKey(orbit, shieldOffset: 1))
            ],
            _runtime.InteractionMode,
            null,
            Array.Empty<WorldMutationDelta>(),
            Array.Empty<DamageEventDelta>());

        ApplyReplicatedMessage(new ServerSnapshotMessage(snapshot));
    }

    // A weapon gallery for the loopback remote players: every few seconds each one holds the next
    // known item (the shields in the off hand), so every mesh can be looked at without the items.
    private static string? LoopbackGalleryVisualKey(float seconds, int shieldOffset)
    {
        List<string> weapons = [];
        List<string> shields = [];
        foreach (string id in WeaponMeshCatalog.KnownVisualIds)
        {
            (WeaponMeshCatalog.IsShield(id) ? shields : weapons).Add(id);
        }

        if (weapons.Count == 0)
        {
            return null;
        }

        int step = (int)(seconds / 3f);
        string main = weapons[(step + (shieldOffset * 5)) % weapons.Count];
        string? off = shields.Count == 0 ? null : shields[(step + shieldOffset) % shields.Count];
        return PlayerEquipmentVisuals.Format(main, off);
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
    // The snapshot is the server's word and may be hostile: EquipmentLoadout skips non-finite values,
    // Percent modifiers and unknown stats and stops at 8 modifiers per slot, an unknown light is None, and a missing
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
            loadout.Set(EquipmentSlot.MainHand, mainItem.Modifiers!, flatOnly: true);
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

                loadout.Set(slot, item.Modifiers!, flatOnly: true);
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
        _inventoryApplyCount++;
        _quickbarHud.ApplyInventory(inventory);
        EquipmentPresentation presentation = ReadEquipment(inventory, _equipmentLoadout);
        ObserveEquipmentForAudio(presentation);

        _runtime.Player.Combatant.ApplyEquipment(_equipmentLoadout);
        _viewModelController.SetHeldItem(presentation.MainHandVisualId);
        _viewModelController.SetOffHandItem(presentation.OffHandVisualId);
        // Read by CollectHeldLights (PlayScene.HeldLights.cs) each frame.
        _localHeldLight = presentation.HeldLight;
        // Before the first snapshot nothing is drawn in the hands; afterwards each hand shows its item.
        _localEquipmentVisuals = inventory is null
            ? default
            : new PlayerEquipmentVisuals(
                PlayerEquipmentVisuals.Normalize(presentation.MainHandVisualId),
                PlayerEquipmentVisuals.Normalize(presentation.OffHandVisualId));
    }
}
