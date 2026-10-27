using OpenTK.Mathematics;
using Xunit;
using Dream.Client.Presentation;
using Dream.Client.World;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;

namespace Dream.Tests.Client.Presentation;


public class ClientWorldSessionPresentationTests
{
    [Fact]
    public void CapturePresentationState_WithPresentationLeadSeconds_PredictsLocalCameraForwardBetweenTicks()
    {
        using ClientWorldSession session = new(
            floorSize: 10,
            playerStartPosition: new Vector3(5f, 1.5f, 5f),
            new SceneEventHub());
        PlayerSimulationTuning tuning = new(
            EyeHeightOffset: 0.35f,
            MouseSensitivity: 2.5f,
            MoveSpeed: 6f,
            NoclipSpeed: 150f,
            JumpVelocity: 5f);

        session.Player.Update(
            deltaTime: 0f,
            new PlayerSimulationInput(
                Movement: new MovementCommand(Vector3.Zero),
                LookDelta: Vector2.Zero,
                AllowGameplayInput: true),
            tuning);
        session.Player.PhysicsBody.LinearVelocity = new BulletSharp.Math.Vector3(4f, 0f, 0f);

        WorldSessionPresentationState baseState = session.CapturePresentationState(WorldVisibility.MaxRenderDistance);
        WorldSessionPresentationState smoothedState = session.CapturePresentationState(
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
        using ClientWorldSession session = new(
            floorSize: 10,
            playerStartPosition: new Vector3(5f, 1.5f, 5f),
            new SceneEventHub());
        PlayerSimulationTuning tuning = new(
            EyeHeightOffset: 0.35f,
            MouseSensitivity: 2.5f,
            MoveSpeed: 6f,
            NoclipSpeed: 150f,
            JumpVelocity: 5f);

        session.Player.Update(
            deltaTime: 0f,
            new PlayerSimulationInput(
                Movement: new MovementCommand(Vector3.Zero),
                LookDelta: Vector2.Zero,
                AllowGameplayInput: true),
            tuning);
        session.Player.PhysicsBody.LinearVelocity = new BulletSharp.Math.Vector3(0f, -12f, 0f);

        WorldSessionPresentationState baseState = session.CapturePresentationState(WorldVisibility.MaxRenderDistance);
        WorldSessionPresentationState smoothedState = session.CapturePresentationState(
            WorldVisibility.MaxRenderDistance,
            presentationLeadSeconds: SimulationClock.FixedDeltaSeconds * 0.5f);

        Assert.Equal(baseState.Camera.Position.Y, smoothedState.Camera.Position.Y, 5);
    }

    [Fact]
    public void CapturePresentationState_UsesStableStaticExportAndBoundsDynamicGeometry()
    {
        using ClientWorldSession session = new(
            floorSize: 20,
            playerStartPosition: new Vector3(1.5f, 1.5f, 1.5f),
            new SceneEventHub());
        session.Map.AddCube(new Vector3(18f, 1f, 18f), isStatic: false, withPhysics: false);

        WorldSessionPresentationState state = session.CapturePresentationState(maxRenderDistance: 1f);

        Assert.Contains(state.VisibleWorld.Cells, cell => cell.Key == new GeometryCellKey(19, 0, 19));
        Assert.Contains(state.VisibleWorld.Cells, cell => cell.Key == new GeometryCellKey(1, 0, 1));
        Assert.DoesNotContain(state.VisibleWorld.Cells, cell => cell.Key == new GeometryCellKey(18, 1, 18));
        Assert.DoesNotContain(
            state.VisibleWorld.DynamicCompatibilityCubes,
            cube => cube.Position == new Vector3(18.5f, 1.5f, 18.5f));
    }
}
