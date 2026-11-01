using OpenTK.Mathematics;
using Xunit;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;

namespace Dream.Tests.Simulation.Runtime;


public class WorldSessionRuntimeMultiplayerEnemyTests
{
    [Fact]
    public void AdvanceAuthoritativeTick_WhenSecondaryRemotePlayerIsNearestEnemyTarget_AppliesDamageToThatSession()
    {
        using WorldSessionRuntime runtime = new(
            floorSize: 10,
            playerStartPosition: new Vector3(3.5f, 1.5f, 0.5f),
            new SceneEventHub());
        runtime.AttachRemoteSession(new PlayerSessionId("session-a"));
        runtime.AttachRemoteSession(new PlayerSessionId("session-b"));

        // The join window (192 ticks) first, then 160 ticks of the wolf.
        for (int tick = 1; tick <= 160 + PlayerRespawnRules.SpawnProtectionTicks; tick++)
        {
            runtime.AdvanceAuthoritativeTick(
                (ulong)tick,
                SimulationClock.FixedDeltaSeconds,
                new PlayerCommandFrame(
                    PlayerSessionId.LocalDefault,
                    (ulong)tick,
                    PlayerModeSwitchIntent.None,
                    PlayerMoveIntent.None,
                    PlayerLookIntent.None,
                    PrimaryUsePressed: false),
                CreatePlayerTuning());
        }

        ServerSnapshotEnvelope snapshot = runtime.ExportServerSnapshotEnvelope();
        ActorSnapshot primaryActor = Assert.Single(snapshot.Actors, actor => actor.SessionId == "session-a");
        ActorSnapshot secondaryActor = Assert.Single(snapshot.Actors, actor => actor.SessionId == "session-b");

        Assert.Equal(100, primaryActor.CurrentHealth);
        Assert.True(secondaryActor.CurrentHealth < secondaryActor.MaxHealth);
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
