using OpenTK.Mathematics;
using Xunit;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Physics;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;

namespace Dream.Tests.Simulation.Runtime;

// Secondary sessions used to be kinematic ghosts (Position += direction * speed, no gravity,
// jump or collision) that only moved on ticks where a command arrived. They now wrap a full
// physics Player like the primary seat.
public class RemoteSessionPlayerStateTests
{
    private static readonly PlayerSimulationTuning Tuning = PlayerSimulationTuning.AuthoritativeDefault;

    [Fact]
    public void ApplyCommand_UpdatesInteractionModeAndFacingImmediately()
    {
        using PhysicsIntegrator physics = new();
        Map map = new(physics, width: 10, depth: 10);
        using RemoteSessionPlayerState state = CreateState(map, physics, new Vector3(4f, 1.5f, 4f));

        state.ApplyCommand(CreateCommand(1, forward: 0f, facing: Vector3.UnitX, mode: PlayerInteractionMode.Combat));

        Assert.Equal(PlayerInteractionMode.Combat, state.InteractionMode);
        Assert.Equal(1f, state.Facing.X, 3);
        Assert.Equal("session-b", state.SessionId.Value);
    }

    [Fact]
    public void Simulate_MovesAlongClientFacingThroughPhysics()
    {
        using PhysicsIntegrator physics = new();
        Map map = new(physics, width: 10, depth: 10);
        using RemoteSessionPlayerState state = CreateState(map, physics, new Vector3(4f, 1.5f, 4f));
        state.ApplyCommand(CreateCommand(1, forward: 1f, facing: Vector3.UnitX));

        Step(state, physics, ticks: 32);

        Assert.True(state.Position.X > 5f, $"x={state.Position.X}");
        Assert.Equal(4f, state.Position.Z, 1);
    }

    [Fact]
    public void Simulate_FallsUnderGravity()
    {
        using PhysicsIntegrator physics = new();
        Map map = new(physics, width: 10, depth: 10);
        using RemoteSessionPlayerState state = CreateState(map, physics, new Vector3(4f, 4.5f, 4f));

        Step(state, physics, ticks: 96);

        Assert.InRange(state.Position.Y, 1.3f, 1.7f);
    }

    [Fact]
    public void Simulate_IsBlockedByStaticCube()
    {
        using PhysicsIntegrator physics = new();
        Map map = new(physics, width: 10, depth: 10);
        Assert.NotNull(map.TryAddCube(new Vector3(6f, 1f, 4f), isStatic: true));
        using RemoteSessionPlayerState state = CreateState(map, physics, new Vector3(4.5f, 1.5f, 4.5f));
        state.ApplyCommand(CreateCommand(1, forward: 1f, facing: Vector3.UnitX));

        Step(state, physics, ticks: 128);

        Assert.True(state.Position.X < 5.6f, $"Walked through the cube: x={state.Position.X}");
    }

    [Fact]
    public void Simulate_KeepsMovingWithoutNewCommandsAtTheSameRate()
    {
        using PhysicsIntegrator physics = new();
        Map map = new(physics, width: 20, depth: 20);
        using RemoteSessionPlayerState everyTick = CreateState(map, physics, new Vector3(2.5f, 1.5f, 2.5f), "session-b");
        using RemoteSessionPlayerState sparse = CreateState(map, physics, new Vector3(2.5f, 1.5f, 8.5f), "session-c");

        for (int tick = 0; tick < 64; tick++)
        {
            everyTick.ApplyCommand(CreateCommand((ulong)tick + 1, forward: 1f, facing: Vector3.UnitX, session: "session-b"));
            if (tick % 4 == 0)
            {
                sparse.ApplyCommand(CreateCommand((ulong)tick + 1, forward: 1f, facing: Vector3.UnitX, session: "session-c"));
            }

            StepAll([everyTick, sparse], physics);
        }

        Assert.Equal(everyTick.Position.X, sparse.Position.X, 1);
    }

    [Fact]
    public void Dispose_RemovesTheBodyFromThePhysicsWorld()
    {
        using PhysicsIntegrator physics = new();
        Map map = new(physics, width: 10, depth: 10);
        RemoteSessionPlayerState state = CreateState(map, physics, new Vector3(4f, 1.5f, 4f));
        Assert.Single(physics.SessionPlayerBodies);

        state.Dispose();

        Assert.Empty(physics.SessionPlayerBodies);
    }

    [Fact]
    public void Runtime_SecondarySession_PushesDynamicCubeLikeThePrimary()
    {
        using WorldSessionRuntime runtime = new(16, new Vector3(5f, 1.5f, 5f), new SceneEventHub());
        runtime.Enemies.Clear();
        PlayerSessionId primary = new("session-a");
        PlayerSessionId secondary = new("session-b");
        runtime.AttachRemoteSession(primary);
        runtime.AttachRemoteSession(secondary);
        Vector3 start = new(2.5f, 1.5f, 12.5f);
        RemoteSessionPlayerState state = GetSecondary(runtime, secondary);
        state.SetWorldPosition(start);
        Cube cube = Assert.IsType<Cube>(runtime.Map.TryAddCube(new Vector3(4f, 1f, 12f)));
        float cubeStartX = cube.Position.X;

        for (ulong tick = 1; tick <= 128; tick++)
        {
            runtime.AdvanceSecondaryRemoteSession(
                CreateCommand(tick, forward: 1f, facing: Vector3.UnitX, session: "session-b"),
                SimulationClock.FixedDeltaSeconds,
                Tuning);
            runtime.AdvanceAuthoritativeTick(tick, SimulationClock.FixedDeltaSeconds, (PlayerCommandFrame?)null, Tuning);
        }

        Assert.True(cube.Position.X > cubeStartX + 0.5f, $"Cube did not move: {cube.Position.X}");
    }

