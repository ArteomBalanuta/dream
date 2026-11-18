using BulletSharp;
using Dream.Simulation.Physics;
using Dream.Simulation.Players;
using OpenTK.Mathematics;
using Xunit;
using TkVector3 = OpenTK.Mathematics.Vector3;

namespace Dream.Tests.BugHunt;

// BH-SIM2-1: PlayerMotor's freeze stored body.InvMass into _originalMass and restored it as the
// mass. With the shipped player body (mass 1) that round trip is accidentally exact, so every
// existing test passes either way; these use a body whose mass is not 1.
public class Sim2PlayerMotorFreezeMassTests
{
    private const float HeavyMass = 70f;

    [Fact]
    public void FreezeThenUnfreeze_OnAHeavyBody_RestoresTheSameMass()
    {
        using PhysicsIntegrator physics = new();
        RigidBody body = CreateHeavyPlayerBody(physics);
        PlayerMotor motor = new(physics);
        PlayerSimulationTuning tuning = CreateTuning();

        float massBefore = 1f / body.InvMass;
        BulletSharp.Math.Vector3 inertiaBefore = body.LocalInertia;
        Assert.Equal(HeavyMass, massBefore, 3);

        // Freeze.
        motor.Update(body, Position(body), new MovementCommand(TkVector3.Zero, FreezePressed: true), tuning, 0.016f);
        Assert.True(motor.IsFrozen);
        Assert.Equal(0f, body.InvMass);

        // Release the key, then press it again to unfreeze (the toggle is edge triggered).
        motor.Update(body, Position(body), new MovementCommand(TkVector3.Zero), tuning, 0.016f);
        motor.Update(body, Position(body), new MovementCommand(TkVector3.Zero, FreezePressed: true), tuning, 0.016f);

        Assert.False(motor.IsFrozen);
        // Before the fix the body came back at mass 1/70, i.e. InvMass 70.
        Assert.Equal(HeavyMass, 1f / body.InvMass, 3);
        Assert.Equal(inertiaBefore.X, body.LocalInertia.X, 3);
        Assert.Equal(inertiaBefore.Y, body.LocalInertia.Y, 3);
        Assert.Equal(inertiaBefore.Z, body.LocalInertia.Z, 3);
    }

    [Fact]
    public void ResetSeatState_WhileFrozen_HandsTheNextOccupantTheOriginalMass()
    {
        using PhysicsIntegrator physics = new();
        RigidBody body = CreateHeavyPlayerBody(physics);
        PlayerMotor motor = new(physics);
        PlayerSimulationTuning tuning = CreateTuning();

        motor.Update(body, Position(body), new MovementCommand(TkVector3.Zero, FreezePressed: true), tuning, 0.016f);
        Assert.Equal(0f, body.InvMass);

        motor.ResetSeatState(body);

        Assert.False(motor.IsFrozen);
        Assert.Equal(HeavyMass, 1f / body.InvMass, 3);
    }

    private static RigidBody CreateHeavyPlayerBody(PhysicsIntegrator physics)
    {
        PhysicsFactory factory = new();
        RigidBody body = factory.CreatePlayerBody(new BulletSharp.Math.Vector3(5f, 1.5f, 5f));
        physics.RegisterPlayerBody(body, PhysicsBodyOwnership.IntegratorOwned);
        // A heavier character than the shipped 1 kg body: mass and inverse mass now differ.
        body.SetMassProps(HeavyMass, body.LocalInertia);
        return body;
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

    private static TkVector3 Position(RigidBody body)
    {
        body.MotionState.GetWorldTransform(out BulletSharp.Math.Matrix transform);
        return transform.Origin.ToOpenTK();
    }
}
