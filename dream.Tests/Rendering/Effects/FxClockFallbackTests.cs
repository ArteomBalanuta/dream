using Dream.Rendering.Effects;
using Dream.Rendering.Lighting;
using Dream.Rendering.World;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Rendering.Effects;

// FxFrameInputs.ClockSeconds defaults to NaN, meaning the frame supplies no presentation clock, and the
// simulation then counts its own time (wrapping at 4096 s). The finite path is covered by
// FireFlickerTests.ABrazierHalo_AndItsLight_AreDrivenByTheSameClock; these cover the unset path.
// The halo of a plain lamp light is the probe: its size is 1.25 * (1 + 0.35 * (flicker - 1)), where
// flicker is FireFlicker at the simulation's clock.
public class FxClockFallbackTests
{
    private const float Dt = 1f / 60f;
    private static readonly Vector3 Centre = new(10.5f, 3.5f, 5.5f);
    private static readonly Vector3 Camera = new(10f, 3f, 12f);
    private static readonly FxViewBasis Basis = new(Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ);
    private static readonly PointLight[] Lamp = [new PointLight(Centre, LampLightIndex.LampRadius, LampLightIndex.LampColor)];

    private static float Step(FxSimulation simulation, float dt, double clock)
    {
        FxFrameInputs inputs = new(dt, Camera, -Vector3.UnitZ, Camera, 1f, Basis, clock);
        simulation.Advance(in inputs, Lamp);
        FxInstance[] instances = new FxInstance[FxLimits.MaxInstances];
        int count = simulation.WriteInstances(instances);
        return instances.Take(count).Single(i => i.Frame == (float)FxFrame.Halo).HalfSize;
    }

    private static float Expected(double clock)
    {
        float flicker = FireFlicker.Sample(FireFlickerKind.Lamp, Centre, clock, 0).Intensity;
        return 1.25f * (1f + (0.35f * (flicker - 1f)));
    }

    [Fact]
    public void An_Unset_Clock_Makes_The_Simulation_Count_Its_Own_Time()
    {
        FxSimulation simulation = new(1u);
        double own = 0.0;
        float first = float.NaN;
        bool moved = false;
        for (int i = 0; i < 120; i++)
        {
            own += Dt;
            float half = Step(simulation, Dt, double.NaN);
            Assert.Equal(Expected(own), half, 4);
            first = i == 0 ? half : first;
            moved |= MathF.Abs(half - first) > 1e-3f;
        }

        Assert.True(moved, "an unset clock must still advance the flicker");
    }

    [Theory]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void An_Infinite_Clock_Is_Treated_As_Unset_Not_As_A_Frozen_Flicker(double bad)
    {
        FxSimulation unset = new(1u);
        FxSimulation infinite = new(1u);
        for (int i = 0; i < 60; i++)
        {
            Assert.Equal(Step(unset, Dt, double.NaN), Step(infinite, Dt, bad));
        }
    }

    [Fact]
    public void A_Finite_Zero_Is_A_Real_Clock_And_Pins_The_Flicker()
    {
        FxSimulation simulation = new(1u);
        float a = Step(simulation, Dt, 0.0);
        float b = Step(simulation, Dt, 0.0);
        Assert.Equal(Expected(0.0), a, 4);
        Assert.Equal(a, b);
    }

    [Fact]
    public void Dropping_The_Clock_Resumes_From_The_Last_Finite_Value_And_Wraps_At_4096()
    {
        FxSimulation simulation = new(1u);
        Step(simulation, Dt, 2.5);
        Assert.Equal(Expected(2.5 + Dt), Step(simulation, Dt, double.NaN), 4);

        FxSimulation nearWrap = new(1u);
        Step(nearWrap, Dt, 4095.95);
        double resumed = 4095.95 + 0.1f;
        Assert.True(resumed >= 4096.0);
        // dt is clamped to MaxDeltaSeconds (0.1), so a 0.5 s frame advances the clock by 0.1 only.
        Assert.Equal(Expected(resumed - 4096.0), Step(nearWrap, 0.5f, double.NaN), 4);
    }
}
