using OpenTK.Mathematics;
using Xunit;
using Dream.Client.Presentation;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Application;
using Dream.Server;
using Dream.Server.Persistence;
using Dream.Simulation.Inventory;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;

namespace Dream.Tests.Simulation.Runtime;


public class RuntimeAuthoritativeSessionHostTests
{
    [Fact]
    public void CreateDefault_UsesConfiguredStartupWorldSize()
    {
        GameSettings settings = new(new Vector2i(1440, 1024));

        using RuntimeAuthoritativeSessionHost host = RuntimeAuthoritativeSessionHost.CreateDefault();

        Assert.Equal(settings.FloorSize, host.Runtime.Map.Width);
        Assert.Equal(settings.FloorSize, host.Runtime.Map.Depth);
    }

    [Fact]
    public void JoinSession_ExportsAuthoredSubcellStartupCellsSeparatelyFromFracturedCells()
    {
        using RuntimeAuthoritativeSessionHost host = RuntimeAuthoritativeSessionHost.CreateDefault();

        JoinAcceptedMessage accepted = host.JoinSession("session-a", "Player A");

        WorldSnapshot world = Assert.IsType<WorldSnapshot>(accepted.InitialWorld);
        Assert.Contains(world.AuthoredSubcellCells ?? Array.Empty<Vector3>(), cell => cell == new Vector3(18f, 1f, 16f));
        Assert.DoesNotContain(world.FracturedCubeCells ?? Array.Empty<Vector3>(), cell => cell == new Vector3(18f, 1f, 16f));
    }

    [Fact]
    public void JoinSession_InitialWorld_IncludesNearbyStartupTerrainLandmark()
    {
        using RuntimeAuthoritativeSessionHost host = RuntimeAuthoritativeSessionHost.CreateDefault();

        JoinAcceptedMessage accepted = host.JoinSession("session-a", "Player A");

        Assert.NotNull(accepted.InitialWorld);
        WorldSnapshot world = accepted.InitialWorld.Value;
        Assert.Contains(new Vector3(7f, 1f, 5f), world.PlacedCubeCells);
        Assert.Contains(new Vector3(12f, 1f, 8f), world.PlacedCubeCells);
        Assert.Equal(GeometryMaterialId.Dirt, host.Runtime.Map.TryGetGeometryCellAtCell(7, 1, 5)?.Definition.MaterialId);
        Assert.Equal(GeometryMaterialId.Stone, host.Runtime.Map.TryGetGeometryCellAtCell(12, 1, 8)?.Definition.MaterialId);
    }

    [Fact]
    public void JoinSession_StoresRequestedDisplayName_OnConnectedPlayer()
    {
        SceneEventHub events = new();
        using WorldSessionRuntime runtime = new(10, new Vector3(5f, 1.5f, 5f), events, loadVisualAssets: false);
        using RuntimeAuthoritativeSessionHost host = new(runtime);

        JoinAcceptedMessage accepted = host.JoinSession("session-a", "Player A");

        Assert.Equal("session-a", accepted.SessionId);
        Assert.True(runtime.Sessions.TryGet(new PlayerSessionId("session-a"), out ConnectedPlayer? player));
        Assert.NotNull(player);
        Assert.Equal("Player A", player.DisplayName);
    }

    [Fact]
    public void JoinSession_ReturnsCurrentJoinedPlayerRoster()
    {
        SceneEventHub events = new();
        using WorldSessionRuntime runtime = new(10, new Vector3(5f, 1.5f, 5f), events, loadVisualAssets: false);
        using RuntimeAuthoritativeSessionHost host = new(runtime);

        host.JoinSession("session-a", "Player A");
        JoinAcceptedMessage accepted = host.JoinSession("session-b", "Player B");

        Assert.Collection(
            accepted.EffectiveRoster.OrderBy(entry => entry.SessionId),
            first =>
            {
                Assert.Equal("session-a", first.SessionId);
                Assert.Equal("Player A", first.DisplayName);
            },
            second =>
            {
                Assert.Equal("session-b", second.SessionId);
                Assert.Equal("Player B", second.DisplayName);
            });
    }

