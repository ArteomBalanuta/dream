using OpenTK.Mathematics;
using Xunit;
using Dream.Client.Audio;
using Dream.Rendering.Actors;

namespace Dream.Tests.Client.Audio;

public class FootstepCadenceTests
{
    private static int CountSteps(FootstepCadence cadence, float speed, float seconds, bool grounded = true)
    {
        const float Dt = 1f / 60f;
        int steps = 0;
        float x = 0f;
        cadence.Advance(new Vector3(x, 0, 0), grounded, Dt);
        for (float t = 0; t < seconds; t += Dt)
        {
            x += speed * Dt;
            if (cadence.Advance(new Vector3(x, 0, 0), grounded, Dt))
            {
                steps++;
            }
        }

        return steps;
    }

    [Fact]
    public void Walking_Steps_Once_Per_Stride()
    {
        // The walk tier, 1.6 m/s for 4 s: 6.4 m of 0.85 m strides, about 1.9 steps/s - real walking
        // cadence, which the old 1.6 m stride (one footfall every four metres) was nowhere near.
        Assert.InRange(CountSteps(new FootstepCadence(), 1.6f, 4f), 7, 8);
    }

    [Fact]
    public void Running_Uses_The_Run_Stride()
    {
        // The run tier, 4.3 m/s for 2 s: 8.6 m of 1.5 m strides, about 2.9 steps/s. The walking
        // stride would have given ten steps at this speed.
        int steps = CountSteps(new FootstepCadence(), 4.3f, 2f);
        Assert.InRange(steps, 5, 6);
    }

    [Fact]
    public void Sprinting_Uses_The_Longer_Stride()
    {
        // The sprint tier, 6 m/s for 2 s: 12 m of 2.0 m strides, fewer than the 8 the run stride
        // would give over the same ground.
        int steps = CountSteps(new FootstepCadence(), 6f, 2f);
        Assert.InRange(steps, 5, 6);
    }

    [Fact]
    public void TheStrideTiersMatchTheAnimationTiers()
    {
        Assert.True(FootstepCadence.WalkStride < FootstepCadence.RunStride);
        Assert.True(FootstepCadence.RunStride < FootstepCadence.SprintStride);
        Assert.Equal(
            PlayerAnimationStateMachine.RunSpeedThreshold, FootstepCadence.RunSpeedThreshold, 4);
        Assert.Equal(
            PlayerAnimationStateMachine.SprintSpeedThreshold, FootstepCadence.SprintSpeedThreshold, 4);
    }

    [Fact]
    public void Standing_Still_And_Being_Airborne_Make_No_Steps()
    {
        Assert.Equal(0, CountSteps(new FootstepCadence(), 0f, 3f));
        Assert.Equal(0, CountSteps(new FootstepCadence(), 5f, 3f, grounded: false));
    }

    [Fact]
    public void A_Teleport_Does_Not_Count_As_Walking()
    {
        FootstepCadence cadence = new();
        cadence.Advance(Vector3.Zero, true, 0.016f);
        Assert.False(cadence.Advance(new Vector3(500, 0, 0), true, 0.016f));
        Assert.False(cadence.Advance(new Vector3(500.01f, 0, 0), true, 0.016f));
    }

    [Fact]
    public void Non_Finite_Positions_Are_Ignored()
    {
        FootstepCadence cadence = new();
        Assert.False(cadence.Advance(new Vector3(float.NaN, 0, 0), true, 0.016f));
    }
}
