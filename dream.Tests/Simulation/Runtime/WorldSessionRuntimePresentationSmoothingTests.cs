using OpenTK.Mathematics;
using Xunit;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;

namespace Dream.Tests.Simulation.Runtime;


public class WorldSessionRuntimePresentationSmoothingTests
{
    [Fact]
    public void CapturePresentationState_WithPresentationLeadSeconds_PredictsLocalCameraForwardBetweenTicks()
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
            NoclipSpeed: 150f,
            JumpVelocity: 5f);

        runtime.Player.Update(
            deltaTime: 0f,
            new PlayerSimulationInput(
                Movement: new MovementCommand(Vector3.Zero),
                LookDelta: Vector2.Zero,
                AllowGameplayInput: true),
            tuning);
        runtime.Player.PhysicsBody.LinearVelocity = new BulletSharp.Math.Vector3(4f, 0f, 0f);

        WorldSessionPresentationState baseState = runtime.CapturePresentationState(WorldVisibility.MaxRenderDistance);
        WorldSessionPresentationState smoothedState = runtime.CapturePresentationState(
            WorldVisibility.MaxRenderDistance,
            presentationLeadSeconds: SimulationClock.FixedDeltaSeconds * 0.5f);

        Assert.Equal(baseState.Camera.Position.Y, smoothedState.Camera.Position.Y);
        Assert.True(smoothedState.Camera.Position.X > baseState.Camera.Position.X);
        Assert.InRange(
            smoothedState.Camera.Position.X - baseState.Camera.Position.X,
            0.02f,
            0.04f);
    }

    [Fact]
    public void CapturePresentationState_DoesNotPredictVerticalCameraDipBetweenTicks()
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
            NoclipSpeed: 150f,
            JumpVelocity: 5f);

        runtime.Player.Update(
            deltaTime: 0f,
            new PlayerSimulationInput(
                Movement: new MovementCommand(Vector3.Zero),
                LookDelta: Vector2.Zero,
                AllowGameplayInput: true),
            tuning);
        runtime.Player.PhysicsBody.LinearVelocity = new BulletSharp.Math.Vector3(0f, -12f, 0f);

        WorldSessionPresentationState baseState = runtime.CapturePresentationState(WorldVisibility.MaxRenderDistance);
        WorldSessionPresentationState smoothedState = runtime.CapturePresentationState(
            WorldVisibility.MaxRenderDistance,
            presentationLeadSeconds: SimulationClock.FixedDeltaSeconds * 0.5f);

        Assert.Equal(baseState.Camera.Position.Y, smoothedState.Camera.Position.Y, 5);
    }

    [Fact]
    public void CapturePresentationState_UsesStableFullWorldExportForRendering()
    {
        using WorldSessionRuntime runtime = new(
            floorSize: 20,
            playerStartPosition: new Vector3(1.5f, 1.5f, 1.5f),
            new SceneEventHub(),
            loadVisualAssets: false);

        WorldSessionPresentationState state = runtime.CapturePresentationState(maxRenderDistance: 1f);

        Assert.Contains(state.VisibleWorld.Cells, cell => cell.Key == new GeometryCellKey(19, 0, 19));
    }

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
        WorldSessionPresentationState presentation = runtime.CapturePresentationState(WorldVisibility.MaxRenderDistance);

        Assert.True(after.Z < before.Z - 0.01f, $"Expected same-tick forward movement, but before Z was {before.Z} and after Z was {after.Z}.");
        Assert.Equal(after.X, presentation.Camera.Position.X, precision: 4);
        Assert.Equal(after.Y + tuning.EyeHeightOffset, presentation.Camera.Position.Y, precision: 4);
        Assert.Equal(after.Z, presentation.Camera.Position.Z, precision: 4);
    }
}