    [Fact]
    public void JoinSession_FirstJoinedRemoteSessionRemainsPrimaryActorOwnerForLaterClients()
    {
        SceneEventHub events = new();
        using WorldSessionRuntime runtime = new(10, new Vector3(5f, 1.5f, 5f), events, loadVisualAssets: false);
        using RuntimeAuthoritativeSessionHost host = new(runtime);

        host.JoinSession("session-a", "Host Player");
        host.JoinSession("session-b", "Second Player");

        Assert.Equal(new PlayerSessionId("session-a"), runtime.PrimaryRemoteSessionId);

        ServerSnapshotEnvelope snapshot = host.ExportSnapshot();
        ClientScenePresenter secondClientPresenter = new(localSessionId: "session-b");
        secondClientPresenter.ApplySnapshot(snapshot, receivedAtSeconds: 1.0);
        secondClientPresenter.ApplySnapshot(snapshot, receivedAtSeconds: 2.0);

        RemotePlayerPresentationState hostModel = Assert.Single(
            secondClientPresenter.BuildRemotePlayerStates(renderTimeSeconds: 1.5),
            player => player.SessionId == "session-a");
        Assert.Equal("Host Player", hostModel.DisplayName);
    }

    [Fact]
    public void JoinSession_DoesNotDrainPendingWorldMutationsForExistingClients()
    {
        SceneEventHub events = new();
        using WorldSessionRuntime runtime = new(10, new Vector3(5f, 1.5f, 5f), events, loadVisualAssets: false);
        using RuntimeAuthoritativeSessionHost host = new(runtime);
        host.JoinSession("session-a", "Player A");

        AuthoritativeActionResult actionResult = host.ProcessAction(new PlayerActionMessage(
            new PlayerSessionId("session-a"),
            1,
            new BuildUseIntent(new Vector3(6f, 1f, 6f))));
        host.JoinSession("session-b", "Player B");

        ServerSnapshotEnvelope nextBroadcastSnapshot = host.ExportSnapshot();

        Assert.True(actionResult.Accepted);
        WorldMutationDelta mutation = Assert.Single(nextBroadcastSnapshot.WorldMutations);
        Assert.Equal(WorldMutationKind.CubePlaced, mutation.Kind);
        Assert.Equal(new Vector3(6f, 1f, 6f), mutation.CellPosition);
    }

