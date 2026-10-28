using System.Reflection;
using BulletSharp;
using OpenTK.Mathematics;
using Xunit;
using Dream.Client.World;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Physics;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;

namespace Dream.Tests.Simulation.Players;

// Player owns a native-backed contact callback and is IDisposable (MEM-11). Every owner of a
// Player must dispose it before the physics world the callback tests against, so the callback
// is not left to BulletSharp's finalizer: the authoritative runtime (primary and secondary
// sessions), the desktop client session, and each secondary session's state.
public class PlayerDisposalOwnershipTests
{
    private static readonly FieldInfo PlayerCallbackField =
        typeof(Player).GetField("_contactCallback", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static readonly FieldInfo SecondaryPlayersField =
        typeof(WorldSessionRuntime).GetField("_secondaryRemotePlayers", BindingFlags.Instance | BindingFlags.NonPublic)!;

    [Fact]
    public void WorldSessionRuntime_Dispose_DisposesPrimaryAndEverySecondaryPlayerCallback()
    {
        WorldSessionRuntime runtime = new(
            WorldDefaults.StartupFloorSize,
            new Vector3(40f, 1.5f, 40f),
            new SceneEventHub());
        runtime.AttachRemoteSession(new PlayerSessionId("session-a"), "a");
        runtime.AttachRemoteSession(new PlayerSessionId("session-b"), "b");
        runtime.AttachRemoteSession(new PlayerSessionId("session-c"), "c");

        RemoteSessionPlayerState[] secondaries = SecondariesOf(runtime);
        Assert.Equal(2, secondaries.Length);
        List<Player.PlayerContactCallback> callbacks = [CallbackOf(runtime.Player)];
        callbacks.AddRange(secondaries.Select(secondary => CallbackOf(secondary.PhysicsPlayer)));
        Assert.Equal(3, callbacks.Distinct(ReferenceEqualityComparer.Instance).Count());
        Assert.All(callbacks, callback => Assert.False(callback.IsDisposed));
        Assert.Equal(2, runtime.Physics.SessionPlayerBodies.Count);

        runtime.Dispose();

        Assert.All(callbacks, callback => Assert.True(callback.IsDisposed));
        // Each secondary removed its own body; Physics.Dispose found none left to remove again.
        Assert.Empty(runtime.Physics.SessionPlayerBodies);
        Assert.Empty(SecondariesOf(runtime));

        // A second dispose is harmless: nothing is disposed or removed twice.
        runtime.Dispose();
        Assert.All(callbacks, callback => Assert.True(callback.IsDisposed));
    }

    [Fact]
    public void WorldSessionRuntime_Dispose_AfterSecondaryDetached_DisposesRemainingPlayersOnce()
    {
        WorldSessionRuntime runtime = new(
            WorldDefaults.StartupFloorSize,
            new Vector3(40f, 1.5f, 40f),
            new SceneEventHub());
        runtime.AttachRemoteSession(new PlayerSessionId("session-a"), "a");
        runtime.AttachRemoteSession(new PlayerSessionId("session-b"), "b");
        runtime.AttachRemoteSession(new PlayerSessionId("session-c"), "c");
        RemoteSessionPlayerState[] secondaries = SecondariesOf(runtime);
        Player.PlayerContactCallback detachedCallback = CallbackOf(secondaries[0].PhysicsPlayer);
        Player.PlayerContactCallback remainingCallback = CallbackOf(secondaries[1].PhysicsPlayer);

        runtime.DetachRemoteSession(secondaries[0].SessionId);

        Assert.True(detachedCallback.IsDisposed);
        Assert.False(remainingCallback.IsDisposed);
        Assert.Single(runtime.Physics.SessionPlayerBodies);

        runtime.Dispose();

        Assert.True(remainingCallback.IsDisposed);
        Assert.True(CallbackOf(runtime.Player).IsDisposed);
        Assert.Empty(runtime.Physics.SessionPlayerBodies);
    }

    [Fact]
    public void ClientWorldSession_Dispose_DisposesPlayerCallback_AndIsIdempotent()
    {
        ClientWorldSession session = new(
            floorSize: 10,
            playerStartPosition: new Vector3(5f, 1.5f, 5f),
            new SceneEventHub());
        Player.PlayerContactCallback callback = CallbackOf(session.Player);
        Assert.False(callback.IsDisposed);

        session.Dispose();

        Assert.True(callback.IsDisposed);
        Assert.Null(session.Physics.PlayerBody);

        session.Dispose();
        Assert.True(callback.IsDisposed);
    }

    [Fact]
    public void RemoteSessionPlayerState_Dispose_DisposesPlayerAndRemovesBodyOnce()
    {
        using PhysicsIntegrator physics = new();
        Map map = new(physics, width: 12, depth: 12);
        RemoteSessionPlayerState state = new(
            actorId: 7,
            new PlayerSessionId("session-x"),
            new Vector3(5f, 1.5f, 5f),
            map,
            physics);
        Player.PlayerContactCallback callback = CallbackOf(state.PhysicsPlayer);
        RigidBody body = state.PhysicsPlayer.PhysicsBody;
        Assert.Contains(body, physics.SessionPlayerBodies);

        state.Dispose();

        Assert.True(callback.IsDisposed);
        Assert.DoesNotContain(body, physics.SessionPlayerBodies);
        Assert.Throws<ObjectDisposedException>(() => state.PhysicsPlayer.Update(
            1f / 60f,
            new PlayerSimulationInput(new MovementCommand(Vector3.Zero), Vector2.Zero, AllowGameplayInput: true),
            PlayerSimulationTuning.AuthoritativeDefault));

        state.Dispose();
        Assert.True(callback.IsDisposed);
        Assert.Empty(physics.SessionPlayerBodies);
    }

    private static Player.PlayerContactCallback CallbackOf(Player player)
    {
        return (Player.PlayerContactCallback)PlayerCallbackField.GetValue(player)!;
    }

    private static RemoteSessionPlayerState[] SecondariesOf(WorldSessionRuntime runtime)
    {
        var secondaries = (Dictionary<PlayerSessionId, RemoteSessionPlayerState>)SecondaryPlayersField.GetValue(runtime)!;
        return secondaries.Values.ToArray();
    }
}
