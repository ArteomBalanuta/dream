using OpenTK.Mathematics;
using System.Reflection;
using Xunit;
using Dream.Client.Presentation;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Application;
using Dream.Server;
using Dream.Server.Persistence;
using Dream.Simulation.Persistence;
using Dream.Simulation.Rules;
using Dream.Simulation.Combat;
using Dream.Simulation.Enemies;
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
        using WorldSessionRuntime runtime = new(10, new Vector3(5f, 1.5f, 5f), events);
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
        using WorldSessionRuntime runtime = new(10, new Vector3(5f, 1.5f, 5f), events);
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
        using WorldSessionRuntime runtime = new(10, new Vector3(5f, 1.5f, 5f), events);
        using RuntimeAuthoritativeSessionHost host = new(runtime);

        host.JoinSession("session-a", "Host Player");
        host.JoinSession("session-b", "Second Player");

        Assert.Equal(new PlayerSessionId("session-a"), runtime.PrimaryRemoteSessionId);

        ServerSnapshotEnvelope snapshot = host.ExportSnapshot();
        ClientScenePresenter secondClientPresenter = new(localSessionId: "session-b");
        secondClientPresenter.ApplySnapshot(snapshot);
        secondClientPresenter.ApplySnapshot(snapshot);

        RemotePlayerPresentationState hostModel = Assert.Single(
            secondClientPresenter.BuildRemotePlayerStates(renderTick: snapshot.ServerTick),
            player => player.SessionId == "session-a");
        Assert.Equal("Host Player", hostModel.DisplayName);
    }

    [Fact]
    public void ProcessAction_PrimaryDead_DoesNotBlockSecondaryRemoteEnemyCombat()
    {
        SceneEventHub events = new();
        using WorldSessionRuntime runtime = new(10, new Vector3(5f, 1.5f, 5f), events);
        using RuntimeAuthoritativeSessionHost host = new(runtime);
        PlayerSessionId primarySession = new("session-a");
        PlayerSessionId secondarySession = new("session-b");
        host.JoinSession(primarySession.Value, "Host Player");
        host.JoinSession(secondarySession.Value, "Second Player");

        runtime.Player.SetHealth(0, runtime.Player.Combatant.Health.MaxHealth);
        runtime.Enemies.Clear();

        ServerSnapshotEnvelope before = host.ExportSnapshot();
        ActorSnapshot secondaryActor = Assert.Single(before.Actors, actor => actor.SessionId == secondarySession.Value);
        EnemyCombatant combatant = new(
            () => secondaryActor.Position + (Vector3.UnitX * 1.5f),
            events,
            actorId: 4001,
            maxHealth: 100,
            attackDamage: 10,
            attackIntervalSeconds: 1f);
        EnemyActor enemy = new(
            combatant,
            secondaryActor.Position + (Vector3.UnitX * 1.5f),
            aggroRange: 10f,
            attackRange: 1.35f,
            moveSpeed: 3f,
            displayName: "Wolf");
        runtime.Enemies.Add(enemy);

        host.AdvanceTick(
            serverTick: 1,
            SimulationClock.FixedDeltaSeconds,
            [
                new PlayerCommandFrame(
                    secondarySession,
                    1,
                    new PlayerModeSwitchIntent(PlayerInteractionMode.Combat),
                    PlayerMoveIntent.None,
                    new PlayerLookIntent(0f, 0f, Vector3.UnitX),
                    PrimaryUsePressed: false)
            ],
            CreatePlayerTuning(),
            allowGameplaySimulation: false);

        AuthoritativeActionResult result = host.ProcessAction(new PlayerActionMessage(
            secondarySession,
            1,
            new CombatUseIntent(Vector3.Zero, Vector3.UnitX)));

        Assert.True(result.Accepted);
        CombatResolutionResult payload = Assert.IsType<CombatResolutionResult>(result.Payload);
        Assert.Equal(4001, payload.TargetActorId);
        Assert.True(payload.DamageApplied > 0);
        Assert.True(enemy.Combatant.Health.CurrentHealth < enemy.Combatant.Health.MaxHealth);
    }

    [Fact]
    public void AdvanceTick_SecondaryRemotePrimaryUseCombat_DamagesEnemy()
    {
        SceneEventHub events = new();
        using WorldSessionRuntime runtime = new(10, new Vector3(5f, 1.5f, 5f), events);
        using RuntimeAuthoritativeSessionHost host = new(runtime);
        PlayerSessionId primarySession = new("session-a");
        PlayerSessionId secondarySession = new("session-b");
        host.JoinSession(primarySession.Value, "Host Player");
        host.JoinSession(secondarySession.Value, "Second Player");

        runtime.Enemies.Clear();
        ServerSnapshotEnvelope before = host.ExportSnapshot();
        ActorSnapshot secondaryActor = Assert.Single(before.Actors, actor => actor.SessionId == secondarySession.Value);
        EnemyCombatant combatant = new(
            () => secondaryActor.Position + (Vector3.UnitX * 1.5f),
            events,
            actorId: 4002,
            maxHealth: 100,
            attackDamage: 10,
            attackIntervalSeconds: 1f);
        EnemyActor enemy = new(
            combatant,
            secondaryActor.Position + (Vector3.UnitX * 1.5f),
            aggroRange: 10f,
            attackRange: 1.35f,
            moveSpeed: 3f,
            displayName: "Wolf");
        runtime.Enemies.Add(enemy);

        IReadOnlyList<AuthoritativeActionResult> results = host.AdvanceTick(
            serverTick: 1,
            SimulationClock.FixedDeltaSeconds,
            [
                new PlayerCommandFrame(
                    secondarySession,
                    1,
                    new PlayerModeSwitchIntent(PlayerInteractionMode.Combat),
                    PlayerMoveIntent.None,
                    new PlayerLookIntent(0f, 0f, Vector3.UnitX),
                    PrimaryUsePressed: true)
            ],
            CreatePlayerTuning(),
            allowGameplaySimulation: false);

        AuthoritativeActionResult result = Assert.Single(results);
        CombatResolutionResult payload = Assert.IsType<CombatResolutionResult>(result.Payload);

        Assert.True(result.Accepted);
        Assert.Equal(secondarySession.Value, result.SessionId);
        Assert.Equal(4002, payload.TargetActorId);
        Assert.True(payload.DamageApplied > 0);
        Assert.True(enemy.Combatant.Health.CurrentHealth < enemy.Combatant.Health.MaxHealth);
    }

    [Fact]
    public void AdvanceTick_SecondaryRemotePrimaryUseBuilding_PlacesCube()
    {
        SceneEventHub events = new();
        using WorldSessionRuntime runtime = new(12, new Vector3(5f, 1.5f, 5f), events);
        using RuntimeAuthoritativeSessionHost host = new(runtime);
        PlayerSessionId primarySession = new("session-a");
        PlayerSessionId secondarySession = new("session-b");
        host.JoinSession(primarySession.Value, "Host Player");
        host.JoinSession(secondarySession.Value, "Second Player");

        IReadOnlyList<AuthoritativeActionResult> results = host.AdvanceTick(
            serverTick: 1,
            SimulationClock.FixedDeltaSeconds,
            [
                new PlayerCommandFrame(
                    secondarySession,
                    1,
                    new PlayerModeSwitchIntent(PlayerInteractionMode.Building),
                    PlayerMoveIntent.None,
                    new PlayerLookIntent(0f, 0f, Vector3.UnitX),
                    PrimaryUsePressed: true)
            ],
            CreatePlayerTuning(),
            allowGameplaySimulation: false);

        AuthoritativeActionResult result = Assert.Single(results);
        WorldMutationResult payload = Assert.IsType<WorldMutationResult>(result.Payload);

        Assert.True(result.Accepted);
        Assert.Equal(secondarySession.Value, result.SessionId);
        Assert.Equal(WorldMutationKind.CubePlaced, payload.Kind);
        Assert.True(payload.Applied);
        Assert.NotNull(runtime.Map.TryGetCubeAtCell(
            (int)payload.CellPosition.X,
            (int)payload.CellPosition.Y,
            (int)payload.CellPosition.Z));
    }

    [Fact]
    public void JoinSession_LateJoinerReceivesInitialActorSnapshotsForAlreadyJoinedPlayers()
    {
        SceneEventHub events = new();
        using WorldSessionRuntime runtime = new(10, new Vector3(5f, 1.5f, 5f), events);
        using RuntimeAuthoritativeSessionHost host = new(runtime);

        host.JoinSession("session-a", "Host Player");
        JoinAcceptedMessage accepted = host.JoinSession("session-b", "Second Player");

        Assert.Contains(
            accepted.EffectiveInitialActors,
            actor => actor.Kind == CreatureKind.Player &&
                actor.SessionId == "session-a" &&
                actor.DisplayName == "Host Player");
        Assert.Contains(
            accepted.EffectiveInitialActors,
            actor => actor.Kind == CreatureKind.Player &&
                actor.SessionId == "session-b" &&
                actor.DisplayName == "Second Player");
    }

    [Fact]
    public void ProcessAction_PlayerStatsUpdateOnlyChangesActingPlayerStats()
    {
        SceneEventHub events = new();
        using WorldSessionRuntime runtime = new(10, new Vector3(5f, 1.5f, 5f), events);
        using RuntimeAuthoritativeSessionHost host = new(runtime);
        PlayerSessionId hostSession = new("session-a");
        PlayerSessionId secondSession = new("session-b");
        host.JoinSession(hostSession.Value, "Host Player");
        host.JoinSession(secondSession.Value, "Second Player");

        AuthoritativeActionResult result = host.ProcessAction(new PlayerActionMessage(
            hostSession,
            1,
            new PlayerStatsUpdateIntent(new PlayerCombatStatsSnapshot(22, 4.5f, 16))));

        ServerSnapshotEnvelope snapshot = host.ExportSnapshot();
        ActorSnapshot hostActor = Assert.Single(snapshot.Actors, actor => actor.SessionId == hostSession.Value);
        ActorSnapshot secondActor = Assert.Single(snapshot.Actors, actor => actor.SessionId == secondSession.Value);

        Assert.True(result.Accepted);
        Assert.Equal(22, hostActor.CombatStats?.Strength);
        Assert.Equal(4.5f, hostActor.CombatStats?.AttackSpeed);
        Assert.Equal(16, hostActor.CombatStats?.Vitality);
        Assert.Equal(10, secondActor.CombatStats?.Strength);
        Assert.Equal(2f, secondActor.CombatStats?.AttackSpeed);
        Assert.Equal(10, secondActor.CombatStats?.Vitality);
    }

    [Fact]
    public void JoinSession_DoesNotDrainPendingWorldMutationsForExistingClients()
    {
        SceneEventHub events = new();
        using WorldSessionRuntime runtime = new(10, new Vector3(5f, 1.5f, 5f), events);
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
        using WorldSessionRuntime runtime = new(10, new Vector3(5f, 1.5f, 5f), events);
        using RuntimeAuthoritativeSessionHost host = new(runtime);

        host.JoinSession("session-a", "Player A");

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => host.JoinSession("session-b", "Player A"));

        Assert.Contains("already claimed", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReleaseConnection_Allows_Fresh_Session_To_Rejoin_Same_DisplayName()
    {
        using DedicatedServerHost host = DedicatedServerHost.CreateRuntimeAttached();

        JoinAcceptedMessage firstJoin = Assert.IsType<JoinAcceptedMessage>(Assert.Single(host.HandleClientMessage(
            "connection-1",
            new ClientJoinRequestMessage("session-a", "Player A"))));
        host.ReleaseConnection("connection-1");
        JoinAcceptedMessage secondJoin = Assert.IsType<JoinAcceptedMessage>(Assert.Single(host.HandleClientMessage(
            "connection-2",
            new ClientJoinRequestMessage("session-b", "Player A"))));
        ServerSnapshotEnvelope snapshot = host.CreateSnapshotMessage().Snapshot;

        Assert.Equal("session-a", firstJoin.SessionId);
        Assert.Equal("session-b", secondJoin.SessionId);
        Assert.Contains(secondJoin.EffectiveRoster, player => player.SessionId == "session-b");
        Assert.DoesNotContain(secondJoin.EffectiveRoster, player => player.SessionId == "session-a");
        Assert.Contains(snapshot.Actors, actor => actor.SessionId == "session-b");
        Assert.DoesNotContain(snapshot.Actors, actor => actor.SessionId == "session-a");
    }

    [Fact]
    public void AdvanceTick_TracksInteractionModePerSessionInsteadOfGlobally()
    {
        SceneEventHub events = new();
        using WorldSessionRuntime runtime = new(10, new Vector3(5f, 1.5f, 5f), events);
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
                Level: 1,
                Experience: 27),
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
            playerProfiles: playerProfiles,
            worldPersistence: worldPersistence);
        using RuntimeAuthoritativeSessionHost host = new(runtime);

        host.JoinSession("session-a", "Player A");

        Assert.Equal(PlayerInteractionMode.Combat, runtime.InteractionMode);
        Assert.Equal(17, runtime.Player.Combatant.BaseStats.Strength);
        Assert.Equal(3.5f, runtime.Player.Combatant.BaseStats.AttackSpeed);
        Assert.Equal(14, runtime.Player.Combatant.BaseStats.Vitality);
        Assert.Equal(1, runtime.Player.Combatant.Level);
        Assert.Equal(27, runtime.Player.Combatant.Experience);
        Assert.Equal(19, runtime.Player.Combatant.Stats.Strength);
        Assert.Equal(3.75f, runtime.Player.Combatant.Stats.AttackSpeed);
        Assert.Equal(14, runtime.Player.Combatant.Stats.Vitality);
        Assert.Equal(73, runtime.Player.Combatant.Health.CurrentHealth);
        Assert.Equal(120, runtime.Player.Combatant.Health.MaxHealth);
        Assert.Equal(11f, runtime.Player.GetWorldPosition().X, 3);
        Assert.Equal(7f, runtime.Player.GetWorldPosition().Z, 3);
        Assert.Equal(1f, runtime.Player.Camera.Front.X, 3);
        Assert.Equal(0f, runtime.Player.Camera.Front.Z, 3);
    }

    [Fact]
    public void Constructor_RestoresPersistedCubeFracturePiecesIntoAuthoritativeMap()
    {
        InMemoryPlayerProfileRepository playerProfiles = new();
        InMemoryWorldPersistenceRepository worldPersistence = new();
        GeometryCellKey cell = new(4, 1, 4);
        PersistedCubeFracturePiece persistedPiece = new(
            ObjectId: ObjectId.None,
            OriginObjectId: new ObjectId(88),
            PieceIndex: 0,
            OwnerPlayerName: "builder-a",
            ParentCell: new Vector3(cell.X, cell.Y, cell.Z),
            Center: new Vector3(4.25f, 1.2f, 4.35f),
            SideLength: 0.2f,
            MaterialId: GeometryMaterialId.Sand,
            CreatedUtc: DateTime.UtcNow,
            UpdatedUtc: DateTime.UtcNow);
        worldPersistence.SaveCubeFracturePiece(persistedPiece);
        SceneEventHub events = new();

        using WorldSessionRuntime runtime = new(
            10,
            new Vector3(5f, 1.5f, 5f),
            events,
            playerProfiles: playerProfiles,
            worldPersistence: worldPersistence,
            // The server runtime's map is headless by default; this test reads its render export.
            headlessMap: false);

        GeometryRenderExport export = runtime.Map.CaptureGeometryRenderExport(new Vector3(4.5f, 1.5f, 4.5f), 4f);
        GeometryRenderBlockDescriptor block = Assert.Single(export.DynamicBlocks, block => block.CellKey == cell);
        Assert.Equal(persistedPiece.Center, block.Center);
        Assert.Equal(persistedPiece.SideLength, block.SideLength);
        Assert.Equal(GeometryMaterialId.Sand, block.MaterialId);
    }

    [Fact]
    public void ProcessAction_FatalCubeHit_PersistsFracturePiecesAndDeletesPlacedCube()
    {
        InMemoryPlayerProfileRepository playerProfiles = new();
        InMemoryWorldPersistenceRepository worldPersistence = new();
        SceneEventHub events = new();
        using WorldSessionRuntime runtime = new(
            10,
            new Vector3(5f, 1.5f, 5f),
            events,
            playerProfiles: playerProfiles,
            worldPersistence: worldPersistence);
        using RuntimeAuthoritativeSessionHost host = new(runtime);
        PlayerSessionId sessionId = new("session-a");
        host.JoinSession(sessionId.Value, "Player A");
        runtime.Enemies.Clear();
        AuthoritativeActionResult buildResult = host.ProcessAction(new PlayerActionMessage(
            sessionId,
            1,
            new BuildUseIntent(new Vector3(7f, 1f, 7f))));
        PersistedPlacedCube persistedCube = Assert.Single(worldPersistence.LoadPlacedCubes());
        host.ProcessAction(new PlayerActionMessage(
            sessionId,
            2,
            new PlayerStatsUpdateIntent(new PlayerCombatStatsSnapshot(Strength: PlayerCombatStats.MaxStrength, AttackSpeed: 2f, Vitality: 10))));
        runtime.SetAuthoritativeInteractionMode(PlayerInteractionMode.Combat);
        runtime.Player.SetWorldPosition(new Vector3(5f, 1.5f, 7.5f));
        runtime.Player.SetFacing(Vector3.UnitX);

        AuthoritativeActionResult combatResult = host.ProcessAction(new PlayerActionMessage(
            sessionId,
            3,
            new CombatUseIntent(new Vector3(5f, 1.5f, 7.5f), Vector3.UnitX)));

        Assert.True(buildResult.Accepted);
        Assert.True(combatResult.Accepted);
        CombatResolutionResult resolution = Assert.IsType<CombatResolutionResult>(combatResult.Payload);
        Assert.True(resolution.TargetDied);
        Assert.Empty(worldPersistence.LoadPlacedCubes());
        PersistedCubeFracturePiece[] pieces = worldPersistence.LoadCubeFracturePieces().ToArray();
        Assert.InRange(pieces.Length, 5, 7);
        Assert.All(pieces, piece =>
        {
            Assert.Equal(persistedCube.ObjectId.AsInt(), piece.OriginObjectId.AsInt());
            Assert.Equal("Player A", piece.OwnerPlayerName);
            Assert.Equal(persistedCube.CellPosition, piece.ParentCell);
            Assert.Equal(persistedCube.MaterialId, piece.MaterialId);
        });
    }

    [Fact]
    public void PersistCheckpoint_PersistsMovedPlacedCubeCenter()
    {
        InMemoryPlayerProfileRepository playerProfiles = new();
        InMemoryWorldPersistenceRepository worldPersistence = new();
        SceneEventHub events = new();
        using WorldSessionRuntime runtime = new(
            10,
            new Vector3(5f, 1.5f, 5f),
            events,
            playerProfiles: playerProfiles,
            worldPersistence: worldPersistence);
        using RuntimeAuthoritativeSessionHost host = new(runtime);
        PlayerSessionId sessionId = new("session-a");
        host.JoinSession(sessionId.Value, "Player A");

        AuthoritativeActionResult buildResult = host.ProcessAction(new PlayerActionMessage(
            sessionId,
            1,
            new BuildUseIntent(new Vector3(7f, 1f, 7f))));
        Assert.True(buildResult.Accepted);
        Cube cube = Assert.IsType<Cube>(runtime.Map.TryGetCubeAtCell(7, 1, 7));
        Vector3 movedCenter = new(7.73f, 1.5f, 7.18f);
        runtime.Map.UpdateCubePosition(cube, movedCenter, syncPhysics: false);

        runtime.PersistCheckpoint();

        PersistedPlacedCube persistedCube = Assert.Single(worldPersistence.LoadPlacedCubes());
        Assert.Equal(new Vector3(7f, 1f, 7f), persistedCube.CellPosition);
        Assert.True(persistedCube.CenterPosition.HasValue);
        Assert.Equal(movedCenter, persistedCube.CenterPosition.Value);
    }

    [Fact]
    public void ProcessAction_MovedPlacedCube_RemainsDamageable_When_RegistryLink_Is_Missing()
    {
        SceneEventHub events = new();
        using WorldSessionRuntime runtime = new(12, new Vector3(5f, 1.5f, 5f), events);
        using RuntimeAuthoritativeSessionHost host = new(runtime);
        PlayerSessionId sessionId = new("session-a");
        host.JoinSession(sessionId.Value, "Player A");
        runtime.Enemies.Clear();

        AuthoritativeActionResult buildResult = host.ProcessAction(new PlayerActionMessage(
            sessionId,
            1,
            new BuildUseIntent(new Vector3(7f, 1f, 7f))));
        Assert.True(buildResult.Accepted);

        Cube cube = Assert.IsType<Cube>(runtime.Map.TryGetCubeAtCell(7, 1, 7));
        Vector3 movedCenter = new(8.25f, 1.5f, 7.5f);
        runtime.Map.UpdateCubePosition(cube, movedCenter, syncPhysics: false);
        Assert.Same(cube, runtime.Map.TryGetCubeAtCell(8, 1, 7));

        WorldObjectRegistry registry = GetWorldObjects(runtime);
        Assert.True(registry.Unregister(cube));

        host.ProcessAction(new PlayerActionMessage(
            sessionId,
            2,
            new PlayerStatsUpdateIntent(new PlayerCombatStatsSnapshot(Strength: PlayerCombatStats.MaxStrength, AttackSpeed: 2f, Vitality: 10))));
        runtime.SetAuthoritativeInteractionMode(PlayerInteractionMode.Combat);

        Vector3 origin = new(8.25f, 1.5f, 10.5f);
        runtime.Player.SetWorldPosition(origin);
        runtime.Player.SetFacing(-Vector3.UnitZ);
        Assert.NotNull(CombatUseResolver.TryResolveDestructibleCubeHit(
            runtime.Map,
            registry,
            origin,
            -Vector3.UnitZ,
            CombatUseResolver.DefaultHitRange));
        AuthoritativeActionResult combatResult = host.ProcessAction(new PlayerActionMessage(
            sessionId,
            3,
            new CombatUseIntent(origin, -Vector3.UnitZ)));

        Assert.True(combatResult.Accepted);
        CombatResolutionResult resolution = Assert.IsType<CombatResolutionResult>(combatResult.Payload);
        Assert.True(
            resolution.DamageApplied > 0,
            $"Expected moved cube damage, got actor={resolution.TargetActorId}, kind={resolution.TargetKind}, damage={resolution.DamageApplied}, died={resolution.TargetDied}, health={resolution.CurrentHealth}/{resolution.MaxHealth}.");
        Assert.True(resolution.TargetDied);
        Assert.Contains(
            runtime.Map.CaptureFracturePieces(),
            piece => piece.Cell == new GeometryCellKey(8, 1, 7));
    }

    [Fact]
    public void ProcessAction_MovedPlacedCube_PersistsOriginalOwnership_When_RegistryLink_Is_Missing()
    {
        InMemoryPlayerProfileRepository playerProfiles = new();
        InMemoryWorldPersistenceRepository worldPersistence = new();
        SceneEventHub events = new();
        using WorldSessionRuntime runtime = new(
            12,
            new Vector3(5f, 1.5f, 5f),
            events,
            playerProfiles: playerProfiles,
            worldPersistence: worldPersistence);
        using RuntimeAuthoritativeSessionHost host = new(runtime);
        PlayerSessionId sessionId = new("session-a");
        host.JoinSession(sessionId.Value, "Player A");
        runtime.Enemies.Clear();

        AuthoritativeActionResult buildResult = host.ProcessAction(new PlayerActionMessage(
            sessionId,
            1,
            new BuildUseIntent(new Vector3(7f, 1f, 7f))));
        Assert.True(buildResult.Accepted);
        PersistedPlacedCube persistedCube = Assert.Single(worldPersistence.LoadPlacedCubes());

        Cube cube = Assert.IsType<Cube>(runtime.Map.TryGetCubeAtCell(7, 1, 7));
        Vector3 movedCenter = new(8.25f, 1.5f, 7.5f);
        runtime.Map.UpdateCubePosition(cube, movedCenter, syncPhysics: false);
        Assert.Same(cube, runtime.Map.TryGetCubeAtCell(8, 1, 7));

        WorldObjectRegistry registry = GetWorldObjects(runtime);
        Assert.True(registry.Unregister(cube));

        host.ProcessAction(new PlayerActionMessage(
            sessionId,
            2,
            new PlayerStatsUpdateIntent(new PlayerCombatStatsSnapshot(Strength: PlayerCombatStats.MaxStrength, AttackSpeed: 2f, Vitality: 10))));
        runtime.SetAuthoritativeInteractionMode(PlayerInteractionMode.Combat);

        Vector3 origin = new(8.25f, 1.5f, 10.5f);
        runtime.Player.SetWorldPosition(origin);
        runtime.Player.SetFacing(-Vector3.UnitZ);

        AuthoritativeActionResult combatResult = host.ProcessAction(new PlayerActionMessage(
            sessionId,
            3,
            new CombatUseIntent(origin, -Vector3.UnitZ)));

        Assert.True(combatResult.Accepted);
        CombatResolutionResult resolution = Assert.IsType<CombatResolutionResult>(combatResult.Payload);
        Assert.True(resolution.TargetDied);
        Assert.Empty(worldPersistence.LoadPlacedCubes());

        PersistedCubeFracturePiece[] pieces = worldPersistence.LoadCubeFracturePieces()
            .OrderBy(piece => piece.PieceIndex)
            .ToArray();
        Assert.NotEmpty(pieces);
        Assert.All(pieces, piece =>
        {
            Assert.Equal(persistedCube.ObjectId.AsInt(), piece.OriginObjectId.AsInt());
            Assert.Equal("Player A", piece.OwnerPlayerName);
            Assert.Equal(new Vector3(8f, 1f, 7f), piece.ParentCell);
        });
    }

    [Fact]
    public void ProcessAction_MovedPlacedCube_DoesNotStealNearbyCubeIdentity_When_RegistryLink_Is_Missing()
    {
        InMemoryPlayerProfileRepository playerProfiles = new();
        InMemoryWorldPersistenceRepository worldPersistence = new();
        SceneEventHub events = new();
        using WorldSessionRuntime runtime = new(
            14,
            new Vector3(5f, 1.5f, 5f),
            events,
            playerProfiles: playerProfiles,
            worldPersistence: worldPersistence);
        using RuntimeAuthoritativeSessionHost host = new(runtime);
        PlayerSessionId sessionId = new("session-a");
        host.JoinSession(sessionId.Value, "Player A");
        runtime.Enemies.Clear();
        runtime.Player.SetWorldPosition(new Vector3(6.5f, 1.5f, 6.5f));

        Assert.True(host.ProcessAction(new PlayerActionMessage(
            sessionId,
            1,
            new BuildUseIntent(new Vector3(7f, 1f, 7f)))).Accepted);
        Assert.True(host.ProcessAction(new PlayerActionMessage(
            sessionId,
            2,
            new BuildUseIntent(new Vector3(8f, 1f, 6f)))).Accepted);

        PersistedPlacedCube[] beforeCombat = worldPersistence.LoadPlacedCubes()
            .OrderBy(cube => cube.ObjectId.AsInt())
            .ToArray();
        Assert.Equal(2, beforeCombat.Length);
        PersistedPlacedCube movedCubePersisted = beforeCombat[0];
        PersistedPlacedCube nearbyCubePersisted = beforeCombat[1];

        Cube movedCube = Assert.IsType<Cube>(runtime.Map.TryGetCubeAtCell(7, 1, 7));
        runtime.Map.UpdateCubePosition(movedCube, new Vector3(8.8f, 1.5f, 7.5f), syncPhysics: false);
        Assert.Same(movedCube, runtime.Map.TryGetCubeAtCell(8, 1, 7));

        WorldObjectRegistry registry = GetWorldObjects(runtime);
        Assert.True(registry.Unregister(movedCube));

        host.ProcessAction(new PlayerActionMessage(
            sessionId,
            3,
            new PlayerStatsUpdateIntent(new PlayerCombatStatsSnapshot(Strength: PlayerCombatStats.MaxStrength, AttackSpeed: 2f, Vitality: 10))));
        runtime.SetAuthoritativeInteractionMode(PlayerInteractionMode.Combat);

        Vector3 origin = new(8.8f, 1.5f, 10.5f);
        runtime.Player.SetWorldPosition(origin);
        runtime.Player.SetFacing(-Vector3.UnitZ);

        AuthoritativeActionResult combatResult = host.ProcessAction(new PlayerActionMessage(
            sessionId,
            4,
            new CombatUseIntent(origin, -Vector3.UnitZ)));

        Assert.True(combatResult.Accepted);
        CombatResolutionResult resolution = Assert.IsType<CombatResolutionResult>(combatResult.Payload);
        Assert.True(resolution.TargetDied);

        PersistedPlacedCube remainingCube = Assert.Single(worldPersistence.LoadPlacedCubes());
        Assert.Equal(nearbyCubePersisted.ObjectId.AsInt(), remainingCube.ObjectId.AsInt());
        Assert.Equal(nearbyCubePersisted.CellPosition, remainingCube.CellPosition);

        PersistedCubeFracturePiece[] fracturePieces = worldPersistence.LoadCubeFracturePieces().ToArray();
        Assert.NotEmpty(fracturePieces);
        Assert.All(fracturePieces, piece => Assert.NotEqual(nearbyCubePersisted.ObjectId.AsInt(), piece.OriginObjectId.AsInt()));
        Assert.Contains(fracturePieces, piece => piece.OriginObjectId.AsInt() == movedCubePersisted.ObjectId.AsInt());
    }

    [Fact]
    public void AdvanceAuthoritativeTick_PhysicsMovedCube_QueuesAuthoritativeMoveMutation()
    {
        SceneEventHub events = new();
        using WorldSessionRuntime runtime = new(12, new Vector3(5f, 1.5f, 5f), events);
        runtime.Enemies.Clear();
        AuthoritativeActionResult buildResult = runtime.ProcessAuthoritativeAction(new PlayerActionMessage(
            PlayerSessionId.LocalDefault,
            1,
            new BuildUseIntent(new Vector3(7f, 1f, 7f))));
        Assert.True(buildResult.Accepted);
        Cube cube = Assert.IsType<Cube>(runtime.Map.TryGetCubeAtCell(7, 1, 7));
        BulletSharp.RigidBody body = Assert.Single(
            runtime.Physics.DynamicMapCubeBodies,
            candidate => candidate.UserObject is ValueTuple<Cube, Vector3> cubeBody &&
                ReferenceEquals(cubeBody.Item1, cube));
        Vector3 authoritativeCenter = new(8.18f, 1.5f, 7.44f);
        BulletSharp.Math.Matrix transform = BulletSharp.Math.Matrix.Translation(
            new BulletSharp.Math.Vector3(authoritativeCenter.X, authoritativeCenter.Y, authoritativeCenter.Z));
        body.WorldTransform = transform;
        body.MotionState.SetWorldTransform(ref transform);

        runtime.AdvanceAuthoritativeTick(
            serverTick: 2,
            SimulationClock.FixedDeltaSeconds,
            new PlayerCommandFrame(
                PlayerSessionId.LocalDefault,
                Sequence: 1,
                PlayerModeSwitchIntent.None,
                new PlayerMoveIntent(ForwardAxis: 0f, StrafeAxis: 0f),
                PlayerLookIntent.None,
                PrimaryUsePressed: false),
            CreatePlayerTuning(),
            allowGameplaySimulation: false);

        ServerSnapshotEnvelope snapshot = runtime.ExportServerSnapshotEnvelope();
        WorldMutationDelta move = Assert.Single(snapshot.WorldMutations, mutation => mutation.Kind == WorldMutationKind.CubeMoved);
        Assert.Equal(new Vector3(7f, 1f, 7f), move.CellPosition);
        Assert.Equal(new Vector3(8f, 1f, 7f), move.TargetCellPosition);
        Assert.Equal(authoritativeCenter, move.CenterPosition);
    }

    [Fact]
    public void JoinSession_Seeds_StarterInventory_For_FirstTimePlayer()
    {
        SceneEventHub events = new();
        using WorldSessionRuntime runtime = new(10, new Vector3(5f, 1.5f, 5f), events);
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
        using WorldSessionRuntime runtime = new(10, new Vector3(5f, 1.5f, 5f), events);
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
    public void JoinSession_RejoiningExistingSession_Allows_Fresh_Action_Sequences()
    {
        SceneEventHub events = new();
        using WorldSessionRuntime runtime = new(10, new Vector3(5f, 1.5f, 5f), events);
        using RuntimeAuthoritativeSessionHost host = new(runtime);
        host.JoinSession("session-a", "Player A");

        AuthoritativeActionResult firstAction = host.ProcessAction(new PlayerActionMessage(
            new PlayerSessionId("session-a"),
            5,
            new InventoryEquipIntent(1)));
        JoinAcceptedMessage rejoined = host.JoinSession("session-a", "Player A");
        AuthoritativeActionResult resumedAction = host.ProcessAction(new PlayerActionMessage(
            new PlayerSessionId("session-a"),
            1,
            new InventoryEquipIntent(0)));

        Assert.True(firstAction.Accepted);
        Assert.Equal("session-a", rejoined.SessionId);
        Assert.True(resumedAction.Accepted);
        InventoryChangedResult payload = Assert.IsType<InventoryChangedResult>(resumedAction.Payload);
        Assert.Equal("Builder Hammer", payload.Inventory.EquippedSlot?.Item?.DisplayName);
    }

    [Fact]
    public void ProcessAction_InventoryEquip_Recomputes_EffectiveStats_But_Persists_BaseStats()
    {
        InMemoryPlayerProfileRepository playerProfiles = new();
        InMemoryWorldPersistenceRepository worldPersistence = new();
        SceneEventHub events = new();
        using WorldSessionRuntime runtime = new(
            10,
            new Vector3(5f, 1.5f, 5f),
            events,
            playerProfiles: playerProfiles,
            worldPersistence: worldPersistence);
        using RuntimeAuthoritativeSessionHost host = new(runtime);
        host.JoinSession("session-a", "Player A");

        host.ProcessAction(new PlayerActionMessage(
            new PlayerSessionId("session-a"),
            1,
            new PlayerStatsUpdateIntent(new PlayerCombatStatsSnapshot(12, 2.5f, 11))));
        host.ProcessAction(new PlayerActionMessage(
            new PlayerSessionId("session-a"),
            2,
            new InventoryEquipIntent(1)));

        Assert.Equal(12, runtime.Player.Combatant.BaseStats.Strength);
        Assert.Equal(2.5f, runtime.Player.Combatant.BaseStats.AttackSpeed, 3);
        Assert.Equal(11, runtime.Player.Combatant.BaseStats.Vitality);
        Assert.Equal(16, runtime.Player.Combatant.Stats.Strength);
        Assert.Equal(4.0f, runtime.Player.Combatant.Stats.AttackSpeed, 3);
        Assert.Equal(11, runtime.Player.Combatant.Stats.Vitality);

        PersistedPlayerProfile? persisted = playerProfiles.TryGet("Player A");
        Assert.True(persisted.HasValue);
        PersistedPlayerProfile profile = persisted.Value;
        Assert.Equal(12, profile.Stats.Strength);
        Assert.Equal(2.5f, profile.Stats.AttackSpeed, 3);
        Assert.Equal(11, profile.Stats.Vitality);
    }

    [Fact]
    public void JoinSession_UsesInjectedCombatFormulaServiceForDerivedHealthAndAttackCadence()
    {
        InMemoryPlayerProfileRepository playerProfiles = new();
        InMemoryWorldPersistenceRepository worldPersistence = new();
        CombatFormulaService formulas = CombatFormulaService.CreateForDefinitions(
            new[]
            {
                new CombatFormulaDefinition("player.min_damage", "5 + strength * 2", "test", true, DateTime.UtcNow),
                new CombatFormulaDefinition("player.max_damage", "10 + strength * 4", "test", true, DateTime.UtcNow),
                new CombatFormulaDefinition("player.max_health", "10 + vitality * 10", "test", true, DateTime.UtcNow),
                new CombatFormulaDefinition("player.critical_damage", "max_damage * 1.5", "test", true, DateTime.UtcNow),
                new CombatFormulaDefinition("player.critical_chance", "0.15", "test", true, DateTime.UtcNow),
                new CombatFormulaDefinition("player.attack_interval_seconds", "2.0 / attack_speed", "test", true, DateTime.UtcNow),
                new CombatFormulaDefinition("player.experience_to_next_level", "1000 + level * 23", "test", true, DateTime.UtcNow),
                new CombatFormulaDefinition("enemy.experience_reward", "enemy_level * 14", "test", true, DateTime.UtcNow)
            });
        SceneEventHub events = new();
        using WorldSessionRuntime runtime = new(
            10,
            new Vector3(5f, 1.5f, 5f),
            events,
            playerProfiles: playerProfiles,
            worldPersistence: worldPersistence,
            combatFormulaService: formulas);
        using RuntimeAuthoritativeSessionHost host = new(runtime);

        host.JoinSession("session-a", "Player A");

        Assert.Equal(110, runtime.Player.Combatant.Health.MaxHealth);
        Assert.Equal(2.0f / 2.25f, runtime.Player.Combatant.DerivedStats.AttackIntervalSeconds, 3);
    }

    [Fact]
    public void ProcessAction_FatalEnemyHit_AwardsExperienceAndPersistsProgression()
    {
        InMemoryPlayerProfileRepository playerProfiles = new();
        InMemoryWorldPersistenceRepository worldPersistence = new();
        CombatFormulaService formulas = CombatFormulaService.CreateForDefinitions(
            new[]
            {
                new CombatFormulaDefinition("player.min_damage", "5 + strength * 2", "test", true, DateTime.UtcNow),
                new CombatFormulaDefinition("player.max_damage", "10 + strength * 4", "test", true, DateTime.UtcNow),
                new CombatFormulaDefinition("player.max_health", "50 + vitality * 5", "test", true, DateTime.UtcNow),
                new CombatFormulaDefinition("player.critical_damage", "max_damage * 1.5", "test", true, DateTime.UtcNow),
                new CombatFormulaDefinition("player.critical_chance", "0.15", "test", true, DateTime.UtcNow),
                new CombatFormulaDefinition("player.attack_interval_seconds", "1.0 / attack_speed", "test", true, DateTime.UtcNow),
                new CombatFormulaDefinition("player.experience_to_next_level", "1000 + level * 23", "test", true, DateTime.UtcNow),
                new CombatFormulaDefinition("enemy.experience_reward", "enemy_level * 14", "test", true, DateTime.UtcNow)
            });
        SceneEventHub events = new();
        using WorldSessionRuntime runtime = new(
            10,
            new Vector3(5f, 1.5f, 5f),
            events,
            playerProfiles: playerProfiles,
            worldPersistence: worldPersistence,
            combatFormulaService: formulas);
        using RuntimeAuthoritativeSessionHost host = new(runtime);
        host.JoinSession("session-a", "Player A");
        runtime.SetAuthoritativeInteractionMode(PlayerInteractionMode.Combat);
        runtime.Player.SetFacing((new Vector3(5.5f, 1f, 2.5f) - runtime.Player.Camera.Position).Normalized());
        runtime.Enemies.ActiveActors[0].Combatant.ApplyDamage(99);

        AuthoritativeActionResult result = host.ProcessAction(new PlayerActionMessage(
            new PlayerSessionId("session-a"),
            3,
            new CombatUseIntent(Vector3.Zero, -Vector3.UnitZ)));

        Assert.True(result.Accepted);
        CombatResolutionResult resolution = Assert.IsType<CombatResolutionResult>(result.Payload);
        Assert.True(resolution.TargetDied);
        Assert.Equal(14, runtime.Player.Combatant.Experience);
        Assert.Equal(1, runtime.Player.Combatant.Level);
        PersistedPlayerProfile? profile = playerProfiles.TryGet("Player A");
        Assert.True(profile.HasValue);
        Assert.Equal(14, profile.Value.Stats.Experience);
        Assert.Equal(1, profile.Value.Stats.Level);
    }

    [Fact]
    public void ProcessAction_FatalEnemyHit_CanLevelUpMoreThanOnceFromFormulaDrivenThresholds()
    {
        InMemoryPlayerProfileRepository playerProfiles = new();
        InMemoryWorldPersistenceRepository worldPersistence = new();
        CombatFormulaService formulas = CombatFormulaService.CreateForDefinitions(
            new[]
            {
                new CombatFormulaDefinition("player.min_damage", "5 + strength * 2", "test", true, DateTime.UtcNow),
                new CombatFormulaDefinition("player.max_damage", "10 + strength * 4", "test", true, DateTime.UtcNow),
                new CombatFormulaDefinition("player.max_health", "50 + vitality * 5", "test", true, DateTime.UtcNow),
                new CombatFormulaDefinition("player.critical_damage", "max_damage * 1.5", "test", true, DateTime.UtcNow),
                new CombatFormulaDefinition("player.critical_chance", "0.15", "test", true, DateTime.UtcNow),
                new CombatFormulaDefinition("player.attack_interval_seconds", "1.0 / attack_speed", "test", true, DateTime.UtcNow),
                new CombatFormulaDefinition("player.experience_to_next_level", "level * 10", "test", true, DateTime.UtcNow),
                new CombatFormulaDefinition("enemy.experience_reward", "35", "test", true, DateTime.UtcNow)
            });
        SceneEventHub events = new();
        using WorldSessionRuntime runtime = new(
            10,
            new Vector3(5f, 1.5f, 5f),
            events,
            playerProfiles: playerProfiles,
            worldPersistence: worldPersistence,
            combatFormulaService: formulas);
        using RuntimeAuthoritativeSessionHost host = new(runtime);
        host.JoinSession("session-a", "Player A");
        runtime.SetAuthoritativeInteractionMode(PlayerInteractionMode.Combat);
        runtime.Player.SetFacing((new Vector3(5.5f, 1f, 2.5f) - runtime.Player.Camera.Position).Normalized());
        runtime.Enemies.ActiveActors[0].Combatant.ApplyDamage(99);

        host.ProcessAction(new PlayerActionMessage(
            new PlayerSessionId("session-a"),
            3,
            new CombatUseIntent(Vector3.Zero, -Vector3.UnitZ)));

        Assert.Equal(3, runtime.Player.Combatant.Level);
        Assert.Equal(5, runtime.Player.Combatant.Experience);
    }

    [Fact]
    public void AdvanceTick_PrimaryRemoteCommandMovesForwardAlongClientFacing()
    {
        SceneEventHub events = new();
        using WorldSessionRuntime runtime = new(10, new Vector3(5f, 1.5f, 5f), events);
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
        using WorldSessionRuntime runtime = new(10, new Vector3(5f, 1.5f, 5f), events);
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
        using WorldSessionRuntime runtime = new(10, new Vector3(5f, 1.5f, 5f), events);
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

    private static WorldObjectRegistry GetWorldObjects(WorldSessionRuntime runtime)
    {
        FieldInfo field = typeof(WorldSessionRuntime).GetField(
            "_worldObjects",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (WorldObjectRegistry)field.GetValue(runtime)!;
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
        private readonly Dictionary<int, PersistedPlacedCube> _placedCubes = [];
        private readonly List<PersistedCubeFracturePiece> _fracturePieces = [];

        public PersistedPlayerWorldState? TryGetPlayerWorldState(string playerName)
        {
            return _players.TryGetValue(playerName, out PersistedPlayerWorldState state) ? state : null;
        }

        public void SavePlayerWorldState(PersistedPlayerWorldState state)
        {
            _players[state.PlayerName] = state;
        }

        public IReadOnlyList<PersistedPlacedCube> LoadPlacedCubes() => _placedCubes.Values.ToArray();

        public void SavePlacedCube(PersistedPlacedCube cube)
        {
            _placedCubes[cube.ObjectId.AsInt()] = cube;
        }

        public void DeletePlacedCube(ObjectId objectId)
        {
            _placedCubes.Remove(objectId.AsInt());
        }

        public IReadOnlyList<PersistedCubeFracturePiece> LoadCubeFracturePieces() => _fracturePieces.ToArray();

        public void SaveCubeFracturePiece(PersistedCubeFracturePiece piece)
        {
            _fracturePieces.RemoveAll(existing =>
                existing.OriginObjectId.AsInt() == piece.OriginObjectId.AsInt() &&
                existing.PieceIndex == piece.PieceIndex);
            _fracturePieces.Add(piece);
        }

        public void DeleteCubeFracturePieces(ObjectId originObjectId)
        {
            _fracturePieces.RemoveAll(piece => piece.OriginObjectId.AsInt() == originObjectId.AsInt());
        }

        public IReadOnlyList<PersistedEnemyInstance> LoadActiveEnemies() => Array.Empty<PersistedEnemyInstance>();

        public void SaveEnemyInstance(PersistedEnemyInstance enemy)
        {
        }

        public void DeleteEnemyInstance(ObjectId objectId)
        {
        }

        public long LoadNextObjectId() => 1;


        public void ReassignEnemyId(int oldEnemyId, int newEnemyId)
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

        public PersistedPlayerInventory LoadPlayerInventory(string playerName) => PersistedPlayerInventory.Empty;

        public long GetMaxItemInstanceId() => 0;

        public void SaveInventorySlot(PersistedInventorySlot slot)
        {
        }

        public void DeleteInventorySlots(string playerName)
        {
        }

        public void SaveStarterKitGrant(string playerName, int kitVersion)
        {
        }
    }
}
