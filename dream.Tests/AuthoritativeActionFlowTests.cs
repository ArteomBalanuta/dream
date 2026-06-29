using OpenTK.Mathematics;
using Xunit;

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
