using OpenTK.Mathematics;
using Xunit;
using Dream.Rendering.Actors;
using Dream.Simulation.Enemies;

namespace Dream.Tests.Rendering.Actors;


public class EnemyVisualStateCacheTests
{
    [Fact]
    public void GetOrAdd_ReturnsSameInstanceForSameId()
    {
        EnemyVisualStateCache cache = new();

        EnemyVisualState first = cache.GetOrAdd(2000);
        EnemyVisualState second = cache.GetOrAdd(2000);

        Assert.Same(first, second);
        Assert.NotSame(first, cache.GetOrAdd(2001));
    }

    [Fact]
    public void PruneExcept_RemovesDespawnedIdsAndKeepsCurrent()
    {
        EnemyVisualStateCache cache = new();
        EnemyVisualState kept = cache.GetOrAdd(1);
        cache.GetOrAdd(2);

        cache.PruneExcept(new HashSet<int> { 1 });

        Assert.Equal(1, cache.Count);
        Assert.Same(kept, cache.GetOrAdd(1));
    }

    [Fact]
    public void GetOrAdd_AfterPrune_ReturnsFreshState()
    {
        EnemyVisualStateCache cache = new();
        EnemyVisualState old = cache.GetOrAdd(7);

        cache.PruneExcept(new HashSet<int>());
        EnemyVisualState respawned = cache.GetOrAdd(7);

        Assert.NotSame(old, respawned);
    }

    // ---- the measured gait (contract C-F) ----
    // The client holds no speed for an enemy, so these are the only numbers a gait can be chosen from.

    [Fact]
    public void ObserveMotion_MeasuresTheHorizontalSpeedOfTheDrawnPose()
    {
        EnemyVisualState state = new();
        const float dt = 1f / 60f;
        const float speed = 5f;

        Assert.Equal(0f, state.ObservedSpeed);
        // The first observation only records where the enemy is; there is no step to measure yet.
        state.ObserveMotion(Vector3.Zero, dt);
        Assert.Equal(0f, state.ObservedSpeed);

        Vector3 position = Vector3.Zero;
        for (int i = 0; i < 240; i++)
        {
            position += new Vector3(speed * dt, 0f, 0f);
            state.ObserveMotion(position, dt);
        }

        Assert.Equal(speed, state.ObservedSpeed, 3);
    }

    [Fact]
    public void ObserveMotion_IgnoresVerticalMotion_ZeroDeltas_AndTeleports()
    {
        EnemyVisualState state = new();
        const float dt = 1f / 60f;

        state.ObserveMotion(Vector3.Zero, dt);
        // A lift straight up is not travel.
        state.ObserveMotion(new Vector3(0f, 4f, 0f), dt);
        Assert.Equal(0f, state.ObservedSpeed, 4);

        // A paused frame cannot divide by zero.
        state.ObserveMotion(new Vector3(1f, 4f, 0f), 0f);
        Assert.Equal(0f, state.ObservedSpeed, 4);

        // Settle on a real speed, then snap across the floor: the jump must not become a gallop.
        Vector3 position = new(1f, 4f, 0f);
        for (int i = 0; i < 240; i++)
        {
            position += new Vector3(2f * dt, 0f, 0f);
            state.ObserveMotion(position, dt);
        }

        Assert.Equal(2f, state.ObservedSpeed, 3);
        state.ObserveMotion(position + new Vector3(50f, 0f, 0f), dt);
        Assert.Equal(2f, state.ObservedSpeed, 3);
    }

    [Fact]
    public void ResolveGaitSpeed_LatchesTheGait_SoADitheringSpeedDoesNotRenameTheClip()
    {
        EnemyVisualState state = new();
        EnemyClipSet clips = EnemyVisualCatalog.WolfClips;
        const float dt = 1f / 60f;

        // Walked up to just under the threshold: still the walk.
        Vector3 position = Vector3.Zero;
        state.ObserveMotion(position, dt);
        for (int i = 0; i < 240; i++)
        {
            position += new Vector3((clips.SprintSpeedThreshold - 0.1f) * dt, 0f, 0f);
            state.ObserveMotion(position, dt);
        }

        float walkGait = state.ResolveGaitSpeed(in clips, EnemyBehaviorState.Chasing);
        Assert.Equal(clips.Move, clips.Resolve(EnemyBehaviorState.Chasing, walkGait));

        // Up to a real chase: the gallop.
        for (int i = 0; i < 240; i++)
        {
            position += new Vector3(5f * dt, 0f, 0f);
            state.ObserveMotion(position, dt);
        }

        float chaseGait = state.ResolveGaitSpeed(in clips, EnemyBehaviorState.Chasing);
        Assert.Equal(clips.Sprint, clips.Resolve(EnemyBehaviorState.Chasing, chaseGait));
        Assert.Equal(1f, clips.ResolvePlaybackSpeed(EnemyBehaviorState.Chasing, chaseGait), 2);

        // Now dither back inside the band. The latch keeps the gallop, so the clip name - and with it
        // the clip time, which a rename resets - does not change.
        for (int i = 0; i < 240; i++)
        {
            float sample = clips.SprintSpeedThreshold - 0.2f + 0.1f * MathF.Sin(i);
            position += new Vector3(sample * dt, 0f, 0f);
            state.ObserveMotion(position, dt);
            float gait = state.ResolveGaitSpeed(in clips, EnemyBehaviorState.Chasing);
            Assert.Equal(clips.Sprint, clips.Resolve(EnemyBehaviorState.Chasing, gait));
        }

        // Standing still drops the latch, and a stopped enemy is idle anyway.
        Assert.Equal(clips.Idle, clips.Resolve(
            EnemyBehaviorState.Idle,
            state.ResolveGaitSpeed(in clips, EnemyBehaviorState.Idle)));
        for (int i = 0; i < 240; i++)
        {
            state.ObserveMotion(position, dt);
        }

        float stoppedGait = state.ResolveGaitSpeed(in clips, EnemyBehaviorState.Chasing);
        Assert.Equal(clips.Move, clips.Resolve(EnemyBehaviorState.Chasing, stoppedGait));
    }

