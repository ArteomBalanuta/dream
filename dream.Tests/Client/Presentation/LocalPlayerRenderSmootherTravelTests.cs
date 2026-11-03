using Dream.Client.Presentation;
using Dream.Rendering.Actors;
using Dream.Simulation.Runtime;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Client.Presentation;

// The travel direction the local render pose carries for the leg twist.
public sealed class LocalPlayerRenderSmootherTravelTests
{
    private const float TickSeconds = SimulationClock.FixedDeltaSeconds;
    private const float Frame = 1f / 60f;

    private static (LocalPlayerRenderSmoother Smoother, Vector3 Body) Walking(Vector3 direction, int ticks)
    {
        LocalPlayerRenderSmoother smoother = new();
        Vector3 body = Vector3.Zero;
        smoother.RecordTick(body, body, TickSeconds);
        for (int i = 0; i < ticks; i++)
        {
            Vector3 next = body + (direction * 5.5f * TickSeconds);
            smoother.RecordTick(body, next, TickSeconds);
            body = next;
        }

        return (smoother, body);
    }

    [Fact]
    public void Stationary_HasNoTravelDirection()
    {
        LocalPlayerRenderSmoother smoother = new();
        smoother.RecordTick(Vector3.Zero, Vector3.Zero, TickSeconds);

        LocalPlayerRenderPose pose = smoother.Resolve(Vector3.Zero, 0.5f, Vector3.UnitZ, Frame);

        Assert.Equal(Vector3.Zero, pose.TravelDirection);
    }

    [Fact]
    public void Walking_ReportsAUnitTravelDirection_AndTheTwistIsZeroWhenFacingIt()
    {
        (LocalPlayerRenderSmoother smoother, Vector3 body) = Walking(Vector3.UnitZ, 4);

        LocalPlayerRenderPose pose = smoother.Resolve(body, 0.5f, Vector3.UnitZ, Frame);

        Assert.Equal(1f, pose.TravelDirection.Length, 4);
        Assert.Equal(
            0f,
            PlayerAnimationStateMachine.LegTwist(pose.Facing.X, pose.Facing.Z, pose.TravelDirection.X, pose.TravelDirection.Z),
            3);
    }

    [Fact]
    public void StrafingLeftOfFacing_GivesANegativeTwist()
    {
        // Facing +Z, moving -X.
        (LocalPlayerRenderSmoother smoother, Vector3 body) = Walking(-Vector3.UnitX, 4);

        LocalPlayerRenderPose pose = smoother.Resolve(body, 0.5f, Vector3.UnitZ, Frame);
        float twist = PlayerAnimationStateMachine.LegTwist(pose.Facing.X, pose.Facing.Z, pose.TravelDirection.X, pose.TravelDirection.Z);

        Assert.Equal(-MathF.PI / 2f, twist, 3);
    }

    [Fact]
    public void ADirectionChange_IsSmoothed_NotSnapped()
    {
        (LocalPlayerRenderSmoother smoother, Vector3 body) = Walking(Vector3.UnitZ, 6);
        smoother.Resolve(body, 0.5f, Vector3.UnitZ, Frame);

        // Turn to +X: one frame later the direction has started to move but is not there yet.
        Vector3 next = body + (Vector3.UnitX * 5.5f * TickSeconds);
        smoother.RecordTick(body, next, TickSeconds);
        LocalPlayerRenderPose pose = smoother.Resolve(next, 0.5f, Vector3.UnitZ, Frame);

        Assert.True(pose.TravelDirection.X > 0.01f, $"{pose.TravelDirection}");
        Assert.True(pose.TravelDirection.X < 0.9f, $"{pose.TravelDirection}");
    }

    [Fact]
    public void StoppingZeroesTheDirection_AndTheNextStartSnapsToTheNewOne()
    {
        (LocalPlayerRenderSmoother smoother, Vector3 body) = Walking(Vector3.UnitZ, 6);
        smoother.RecordTick(body, body, TickSeconds);
        Assert.Equal(Vector3.Zero, smoother.Resolve(body, 0.5f, Vector3.UnitZ, Frame).TravelDirection);

        for (int i = 0; i < 4; i++)
        {
            Vector3 next = body + (Vector3.UnitX * 5.5f * TickSeconds);
            smoother.RecordTick(body, next, TickSeconds);
            body = next;
        }

        Vector3 direction = smoother.Resolve(body, 0.5f, Vector3.UnitZ, Frame).TravelDirection;

        Assert.Equal(1f, direction.X, 3);
    }
}
