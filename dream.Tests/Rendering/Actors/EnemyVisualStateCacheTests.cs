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
