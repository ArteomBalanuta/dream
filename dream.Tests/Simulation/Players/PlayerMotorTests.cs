using BulletSharp;
using BulletSharp.Math;
using OpenTK.Mathematics;
using Xunit;
using TkVector3 = OpenTK.Mathematics.Vector3;
using Dream.Simulation.Physics;
using Dream.Simulation.Players;
using Dream.Simulation.World;

namespace Dream.Tests.Simulation.Players;


public class PlayerMotorTests
{
    [Fact]
    public void Update_WhenDisablingNoclipWhileInsideFloor_SnapsBodyBackAboveFloor()
    {
        using PhysicsIntegrator physics = new();
        Map map = new(physics, loadVisualAssets: false, width: 10, depth: 10);
        PhysicsFactory factory = new();
        RigidBody body = factory.CreatePlayerBody(new BulletSharp.Math.Vector3(5f, 1.5f, 5f));
        physics.RegisterPlayerBody(body, PhysicsBodyOwnership.IntegratorOwned);
        PlayerMotor motor = new(physics);
        PlayerSimulationTuning tuning = CreateTuning();

        TkVector3 position = GetBodyPosition(body);
        motor.Update(body, position, new MovementCommand(TkVector3.Zero, ToggleNoclipPressed: true), tuning, 0.016f);

        position = GetBodyPosition(body);
        motor.Update(body, position, new MovementCommand(TkVector3.Zero, VerticalDirection: -1f), tuning, 0.20f);

        position = GetBodyPosition(body);
        Assert.True(position.Y < 1.5f, $"Expected noclip movement to move the body into the floor, but Y was {position.Y}.");

        motor.Update(body, position, new MovementCommand(TkVector3.Zero, ToggleNoclipPressed: true), tuning, 0.016f);

        TkVector3 recovered = GetBodyPosition(body);
        Assert.True(recovered.Y >= 1.5f, $"Expected noclip exit to restore a grounded body height, but Y was {recovered.Y}.");
        Assert.Equal(CollisionFlags.None, body.CollisionFlags & CollisionFlags.NoContactResponse);
        GC.KeepAlive(map);
    }

    [Fact]
    public void Update_WhenExitingNoclipWithVerticalLeaningMoveDirection_PreservesGroundedHorizontalSpeed()
    {
        using PhysicsIntegrator physics = new();
        PhysicsFactory factory = new();
        RigidBody body = factory.CreatePlayerBody(new BulletSharp.Math.Vector3(5f, 1.5f, 5f));
        physics.RegisterPlayerBody(body, PhysicsBodyOwnership.IntegratorOwned);
        PlayerMotor motor = new(physics);
        PlayerSimulationTuning tuning = CreateTuning();

        TkVector3 position = GetBodyPosition(body);
        motor.Update(body, position, new MovementCommand(TkVector3.Zero, ToggleNoclipPressed: true), tuning, 0.016f);

        position = GetBodyPosition(body);
        motor.Update(body, position, new MovementCommand(TkVector3.Zero, ToggleNoclipPressed: false), tuning, 0.016f);

        TkVector3 noclipForward = new TkVector3(0f, -0.99f, -0.1f).Normalized();
        position = GetBodyPosition(body);
        motor.Update(
            body,
            position,
            new MovementCommand(noclipForward, ToggleNoclipPressed: true),
            tuning,
            0.016f);

        TkVector3 velocity = body.LinearVelocity.ToOpenTK();
        Assert.InRange(MathF.Abs(velocity.Z), tuning.MoveSpeed * 0.95f, tuning.MoveSpeed * 1.05f);
    }

    private static PlayerSimulationTuning CreateTuning()
    {
        return new PlayerSimulationTuning(
            EyeHeightOffset: 0.35f,
            MouseSensitivity: 2.5f,
            MoveSpeed: 4f,
            NoclipSpeed: 18f,
            JumpVelocity: 5f);
    }

    private static TkVector3 GetBodyPosition(RigidBody body)
    {
        body.MotionState.GetWorldTransform(out Matrix transform);
        return transform.Origin.ToOpenTK();
    }
}
