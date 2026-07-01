using OpenTK.Mathematics;
using Xunit;

public class RuntimeAuthoritativeSessionHostTests
{
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