    [Fact]
    public void HostRejectsSecondConnectionClaimingExistingSessionId()
    {
        using DedicatedServerHost host = DedicatedServerHost.CreateRuntimeAttached();

        IReadOnlyList<IServerToClientMessage> firstReplies = host.HandleClientMessage(
            "connection-1",
            new ClientJoinRequestMessage("session-a", "Player A"));
        IReadOnlyList<IServerToClientMessage> secondReplies = host.HandleClientMessage(
            "connection-2",
            new ClientJoinRequestMessage("session-a", "Impostor"));

        Assert.IsType<JoinAcceptedMessage>(Assert.Single(firstReplies));
        JoinRejectedMessage rejection = Assert.IsType<JoinRejectedMessage>(Assert.Single(secondReplies));
        Assert.Contains("already", rejection.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void JoinSession_RejectsDuplicateDisplayNameAcrossDifferentSessions()
    {
        SceneEventHub events = new();
        using WorldSessionRuntime runtime = new(10, new Vector3(5f, 1.5f, 5f), events, loadVisualAssets: false);
        using RuntimeAuthoritativeSessionHost host = new(runtime);

        host.JoinSession("session-a", "Player A");

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => host.JoinSession("session-b", "Player A"));

        Assert.Contains("already claimed", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AdvanceTick_TracksInteractionModePerSessionInsteadOfGlobally()
    {
        SceneEventHub events = new();
        using WorldSessionRuntime runtime = new(10, new Vector3(5f, 1.5f, 5f), events, loadVisualAssets: false);
        using RuntimeAuthoritativeSessionHost host = new(runtime);
        PlayerSimulationTuning tuning = CreatePlayerTuning();
        PlayerSessionId primarySession = new("session-a");
        PlayerSessionId secondarySession = new("session-b");
        host.JoinSession(primarySession.Value, "Player A");
        host.JoinSession(secondarySession.Value, "Player B");

        host.AdvanceTick(
            serverTick: 1,
            SimulationClock.FixedDeltaSeconds,
            [
                CreateModeSwitchCommand(primarySession, sequence: 1, PlayerInteractionMode.Building),
                CreateModeSwitchCommand(secondarySession, sequence: 1, PlayerInteractionMode.Combat)
            ],
            tuning,
            allowGameplaySimulation: false);

        ServerSnapshotEnvelope snapshot = host.ExportSnapshot();
        ActorSnapshot primaryActor = Assert.Single(snapshot.Actors, actor => actor.SessionId == primarySession.Value);
        ActorSnapshot secondaryActor = Assert.Single(snapshot.Actors, actor => actor.SessionId == secondarySession.Value);

        Assert.Equal(PlayerInteractionMode.Building, primaryActor.InteractionMode);
        Assert.Equal(PlayerInteractionMode.Combat, secondaryActor.InteractionMode);
    }

    [Fact]
    public void JoinSession_RestoresPersistedProfileAndWorldStateForReturningPlayer()
    {
        DateTime now = new(2026, 7, 4, 12, 0, 0, DateTimeKind.Utc);
        InMemoryPlayerProfileRepository playerProfiles = new();
        InMemoryWorldPersistenceRepository worldPersistence = new();
        playerProfiles.Save(new PersistedPlayerProfile(
            PlayerName: "Player A",
            DisplayName: "Player A",
            InteractionMode: PlayerInteractionMode.Combat,
            Stats: new PersistedCreatureStats(
                CurrentHealth: 73,
                MaxHealth: 100,
                Strength: 17,
                Agility: 10,
                Vitality: 14,
                Energy: 10,
                MovementSpeed: 6f,
                AttackSpeed: 3.5f,
                Level: 1),
            CreatedUtc: now,
            UpdatedUtc: now));
        worldPersistence.SavePlayerWorldState(new PersistedPlayerWorldState(
            PlayerName: "Player A",
            Position: new Vector3(11f, 1.5f, 7f),
            Facing: Vector3.UnitX,
            CurrentHealth: 73,
            CurrentEnergy: 0,
            UpdatedUtc: now));

        SceneEventHub events = new();
        using WorldSessionRuntime runtime = new(
            20,
            new Vector3(5f, 1.5f, 5f),
            events,
            loadVisualAssets: false,
            playerProfiles: playerProfiles,
            worldPersistence: worldPersistence);
        using RuntimeAuthoritativeSessionHost host = new(runtime);

        host.JoinSession("session-a", "Player A");

        Assert.Equal(PlayerInteractionMode.Combat, runtime.InteractionMode);
        Assert.Equal(17, runtime.Player.Combatant.Stats.Strength);
        Assert.Equal(3.5f, runtime.Player.Combatant.Stats.AttackSpeed);
        Assert.Equal(14, runtime.Player.Combatant.Stats.Vitality);
        Assert.Equal(73, runtime.Player.Combatant.Health.CurrentHealth);
        Assert.Equal(100, runtime.Player.Combatant.Health.MaxHealth);
        Assert.Equal(11f, runtime.Player.GetWorldPosition().X, 3);
        Assert.Equal(7f, runtime.Player.GetWorldPosition().Z, 3);
        Assert.Equal(1f, runtime.Player.Camera.Front.X, 3);
        Assert.Equal(0f, runtime.Player.Camera.Front.Z, 3);
    }

    [Fact]
    public void JoinSession_Seeds_StarterInventory_For_FirstTimePlayer()
    {
        SceneEventHub events = new();
        using WorldSessionRuntime runtime = new(10, new Vector3(5f, 1.5f, 5f), events, loadVisualAssets: false);
        using RuntimeAuthoritativeSessionHost host = new(runtime);

        JoinAcceptedMessage accepted = host.JoinSession("session-a", "Player A");

        Assert.NotNull(accepted.Inventory);
        Assert.Equal(InventoryConstants.GridColumns, accepted.Inventory?.Columns);
        Assert.Equal(InventoryConstants.GridRows, accepted.Inventory?.Rows);
        Assert.NotNull(accepted.Inventory?.EquippedSlot?.Item);
        Assert.Equal("Builder Hammer", accepted.Inventory?.EquippedSlot?.Item?.DisplayName);
    }

    [Fact]
    public void ProcessAction_InventoryEquip_Changes_Authoritative_EquippedTool()
    {
        SceneEventHub events = new();
        using WorldSessionRuntime runtime = new(10, new Vector3(5f, 1.5f, 5f), events, loadVisualAssets: false);
        using RuntimeAuthoritativeSessionHost host = new(runtime);
        host.JoinSession("session-a", "Player A");

        AuthoritativeActionResult result = host.ProcessAction(new PlayerActionMessage(
            new PlayerSessionId("session-a"),
            5,
            new InventoryEquipIntent(1)));

        InventoryChangedResult payload = Assert.IsType<InventoryChangedResult>(result.Payload);
        Assert.Equal("Field Blade", payload.Inventory.EquippedSlot?.Item?.DisplayName);
        InventorySlotSnapshot equippedBackpackSlot = Assert.Single(payload.Inventory.Slots, slot => slot.IsEquipped);
        Assert.Equal(1, equippedBackpackSlot.SlotIndex);
    }

    [Fact]
    public void AdvanceTick_PrimaryRemoteCommandMovesForwardAlongClientFacing()
    {
        SceneEventHub events = new();
        using WorldSessionRuntime runtime = new(10, new Vector3(5f, 1.5f, 5f), events, loadVisualAssets: false);
        using RuntimeAuthoritativeSessionHost host = new(runtime);
        PlayerSimulationTuning tuning = CreatePlayerTuning();
        PlayerSessionId sessionId = new("session-a");
        host.JoinSession(sessionId.Value, "Player A");

        Vector3 start = runtime.Player.GetWorldPosition();
        host.AdvanceTick(
            serverTick: 1,
            SimulationClock.FixedDeltaSeconds,
            [CreateForwardCommand(sessionId, sequence: 1, Vector3.UnitX)],
            tuning,
            allowGameplaySimulation: false);
        host.AdvanceTick(
            serverTick: 2,
            SimulationClock.FixedDeltaSeconds,
            [CreateForwardCommand(sessionId, sequence: 2, Vector3.UnitX)],
            tuning,
            allowGameplaySimulation: false);

        Vector3 end = runtime.Player.GetWorldPosition();
        ServerSnapshotEnvelope snapshot = host.ExportSnapshot();
        ActorSnapshot actor = Assert.Single(snapshot.Actors, actor => actor.SessionId == sessionId.Value);

        Assert.True(end.X > start.X);
        Assert.Equal(start.Z, end.Z, 3);
        Assert.Equal(1f, runtime.Player.Camera.Front.X, 3);
        Assert.Equal(0f, runtime.Player.Camera.Front.Z, 3);
        Assert.Equal(1f, actor.Facing.X, 3);
        Assert.Equal(0f, actor.Facing.Z, 3);
        Assert.True(actor.Position.X > start.X);
        Assert.Equal(start.Z, actor.Position.Z, 3);
    }

    [Fact]
    public void AdvanceTick_PrimaryRemoteStrafeMovesAlongClientFacingRightAxis()
    {
        SceneEventHub events = new();
        using WorldSessionRuntime runtime = new(10, new Vector3(5f, 1.5f, 5f), events, loadVisualAssets: false);
        using RuntimeAuthoritativeSessionHost host = new(runtime);
        PlayerSimulationTuning tuning = CreatePlayerTuning();
        PlayerSessionId sessionId = new("session-a");
        host.JoinSession(sessionId.Value, "Player A");

        Vector3 start = runtime.Player.GetWorldPosition();
        host.AdvanceTick(
            serverTick: 1,
            SimulationClock.FixedDeltaSeconds,
            [CreateStrafeCommand(sessionId, sequence: 1, Vector3.UnitX)],
            tuning,
            allowGameplaySimulation: false);
        host.AdvanceTick(
            serverTick: 2,
            SimulationClock.FixedDeltaSeconds,
            [CreateStrafeCommand(sessionId, sequence: 2, Vector3.UnitX)],
            tuning,
            allowGameplaySimulation: false);

        Vector3 end = runtime.Player.GetWorldPosition();

        Assert.Equal(start.X, end.X, 3);
        Assert.True(end.Z > start.Z);
        Assert.Equal(1f, runtime.Player.Camera.Front.X, 3);
        Assert.Equal(0f, runtime.Player.Camera.Front.Z, 3);
    }

    [Fact]
    public void AdvanceTick_PrimaryRemoteCommandWithoutFacingUsesExistingAuthoritativeFacing()
    {
        SceneEventHub events = new();
        using WorldSessionRuntime runtime = new(10, new Vector3(5f, 1.5f, 5f), events, loadVisualAssets: false);
        using RuntimeAuthoritativeSessionHost host = new(runtime);
        PlayerSimulationTuning tuning = CreatePlayerTuning();
        PlayerSessionId sessionId = new("session-a");
        host.JoinSession(sessionId.Value, "Player A");
        runtime.Player.SetFacing(Vector3.UnitX);

        Vector3 start = runtime.Player.GetWorldPosition();
        host.AdvanceTick(
            serverTick: 1,
            SimulationClock.FixedDeltaSeconds,
            [CreateForwardCommandWithoutFacing(sessionId, sequence: 1)],
            tuning,
            allowGameplaySimulation: false);
        host.AdvanceTick(
            serverTick: 2,
            SimulationClock.FixedDeltaSeconds,
            [CreateForwardCommandWithoutFacing(sessionId, sequence: 2)],
            tuning,
            allowGameplaySimulation: false);

        Vector3 end = runtime.Player.GetWorldPosition();

        Assert.True(end.X > start.X);
        Assert.Equal(start.Z, end.Z, 3);
        Assert.Equal(1f, runtime.Player.Camera.Front.X, 3);
        Assert.Equal(0f, runtime.Player.Camera.Front.Z, 3);
    }

    private static PlayerCommandFrame CreateForwardCommand(
        PlayerSessionId sessionId,
        ulong sequence,
        Vector3 facing)
    {
        return new PlayerCommandFrame(
            sessionId,
            sequence,
            PlayerModeSwitchIntent.None,
            new PlayerMoveIntent(ForwardAxis: 1f, StrafeAxis: 0f),
            new PlayerLookIntent(0f, 0f, facing),
            PrimaryUsePressed: false);
    }

    private static PlayerCommandFrame CreateStrafeCommand(
        PlayerSessionId sessionId,
        ulong sequence,
        Vector3 facing)
    {
        return new PlayerCommandFrame(
            sessionId,
            sequence,
            PlayerModeSwitchIntent.None,
            new PlayerMoveIntent(ForwardAxis: 0f, StrafeAxis: 1f),
            new PlayerLookIntent(0f, 0f, facing),
            PrimaryUsePressed: false);
    }

    private static PlayerCommandFrame CreateForwardCommandWithoutFacing(
        PlayerSessionId sessionId,
        ulong sequence)
    {
        return new PlayerCommandFrame(
            sessionId,
            sequence,
            PlayerModeSwitchIntent.None,
            new PlayerMoveIntent(ForwardAxis: 1f, StrafeAxis: 0f),
            PlayerLookIntent.None,
            PrimaryUsePressed: false);
    }

    private static PlayerCommandFrame CreateModeSwitchCommand(
        PlayerSessionId sessionId,
        ulong sequence,
        PlayerInteractionMode interactionMode)
    {
        return new PlayerCommandFrame(
            sessionId,
            sequence,
            new PlayerModeSwitchIntent(interactionMode),
            PlayerMoveIntent.None,
            PlayerLookIntent.None,
            PrimaryUsePressed: false);
    }

    private static PlayerSimulationTuning CreatePlayerTuning()
    {
        return new PlayerSimulationTuning(
            EyeHeightOffset: 0.35f,
            MouseSensitivity: 2.5f,
            MoveSpeed: 6f,
            NoclipSpeed: 150f,
            JumpVelocity: 5f);
    }

    private sealed class InMemoryPlayerProfileRepository : IPlayerProfileRepository
    {
        private readonly Dictionary<string, PersistedPlayerProfile> _profiles = new(StringComparer.OrdinalIgnoreCase);

        public PersistedPlayerProfile GetOrCreate(string playerName)
        {
            if (_profiles.TryGetValue(playerName, out PersistedPlayerProfile profile))
            {
                return profile;
            }

            profile = new PersistedPlayerProfile(
                playerName,
                playerName,
                PlayerInteractionMode.Building,
                new PersistedCreatureStats(100, 100, 10, 10, 10, 10, 6f, 2f, 1),
                DateTime.UtcNow,
                DateTime.UtcNow);
            _profiles[playerName] = profile;
            return profile;
        }

        public PersistedPlayerProfile? TryGet(string playerName)
        {
            return _profiles.TryGetValue(playerName, out PersistedPlayerProfile profile) ? profile : null;
        }

        public void Save(PersistedPlayerProfile profile)
        {
            _profiles[profile.PlayerName] = profile;
        }
    }

    private sealed class InMemoryWorldPersistenceRepository : IWorldPersistenceRepository
    {
        private readonly Dictionary<string, PersistedPlayerWorldState> _players = new(StringComparer.OrdinalIgnoreCase);

        public PersistedPlayerWorldState? TryGetPlayerWorldState(string playerName)
        {
            return _players.TryGetValue(playerName, out PersistedPlayerWorldState state) ? state : null;
        }

        public void SavePlayerWorldState(PersistedPlayerWorldState state)
        {
            _players[state.PlayerName] = state;
        }

        public IReadOnlyList<PersistedPlacedCube> LoadPlacedCubes() => Array.Empty<PersistedPlacedCube>();

        public void SavePlacedCube(PersistedPlacedCube cube)
        {
        }

        public void DeletePlacedCube(int cubeId)
        {
        }

        public IReadOnlyList<PersistedEnemyInstance> LoadActiveEnemies() => Array.Empty<PersistedEnemyInstance>();

        public void SaveEnemyInstance(PersistedEnemyInstance enemy)
        {
        }

        public void DeleteEnemyInstance(int enemyId)
        {
        }

        public IReadOnlyList<PersistedItemInstance> LoadItemInstances() => Array.Empty<PersistedItemInstance>();

        public void SaveItemInstance(PersistedItemInstance item)
        {
        }

        public void DeleteItemInstance(long itemInstanceId)
        {
        }

        public IReadOnlyList<PersistedInventorySlot> LoadInventorySlots() => Array.Empty<PersistedInventorySlot>();

        public void SaveInventorySlot(PersistedInventorySlot slot)
        {
        }

        public void DeleteInventorySlots(string playerName)
        {
        }
    }
}