    [Fact]
    public void Runtime_PrimaryDetach_ParksItsBodyAndRejoinUnparksIt()
    {
        using WorldSessionRuntime runtime = new(10, new Vector3(5f, 1.5f, 5f), new SceneEventHub());
        PlayerSessionId primary = new("session-a");
        runtime.AttachRemoteSession(primary);

        runtime.DetachRemoteSession(primary);
        Assert.True(runtime.Physics.IsPlayerBodyParked);

        runtime.AttachRemoteSession(primary);
        Assert.False(runtime.Physics.IsPlayerBodyParked);
        Assert.Equal(primary, runtime.PrimaryRemoteSessionId);
    }

    [Fact]
    public void Runtime_SecondaryDetach_RemovesItsBody()
    {
        using WorldSessionRuntime runtime = new(10, new Vector3(5f, 1.5f, 5f), new SceneEventHub());
        runtime.AttachRemoteSession(new PlayerSessionId("session-a"));
        PlayerSessionId secondary = new("session-b");
        runtime.AttachRemoteSession(secondary);
        Assert.Single(runtime.Physics.SessionPlayerBodies);

        runtime.DetachRemoteSession(secondary);

        Assert.Empty(runtime.Physics.SessionPlayerBodies);
    }

    [Fact]
    public void Runtime_FormerPrimaryRejoiningAfterSeatWasTaken_GetsAWorkingSeat()
    {
        // A leaves, C takes the primary seat, A returns. A used to keep C's actor id without a
        // seat and could neither move nor act nor appear in snapshots.
        using WorldSessionRuntime runtime = new(10, new Vector3(5f, 1.5f, 5f), new SceneEventHub());
        PlayerSessionId a = new("session-a");
        PlayerSessionId c = new("session-c");
        runtime.AttachRemoteSession(a, "A");
        runtime.DetachRemoteSession(a);
        runtime.AttachRemoteSession(c, "C");

        ConnectedPlayer returning = runtime.AttachRemoteSession(a, "A");

        Assert.Equal(c, runtime.PrimaryRemoteSessionId);
        Assert.NotEqual(runtime.Player.Combatant.Actor.Id, returning.ActorId);
        ServerSnapshotEnvelope snapshot = runtime.ExportServerSnapshotEnvelope();
        Assert.Contains(snapshot.Actors, actor => actor.SessionId == "session-a" && actor.ActorId == returning.ActorId);
        Assert.Contains(snapshot.Actors, actor => actor.SessionId == "session-c");
        AuthoritativeActionResult build = runtime.ProcessAuthoritativeAction(new PlayerActionMessage(
            a,
            1,
            new BuildUseIntent(GetSecondary(runtime, a).Position + new Vector3(1.5f, -0.5f, 0f))));
        Assert.NotEqual("Unknown player session.", build.Rejection?.Reason);
    }

    private static RemoteSessionPlayerState GetSecondary(WorldSessionRuntime runtime, PlayerSessionId sessionId)
    {
        System.Reflection.FieldInfo field = typeof(WorldSessionRuntime).GetField(
            "_secondaryRemotePlayers",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var players = (Dictionary<PlayerSessionId, RemoteSessionPlayerState>)field.GetValue(runtime)!;
        return players[sessionId];
    }

    private static RemoteSessionPlayerState CreateState(
        Map map,
        PhysicsIntegrator physics,
        Vector3 position,
        string session = "session-b")
    {
        return new RemoteSessionPlayerState(
            actorId: 2001,
            sessionId: new PlayerSessionId(session),
            position: position,
            map,
            physics);
    }

    private static PlayerCommandFrame CreateCommand(
        ulong sequence,
        float forward,
        Vector3 facing,
        PlayerInteractionMode? mode = null,
        string session = "session-b")
    {
        return new PlayerCommandFrame(
            new PlayerSessionId(session),
            sequence,
            new PlayerModeSwitchIntent(mode),
            new PlayerMoveIntent(ForwardAxis: forward, StrafeAxis: 0f),
            new PlayerLookIntent(0f, 0f, facing),
            PrimaryUsePressed: false);
    }

    private static void Step(RemoteSessionPlayerState state, PhysicsIntegrator physics, int ticks)
    {
        for (int tick = 0; tick < ticks; tick++)
        {
            StepAll([state], physics);
        }
    }

    private static void StepAll(IReadOnlyList<RemoteSessionPlayerState> states, PhysicsIntegrator physics)
    {
        foreach (RemoteSessionPlayerState state in states)
        {
            state.Simulate(SimulationClock.FixedDeltaSeconds, Tuning);
        }

        physics.StepSimulationFixed(SimulationClock.FixedDeltaSeconds);
        foreach (RemoteSessionPlayerState state in states)
        {
            state.SyncAfterPhysicsStep(Tuning);
        }
    }
}
