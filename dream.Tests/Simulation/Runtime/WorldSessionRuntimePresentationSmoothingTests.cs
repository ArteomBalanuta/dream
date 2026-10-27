using OpenTK.Mathematics;
using Xunit;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;

namespace Dream.Tests.Simulation.Runtime;


public class WorldSessionRuntimePresentationSmoothingTests
{
    [Fact]
    public void AdvanceAuthoritativeTick_AppliesMovementBeforePhysicsAndResyncsCameraAfterStep()
    {
        using WorldSessionRuntime runtime = new(
            floorSize: 10,
            playerStartPosition: new Vector3(5f, 1.5f, 5f),
            new SceneEventHub(),
            loadVisualAssets: false);
        PlayerSimulationTuning tuning = new(
            EyeHeightOffset: 0.35f,
            MouseSensitivity: 2.5f,
            MoveSpeed: 6f,
            NoclipSpeed: 18f,
            JumpVelocity: 5f);
        Vector3 before = runtime.Player.GetWorldPosition();

        runtime.AdvanceAuthoritativeTick(
            serverTick: 1,
            deltaTime: SimulationClock.FixedDeltaSeconds,
            commandFrame: new PlayerCommandFrame(
                PlayerSessionId.LocalDefault,
                Sequence: 1,
                PlayerModeSwitchIntent.None,
                new PlayerMoveIntent(ForwardAxis: 1f, StrafeAxis: 0f),
                PlayerLookIntent.None,
                PrimaryUsePressed: false),
            playerTuning: tuning,
            allowGameplaySimulation: false);

        Vector3 after = runtime.Player.GetWorldPosition();
        Vector3 cameraPosition = runtime.Player.Camera.Position;

        Assert.True(after.Z < before.Z - 0.01f, $"Expected same-tick forward movement, but before Z was {before.Z} and after Z was {after.Z}.");
        Assert.Equal(after.X, cameraPosition.X, precision: 4);
        Assert.Equal(after.Y + tuning.EyeHeightOffset, cameraPosition.Y, precision: 4);
        Assert.Equal(after.Z, cameraPosition.Z, precision: 4);
    }
}