    // F4: the snap guard is a SPEED, not a distance. A 2.9 m step passed the old 3 m distance gate,
    // but at 60 fps it is a 174 m/s sample, and with SpeedSmoothing 0.5 one of those stays above the
    // 3.0 m/s gait threshold for about six frames (174, 87, 43, 22, 11, 5.4, 2.7) while
    // ResolveSprinting's latch holds the gallop until it falls below 2.6: a gallop flash on a
    // walking wolf. Those samples are real - when ClientScenePresenter.ApplyEnemySample has just
    // cleared the interpolation buffer, EnemyRenderer falls back to the snapshot-quantised
    // actor.Position and the measurement becomes a train of spikes and zeros.
    //
    // This test FAILS against the distance-only guard: it is the whole discrimination.
    [Fact]
    public void ObserveMotion_DiscardsASpikeSampleUnderTheOldDistanceGate_SoTheGallopDoesNotFlash()
    {
        EnemyVisualState state = new();
        EnemyClipSet clips = EnemyVisualCatalog.WolfClips;
        const float dt = 1f / 60f;
        const float walkSpeed = 1.4f;

        Vector3 position = Vector3.Zero;
        state.ObserveMotion(position, dt);
        for (int i = 0; i < 240; i++)
        {
            position += new Vector3(walkSpeed * dt, 0f, 0f);
            state.ObserveMotion(position, dt);
        }

        Assert.Equal(walkSpeed, state.ObservedSpeed, 3);
        Assert.Equal(clips.Move, clips.Resolve(EnemyBehaviorState.Chasing, state.ResolveGaitSpeed(in clips, EnemyBehaviorState.Chasing)));

        // One frame of snapped pose: 2.9 m in a 60 fps frame, under the old 3 m distance gate and
        // 174 m/s of implied speed.
        position += new Vector3(2.9f, 0f, 0f);
        state.ObserveMotion(position, dt);
        Assert.True(2.9f < 3f, "the spike has to be under the old distance gate or it proves nothing");

        // Then the walk resumes. Not one of these frames may show the gallop.
        for (int i = 0; i < 30; i++)
        {
            float gait = state.ResolveGaitSpeed(in clips, EnemyBehaviorState.Chasing);
            Assert.Equal(clips.Move, clips.Resolve(EnemyBehaviorState.Chasing, gait));
            Assert.True(
                state.ObservedSpeed < clips.SprintSpeedThreshold,
                $"frame {i}: the spike leaked into the measured speed ({state.ObservedSpeed} m/s)");
            position += new Vector3(walkSpeed * dt, 0f, 0f);
            state.ObserveMotion(position, dt);
        }
    }

    // And the gate is wide enough that nothing an enemy actually does is thrown away: every chase
    // speed in the catalog has room to spare under it.
    [Fact]
    public void TheSnapGuard_IsAboveEveryChaseSpeedInTheCatalog()
    {
        foreach (EnemyClipSet clips in new[] { EnemyVisualCatalog.WolfClips, EnemyVisualCatalog.HoundClips })
        {
            Assert.True(
                EnemyVisualState.MaxObservedSpeed >= 2f * clips.SprintReferenceSpeed,
                $"the snap guard {EnemyVisualState.MaxObservedSpeed} m/s is too close to a chase at {clips.SprintReferenceSpeed} m/s");
        }

        // A real gallop is measured, not discarded.
        EnemyVisualState state = new();
        const float dt = 1f / 60f;
        Vector3 position = Vector3.Zero;
        state.ObserveMotion(position, dt);
        for (int i = 0; i < 240; i++)
        {
            position += new Vector3(6f * dt, 0f, 0f);
            state.ObserveMotion(position, dt);
        }

        Assert.Equal(6f, state.ObservedSpeed, 3);
    }

    [Fact]
    public void ObserveMotion_AllocatesNothing()
    {
        EnemyVisualState state = new();
        EnemyClipSet clips = EnemyVisualCatalog.WolfClips;
        state.ObserveMotion(Vector3.Zero, 1f / 60f);

        Dream.Tests.Utilities.AllocationAssert.AtMost(0, () =>
        {
            for (int i = 0; i < 64; i++)
            {
                state.ObserveMotion(new Vector3(i * 0.05f, 0f, 0f), 1f / 60f);
                _ = state.ResolveGaitSpeed(in clips, EnemyBehaviorState.Chasing);
            }
        });
    }
}
