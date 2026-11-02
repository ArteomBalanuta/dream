using OpenTK.Mathematics;
using Xunit;
using Dream.Client.Audio;

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
        // 4 m/s for 4 s is 16 m: ten 1.6 m strides.
        Assert.InRange(CountSteps(new FootstepCadence(), 4f, 4f), 9, 10);
    }

    [Fact]
    public void Sprinting_Uses_The_Longer_Stride()
    {
        // 11 m/s for 2 s is 22 m: ten 2.2 m strides, fewer than the 13 a walking stride would give.
        int steps = CountSteps(new FootstepCadence(), 11f, 2f);
        Assert.InRange(steps, 9, 10);
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
