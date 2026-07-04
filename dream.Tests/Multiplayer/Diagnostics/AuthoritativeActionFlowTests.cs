using OpenTK.Mathematics;
using Xunit;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Combat;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;

namespace Dream.Tests.Multiplayer.Diagnostics;


public class AuthoritativeActionFlowTests
{
    [Fact]
    public void PlayerActionHandler_WhenSessionNotJoined_ReturnsRejectedServerActionMessage()
    {
        ServerRoutingContext context = new("connection-1");
        PlayerActionHandler handler = new();
        PlayerActionMessage message = new(
            new PlayerSessionId("session-a"),
            4,
            new BuildUseIntent(new Vector3(2f, 1f, 2f)));

        handler.Handle(context, message);

        ServerActionMessage reply = Assert.IsType<ServerActionMessage>(Assert.Single(context.OutboundMessages));
        Assert.False(reply.Result.Accepted);
        Assert.NotNull(reply.Result.Rejection);
        Assert.Contains("join", reply.Result.Rejection?.Reason ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PlayerActionHandler_WhenJoinedButWithoutRuntime_ReturnsRejectedServerActionMessage()
    {
        ServerRoutingContext context = new("connection-1")
        {
            LastJoinRequest = new ClientJoinRequestMessage("session-a", "player-a")
        };
        PlayerActionHandler handler = new();
        PlayerActionMessage message = new(
            new PlayerSessionId("session-a"),
            7,
            new CombatUseIntent(new Vector3(0f, 1f, 0f), -Vector3.UnitZ));

        handler.Handle(context, message);

        ServerActionMessage reply = Assert.IsType<ServerActionMessage>(Assert.Single(context.OutboundMessages));
        Assert.False(reply.Result.Accepted);
        Assert.NotNull(reply.Result.Rejection);
        Assert.Contains("runtime", reply.Result.Rejection?.Reason ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PlayerActionHandler_WhenJoinedAndQueueAttached_EnqueuesActionInsteadOfReplyingImmediately()
    {
        RecordingPlayerActionQueue actionQueue = new();
        ServerRoutingContext context = new("connection-1", new FakeAuthoritativeSession(), actionQueue: actionQueue)
        {
            LastJoinRequest = new ClientJoinRequestMessage("session-a", "player-a")
        };
        PlayerActionHandler handler = new();
        PlayerActionMessage message = new(
            new PlayerSessionId("session-a"),
            7,
            new CombatUseIntent(new Vector3(0f, 1f, 0f), -Vector3.UnitZ));

        handler.Handle(context, message);

        Assert.Empty(context.OutboundMessages);
        QueuedPlayerAction queuedAction = Assert.Single(actionQueue.EnqueuedActions);
        Assert.Equal("session-a", queuedAction.SessionId.Value);
        Assert.Equal((ulong)7, queuedAction.Message.ActionSequence);
    }

    [Fact]
    public void ClientProtocolSession_ApplyServerActionMessage_CapturesRejectedActionResult()
    {
        ClientProtocolSession session = ClientProtocolSession.CreateDefault();
        AuthoritativeActionResult result = new(
            "session-a",
            9,
            21,
            Accepted: false,
            Payload: null,
            Rejection: new RejectedUseDelta("Rejected by authority"));

        session.ApplyServerMessage(new ServerActionMessage(result));

        Assert.NotNull(session.Context.LastActionResult);
        Assert.False(session.Context.LastActionResult?.Accepted);
        Assert.Equal((ulong)9, session.Context.LastActionResult?.ActionSequence);
    }

    private sealed class RecordingPlayerActionQueue : IPlayerActionQueue
    {
        public List<QueuedPlayerAction> EnqueuedActions { get; } = [];

        public QueuedPlayerAction Enqueue(PlayerActionMessage message)
        {
            QueuedPlayerAction queuedAction = new(
                (ulong)(EnqueuedActions.Count + 1),
                message.SessionId,
                message);
            EnqueuedActions.Add(queuedAction);
            return queuedAction;
        }

        public IReadOnlyList<QueuedPlayerAction> Drain(IEnumerable<PlayerSessionId> joinedSessionIds)
        {
            return [];
        }
    }

    private sealed class FakeAuthoritativeSession : IAuthoritativeSessionHost
    {
        public JoinAcceptedMessage JoinSession(string requestedSessionId, string displayName)
        {
            return new JoinAcceptedMessage(requestedSessionId, 101, 12, InitialWorld: null);
        }

        public AuthoritativeActionResult ProcessAction(PlayerActionMessage message)
        {
            return new AuthoritativeActionResult(
                message.SessionId.Value,
                message.ActionSequence,
                12,
                Accepted: true,
                Payload: null);
        }

        public ServerSnapshotEnvelope ExportSnapshot(bool includeWorldSnapshot = false)
        {
            return new ServerSnapshotEnvelope(
                12,
                new Dictionary<string, ulong>(),
                Array.Empty<ActorSnapshot>(),
                PlayerInteractionMode.Building,
                null,
                Array.Empty<WorldMutationDelta>(),
                Array.Empty<DamageEventDelta>());
        }
    }

    [Fact]
    public void ProcessAuthoritativeAction_AllowsSecondaryRemoteSessionToPlaceCube()
    {
        using WorldSessionRuntime runtime = CreateRuntime();
        runtime.AttachRemoteSession(new PlayerSessionId("session-a"));
        runtime.AttachRemoteSession(new PlayerSessionId("session-b"));

        AuthoritativeActionResult result = runtime.ProcessAuthoritativeAction(new PlayerActionMessage(
            new PlayerSessionId("session-b"),
            11,
            new BuildUseIntent(new Vector3(7f, 1f, 7f))));

        Assert.True(result.Accepted);
        WorldMutationResult mutation = Assert.IsType<WorldMutationResult>(result.Payload);
        Assert.True(mutation.Applied);
        Assert.Equal(WorldMutationKind.CubePlaced, mutation.Kind);
        Assert.NotNull(runtime.Map.TryGetCubeAtCell(7, 1, 7));
    }

    [Fact]
    public void ProcessAuthoritativeAction_UsesSessionOwnedModeForSecondaryRemoteBuildValidation()
    {
        using WorldSessionRuntime runtime = CreateRuntime();
        runtime.AttachRemoteSession(new PlayerSessionId("session-a"));
        runtime.AttachRemoteSession(new PlayerSessionId("session-b"));
        runtime.SetAuthoritativeInteractionMode(PlayerInteractionMode.Combat);
        runtime.AdvanceSecondaryRemoteSession(
            new PlayerCommandFrame(
                new PlayerSessionId("session-b"),
                3,
                new PlayerModeSwitchIntent(PlayerInteractionMode.Building),
                PlayerMoveIntent.None,
                PlayerLookIntent.None,
                PrimaryUsePressed: false),
            SimulationClock.FixedDeltaSeconds,
            CreatePlayerTuning());

        AuthoritativeActionResult result = runtime.ProcessAuthoritativeAction(new PlayerActionMessage(
            new PlayerSessionId("session-b"),
            12,
            new BuildUseIntent(new Vector3(8f, 1f, 7f))));

        Assert.True(result.Accepted);
        Assert.NotNull(runtime.Map.TryGetCubeAtCell(8, 1, 7));
    }

    [Fact]
    public void ProcessAuthoritativeAction_AllowsSecondaryRemoteSessionToResolveCombatDamage()
    {
        using WorldSessionRuntime runtime = CreateRuntime();
        runtime.AttachRemoteSession(new PlayerSessionId("session-a"));
        runtime.AttachRemoteSession(new PlayerSessionId("session-b"));
        runtime.ProcessAuthoritativeAction(new PlayerActionMessage(
            new PlayerSessionId("session-b"),
            13,
            new BuildUseIntent(new Vector3(7f, 1f, 5f))));
        runtime.AdvanceSecondaryRemoteSession(
            new PlayerCommandFrame(
                new PlayerSessionId("session-b"),
                4,
                new PlayerModeSwitchIntent(PlayerInteractionMode.Combat),
                PlayerMoveIntent.None,
                PlayerLookIntent.None,
                PrimaryUsePressed: false),
            SimulationClock.FixedDeltaSeconds,
            CreatePlayerTuning());

        AuthoritativeActionResult result = runtime.ProcessAuthoritativeAction(new PlayerActionMessage(
            new PlayerSessionId("session-b"),
            14,
            new CombatUseIntent(new Vector3(0f, 10f, 0f), Vector3.UnitY)));

        Assert.True(result.Accepted);
        CombatResolutionResult resolution = Assert.IsType<CombatResolutionResult>(result.Payload);
        Assert.True(resolution.DamageApplied > 0);
        Assert.Equal(CreatureKind.Object, resolution.TargetKind);
        Assert.True(resolution.CurrentHealth < resolution.MaxHealth);
    }

    [Fact]
    public void ProcessAuthoritativeAction_UsesSessionOwnedModeForSecondaryRemoteCombatValidation()
    {
        using WorldSessionRuntime runtime = CreateRuntime();
        runtime.AttachRemoteSession(new PlayerSessionId("session-a"));
        runtime.AttachRemoteSession(new PlayerSessionId("session-b"));
        runtime.ProcessAuthoritativeAction(new PlayerActionMessage(
            new PlayerSessionId("session-b"),
            15,
            new BuildUseIntent(new Vector3(7f, 1f, 5f))));
        runtime.SetAuthoritativeInteractionMode(PlayerInteractionMode.Building);
        runtime.AdvanceSecondaryRemoteSession(
            new PlayerCommandFrame(
                new PlayerSessionId("session-b"),
                5,
                new PlayerModeSwitchIntent(PlayerInteractionMode.Combat),
                PlayerMoveIntent.None,
                PlayerLookIntent.None,
                PrimaryUsePressed: false),
            SimulationClock.FixedDeltaSeconds,
            CreatePlayerTuning());

        AuthoritativeActionResult result = runtime.ProcessAuthoritativeAction(new PlayerActionMessage(
            new PlayerSessionId("session-b"),
            16,
            new CombatUseIntent(new Vector3(0f, 10f, 0f), Vector3.UnitY)));

        Assert.True(result.Accepted);
        CombatResolutionResult resolution = Assert.IsType<CombatResolutionResult>(result.Payload);
        Assert.True(resolution.DamageApplied > 0);
    }

    [Fact]
    public void ProcessAuthoritativeAction_UsesAuthoritativeSecondaryActorAimInsteadOfClientProvidedCombatRay()
    {
        using WorldSessionRuntime runtime = CreateRuntime();
        runtime.AttachRemoteSession(new PlayerSessionId("session-a"));
        runtime.AttachRemoteSession(new PlayerSessionId("session-b"));
        runtime.AdvanceSecondaryRemoteSession(
            new PlayerCommandFrame(
                new PlayerSessionId("session-b"),
                6,
                new PlayerModeSwitchIntent(PlayerInteractionMode.Building),
                PlayerMoveIntent.None,
                PlayerLookIntent.None,
                PrimaryUsePressed: false),
            SimulationClock.FixedDeltaSeconds,
            CreatePlayerTuning());
        runtime.ProcessAuthoritativeAction(new PlayerActionMessage(
            new PlayerSessionId("session-b"),
            17,
            new BuildUseIntent(new Vector3(7f, 1f, 5f))));
        runtime.AdvanceSecondaryRemoteSession(
            new PlayerCommandFrame(
                new PlayerSessionId("session-b"),
                7,
                new PlayerModeSwitchIntent(PlayerInteractionMode.Combat),
                PlayerMoveIntent.None,
                PlayerLookIntent.None,
                PrimaryUsePressed: false),
            SimulationClock.FixedDeltaSeconds,
            CreatePlayerTuning());

        AuthoritativeActionResult result = runtime.ProcessAuthoritativeAction(new PlayerActionMessage(
            new PlayerSessionId("session-b"),
            18,
            new CombatUseIntent(new Vector3(0f, 10f, 0f), Vector3.UnitY)));

        Assert.True(result.Accepted);
        CombatResolutionResult resolution = Assert.IsType<CombatResolutionResult>(result.Payload);
        Assert.True(resolution.DamageApplied > 0);
        Assert.Equal(CreatureKind.Object, resolution.TargetKind);
    }

    [Fact]
    public void ProcessAuthoritativeAction_AppliesPlayerStatsUpdateForJoinedSession()
    {
        using WorldSessionRuntime runtime = CreateRuntime();
        runtime.AttachRemoteSession(new PlayerSessionId("session-a"));

        AuthoritativeActionResult result = runtime.ProcessAuthoritativeAction(new PlayerActionMessage(
            new PlayerSessionId("session-a"),
            19,
            new PlayerStatsUpdateIntent(new PlayerCombatStatsSnapshot(22, 4.5f, 16))));

        Assert.True(result.Accepted);
        PlayerStatsUpdateResult applied = Assert.IsType<PlayerStatsUpdateResult>(result.Payload);
        Assert.Equal(22, applied.Stats.Strength);
        Assert.Equal(4.5f, applied.Stats.AttackSpeed, 3);
        Assert.Equal(16, applied.Stats.Vitality);
        Assert.Equal(22, runtime.Player.Combatant.Stats.Strength);
        Assert.Equal(4.5f, runtime.Player.Combatant.Stats.AttackSpeed, 3);
        Assert.Equal(16, runtime.Player.Combatant.Stats.Vitality);
    }

    [Fact]
    public void ProcessAuthoritativeAction_RejectsDuplicateActionSequence()
    {
        using WorldSessionRuntime runtime = CreateRuntime();
        runtime.AttachRemoteSession(new PlayerSessionId("session-a"));
        PlayerActionMessage action = new(
            new PlayerSessionId("session-a"),
            20,
            new PlayerStatsUpdateIntent(new PlayerCombatStatsSnapshot(22, 4.5f, 16)));

        AuthoritativeActionResult first = runtime.ProcessAuthoritativeAction(action);
        AuthoritativeActionResult replay = runtime.ProcessAuthoritativeAction(action);

        Assert.True(first.Accepted);
        Assert.False(replay.Accepted);
        Assert.NotNull(replay.Rejection);
        Assert.Contains("sequence", replay.Rejection?.Reason ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProcessAuthoritativeAction_RejectsCombatActionBeforeAttackCooldownExpires()
    {
        using WorldSessionRuntime runtime = CreateRuntime();
        runtime.AttachRemoteSession(new PlayerSessionId("session-a"));
        runtime.AttachRemoteSession(new PlayerSessionId("session-b"));
        runtime.ProcessAuthoritativeAction(new PlayerActionMessage(
            new PlayerSessionId("session-b"),
            21,
            new BuildUseIntent(new Vector3(7f, 1f, 5f))));
        runtime.AdvanceSecondaryRemoteSession(
            new PlayerCommandFrame(
                new PlayerSessionId("session-b"),
                1,
                new PlayerModeSwitchIntent(PlayerInteractionMode.Combat),
                PlayerMoveIntent.None,
                PlayerLookIntent.None,
                PrimaryUsePressed: false),
            SimulationClock.FixedDeltaSeconds,
            CreatePlayerTuning());

        AuthoritativeActionResult first = runtime.ProcessAuthoritativeAction(new PlayerActionMessage(
            new PlayerSessionId("session-b"),
            22,
            new CombatUseIntent(new Vector3(0f, 10f, 0f), Vector3.UnitY)));
        AuthoritativeActionResult second = runtime.ProcessAuthoritativeAction(new PlayerActionMessage(
            new PlayerSessionId("session-b"),
            23,
            new CombatUseIntent(new Vector3(0f, 10f, 0f), Vector3.UnitY)));

        Assert.True(first.Accepted);
        Assert.False(second.Accepted);
        Assert.NotNull(second.Rejection);
        Assert.Contains("cooldown", second.Rejection?.Reason ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProcessAuthoritativeAction_EquippedFastTool_Shortens_Authoritative_AttackCooldown()
    {
        using WorldSessionRuntime runtime = CreateRuntime();
        runtime.TryCaptureInventorySnapshot(PlayerSessionId.LocalDefault);
        runtime.ProcessAuthoritativeAction(new PlayerActionMessage(
            PlayerSessionId.LocalDefault,
            1,
            new BuildUseIntent(new Vector3(6f, 1f, 5f))));
        runtime.ProcessAuthoritativeAction(new PlayerActionMessage(
            PlayerSessionId.LocalDefault,
            2,
            new PlayerStatsUpdateIntent(new PlayerCombatStatsSnapshot(12, 2.5f, 11))));
        runtime.ProcessAuthoritativeAction(new PlayerActionMessage(
            PlayerSessionId.LocalDefault,
            3,
            new InventoryEquipIntent(1)));
        runtime.SetAuthoritativeInteractionMode(PlayerInteractionMode.Combat);

        Vector3 cubeCenter = new Vector3(6.5f, 1.5f, 5.5f);
        Vector3 lookDirection = (cubeCenter - runtime.Player.Camera.Position).Normalized();
        runtime.Player.SetFacing(lookDirection);

        AuthoritativeActionResult first = runtime.ProcessAuthoritativeAction(new PlayerActionMessage(
            PlayerSessionId.LocalDefault,
            4,
            new CombatUseIntent(Vector3.Zero, -Vector3.UnitZ)));
        runtime.AdvanceAuthoritativeTick(
            serverTick: 1,
            deltaTime: 0.30f,
            commandFrame: new PlayerCommandFrame(
                PlayerSessionId.LocalDefault,
                5,
                PlayerModeSwitchIntent.None,
                PlayerMoveIntent.None,
                PlayerLookIntent.None,
                PrimaryUsePressed: false),
            playerTuning: CreatePlayerTuning(),
            allowGameplaySimulation: false);
        AuthoritativeActionResult second = runtime.ProcessAuthoritativeAction(new PlayerActionMessage(
            PlayerSessionId.LocalDefault,
            6,
            new CombatUseIntent(Vector3.Zero, -Vector3.UnitZ)));

        Assert.True(first.Accepted);
        Assert.True(second.Accepted);
        Assert.Equal(4.0f, runtime.Player.Combatant.Stats.AttackSpeed, 3);
        Assert.Equal(0.25f, runtime.Player.Combatant.DerivedStats.AttackIntervalSeconds, 3);
    }

    [Fact]
    public void ProcessAuthoritativeAction_FatalCubeHit_FracturesCubeAndQueuesFractureReplication()
    {
        using WorldSessionRuntime runtime = CreateRuntime();
        runtime.ProcessAuthoritativeAction(new PlayerActionMessage(
            PlayerSessionId.LocalDefault,
            24,
            new BuildUseIntent(new Vector3(6f, 1f, 5f))));
        runtime.ProcessAuthoritativeAction(new PlayerActionMessage(
            PlayerSessionId.LocalDefault,
            25,
            new PlayerStatsUpdateIntent(new PlayerCombatStatsSnapshot(50, 5f, 10))));
        runtime.SetAuthoritativeInteractionMode(PlayerInteractionMode.Combat);

        Vector3 cubeCenter = new Vector3(6.5f, 1.5f, 5.5f);
        Vector3 lookDirection = (cubeCenter - runtime.Player.Camera.Position).Normalized();
        runtime.Player.SetFacing(lookDirection);

        AuthoritativeActionResult result = runtime.ProcessAuthoritativeAction(new PlayerActionMessage(
            PlayerSessionId.LocalDefault,
            26,
            new CombatUseIntent(Vector3.Zero, -Vector3.UnitZ)));
        ServerSnapshotEnvelope envelope = runtime.ExportServerSnapshotEnvelope();
        WorldSnapshot snapshot = runtime.ExportWorldSnapshot();

        Assert.True(result.Accepted);
        CombatResolutionResult resolution = Assert.IsType<CombatResolutionResult>(result.Payload);
        Assert.True(resolution.TargetDied);
        Assert.Contains(envelope.WorldMutations, mutation =>
            mutation.Kind == WorldMutationKind.CubeFractured &&
            mutation.CellPosition == new Vector3(6f, 1f, 5f));
        Assert.Contains(snapshot.FracturedCubeCells ?? Array.Empty<Vector3>(), cell => cell == new Vector3(6f, 1f, 5f));
        Assert.DoesNotContain(snapshot.PlacedCubeCells, cell => cell == new Vector3(6f, 1f, 5f));
    }

    [Fact]
    public void ProcessAuthoritativeAction_FatalMovedCubeHit_FracturesCurrentAuthoritativeCell()
    {
        using WorldSessionRuntime runtime = CreateRuntime();
        runtime.ProcessAuthoritativeAction(new PlayerActionMessage(
            PlayerSessionId.LocalDefault,
            27,
            new BuildUseIntent(new Vector3(6f, 1f, 5f))));
        runtime.ProcessAuthoritativeAction(new PlayerActionMessage(
            PlayerSessionId.LocalDefault,
            28,
            new PlayerStatsUpdateIntent(new PlayerCombatStatsSnapshot(50, 5f, 10))));
        runtime.SetAuthoritativeInteractionMode(PlayerInteractionMode.Combat);

        Cube cube = Assert.IsType<Cube>(runtime.Map.Cubes[6, 1, 5]);
        runtime.Map.UpdateCubePosition(cube, new Vector3(7.2f, 1.5f, 5.5f));

        Vector3 lookDirection = (cube.Position - runtime.Player.Camera.Position).Normalized();
        runtime.Player.SetFacing(lookDirection);

        AuthoritativeActionResult result = runtime.ProcessAuthoritativeAction(new PlayerActionMessage(
            PlayerSessionId.LocalDefault,
            29,
            new CombatUseIntent(Vector3.Zero, -Vector3.UnitZ)));
        ServerSnapshotEnvelope envelope = runtime.ExportServerSnapshotEnvelope();
        WorldSnapshot snapshot = runtime.ExportWorldSnapshot();

        Assert.True(result.Accepted);
        CombatResolutionResult resolution = Assert.IsType<CombatResolutionResult>(result.Payload);
        Assert.True(resolution.TargetDied);
        Assert.Contains(envelope.WorldMutations, mutation =>
            mutation.Kind == WorldMutationKind.CubeFractured &&
            mutation.CellPosition == new Vector3(7f, 1f, 5f));
        Assert.Contains(snapshot.FracturedCubeCells ?? Array.Empty<Vector3>(), cell => cell == new Vector3(7f, 1f, 5f));
    }

    private static WorldSessionRuntime CreateRuntime()
    {
        return new WorldSessionRuntime(
            floorSize: 10,
            playerStartPosition: new Vector3(5f, 1.5f, 5f),
            new SceneEventHub(),
            loadVisualAssets: false);
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
}
