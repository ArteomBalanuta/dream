using OpenTK.Mathematics;
using Xunit;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Combat;
using Dream.Rendering.Models;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;

namespace Dream.Tests.Simulation.Runtime;

public class WorldSessionRuntimeCombatEventQueueTests
{
    [Fact]
    public void ProcessQueuedAuthoritativeActions_ProcessesCombatInReceiveOrder()
    {
        using WorldSessionRuntime runtime = CreateRuntime();
        PlayerSessionId sessionA = new("session-a");
        PlayerSessionId sessionB = new("session-b");
        runtime.AttachRemoteSession(sessionA, "Player A");
        runtime.AttachRemoteSession(sessionB, "Player B");
        runtime.SetAuthoritativeInteractionMode(PlayerInteractionMode.Combat);

        ServerSnapshotEnvelope before = runtime.ExportServerSnapshotEnvelope();
        ActorSnapshot wolfBefore = Assert.Single(before.Actors, actor => actor.Kind == CreatureKind.Enemy);
        Vector3 wolfAnchor = wolfBefore.Position + new Vector3(0f, WolfAssetDefinition.CombatAnchorHeight, 0f);

        Vector3 wolfDirectionFromPrimary = DirectionFrom(runtime.Player.Camera.Position, wolfAnchor);
        runtime.Player.SetFacing(wolfDirectionFromPrimary);

        Vector3 wolfDirectionFromSecondary = DirectionFrom(
            FindActorPosition(before, sessionB.Value),
            wolfAnchor);
        runtime.AdvanceSecondaryRemoteSession(
            new PlayerCommandFrame(
                sessionB,
                1,
                new PlayerModeSwitchIntent(PlayerInteractionMode.Combat),
                PlayerMoveIntent.None,
                new PlayerLookIntent(0f, 0f, wolfDirectionFromSecondary),
                PrimaryUsePressed: false),
            SimulationClock.FixedDeltaSeconds,
            CreatePlayerTuning());

        IReadOnlyList<AuthoritativeActionResult> results = runtime.ProcessQueuedAuthoritativeActions(
            7,
            [
                new QueuedPlayerAction(
                    1,
                    sessionB,
                    new PlayerActionMessage(sessionB, 11, new CombatUseIntent(Vector3.Zero, -Vector3.UnitZ))),
                new QueuedPlayerAction(
                    2,
                    sessionA,
                    new PlayerActionMessage(sessionA, 12, new CombatUseIntent(Vector3.Zero, -Vector3.UnitZ)))
            ]);

        ServerSnapshotEnvelope after = runtime.ExportServerSnapshotEnvelope();
        ActorSnapshot wolfAfter = Assert.Single(after.Actors, actor => actor.Kind == CreatureKind.Enemy);

        Assert.Equal(2, results.Count);
        Assert.Equal("session-b", results[0].SessionId);
        Assert.Equal((ulong)11, results[0].ActionSequence);
        Assert.Equal("session-a", results[1].SessionId);
        Assert.Equal((ulong)12, results[1].ActionSequence);
        Assert.True(results.All(result => result.Accepted));
        Assert.All(results, result => Assert.IsType<CombatResolutionResult>(result.Payload));
        Assert.True(wolfAfter.CurrentHealth < wolfBefore.CurrentHealth);
    }

    private static WorldSessionRuntime CreateRuntime()
    {
        return new WorldSessionRuntime(
            floorSize: 10,
            playerStartPosition: new Vector3(5f, 1.5f, 5f),
            new SceneEventHub(),
            loadVisualAssets: false);
    }

    private static Vector3 FindActorPosition(ServerSnapshotEnvelope snapshot, string sessionId)
    {
        ActorSnapshot actor = Assert.Single(snapshot.Actors, candidate => candidate.SessionId == sessionId);
        return actor.Position;
    }

    private static Vector3 DirectionFrom(Vector3 origin, Vector3 target)
    {
        Vector3 direction = target - origin;
        return direction.LengthSquared <= 0.0001f ? -Vector3.UnitZ : direction.Normalized();
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
