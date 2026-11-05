using SharpGLTF.Schema2;
using OpenTK.Mathematics;
using Xunit;
using Dream.Client.Audio;
using Dream.Rendering.Actors;
using Dream.Rendering.Models;

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
        // The walk tier, 1.6 m/s for 4 s: 6.4 m of 0.64 m strides, 2.5 steps/s - the cadence the
        // 0.80 s walk cycle's two footfalls actually show. The old 0.85 m stride gave 1.9 steps/s,
        // and the one before that (1.6 m, a sprint stride played at a walk) gave one footfall every
        // four metres.
        Assert.InRange(CountSteps(new FootstepCadence(), 1.6f, 4f), 9, 10);
    }

    [Fact]
    public void Running_Uses_The_Run_Stride()
    {
        // The run tier, 4.3 m/s for 2 s: 8.6 m of 1.333 m strides, 3.2 steps/s, matching the
        // 0.62 s run cycle. The walking stride would have given thirteen steps at this speed.
        int steps = CountSteps(new FootstepCadence(), 4.3f, 2f);
        Assert.InRange(steps, 6, 7);
    }

    [Fact]
    public void Sprinting_Uses_The_Longer_Stride()
    {
        // The sprint tier, 6 m/s for 2 s: 12 m of 1.32 m strides, 4.5 steps/s, matching the 0.44 s
        // sprint cycle (the count lands a little under 9 because the accumulator drops the
        // remainder of the frame a step lands on). The stride is a hair SHORTER than the run's -
        // see FootstepCadence for why that is the arithmetic - and the sprint still has the fastest
        // cadence by a wide margin because it covers far more ground per second.
        int steps = CountSteps(new FootstepCadence(), 6f, 2f);
        Assert.InRange(steps, 8, 9);

        Assert.True(6f / FootstepCadence.SprintStride > 4.3f / FootstepCadence.RunStride);
        Assert.True(4.3f / FootstepCadence.RunStride > 1.6f / FootstepCadence.WalkStride);
    }

    [Fact]
    public void TheStrideTiersMatchTheAnimationTiers()
    {
        Assert.True(FootstepCadence.WalkStride < FootstepCadence.RunStride);
        Assert.Equal(
            PlayerAnimationStateMachine.RunSpeedThreshold, FootstepCadence.RunSpeedThreshold, 4);
        Assert.Equal(
            PlayerAnimationStateMachine.SprintSpeedThreshold, FootstepCadence.SprintSpeedThreshold, 4);
    }

    // The guard that was missing when this class shipped, and the reason it shipped wrong: the tier
    // SPEEDS were pinned against PlayerSimulationTuning, but nothing pinned the strides against the
    // CLIPS, so every tier's audio came out slower than its legs (the sprint by 52%). A stride is
    // not a free number: at the authored rate the body covers
    // referenceSpeed * cycle / FootfallsPerCycle metres between footfalls, and anything else is a
    // foot that lands off its sound. The cycle lengths are read back off the SHIPPED player GLB
    // rather than copied out of gen_char_player.py, so re-timing a clip without re-deriving its
    // stride fails here.
    public static TheoryData<string, float, float> StrideTiers => new()
    {
        { PlayerAnimationStateMachine.WalkClip, PlayerAnimationStateMachine.ReferenceWalkSpeed, FootstepCadence.WalkStride },
        { PlayerAnimationStateMachine.RunClip, PlayerAnimationStateMachine.ReferenceRunSpeed, FootstepCadence.RunStride },
        { PlayerAnimationStateMachine.SprintClip, PlayerAnimationStateMachine.ReferenceSprintSpeed, FootstepCadence.SprintStride }
    };

    [Theory]
    [MemberData(nameof(StrideTiers))]
    public void TheStridesMatchTheAuthoredClipCadence(string clipName, float referenceSpeed, float stride)
    {
        AnimatedModelData data = AnimatedModelData.Load(PlayerModelAssetDefinition.ModelPath);

        Assert.True(data.Skeleton.TryGetClip(clipName, out Animation? _, out float cycleSeconds));
        Assert.True(cycleSeconds > 0f);

        float derived = referenceSpeed * cycleSeconds / FootstepCadence.FootfallsPerCycle;
        Assert.Equal(derived, stride, 2);

        // Said the other way round, which is the way a player hears it: the footfalls per second
        // the audio produces at the tier's speed equal the footfalls per second the legs show.
        Assert.Equal(FootstepCadence.FootfallsPerCycle / cycleSeconds, referenceSpeed / stride, 2);
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
