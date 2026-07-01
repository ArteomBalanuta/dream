using OpenTK.Mathematics;
using Xunit;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;

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
}
