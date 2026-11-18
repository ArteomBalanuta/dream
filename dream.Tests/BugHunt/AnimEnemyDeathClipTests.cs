using OpenTK.Mathematics;
using Xunit;
using Dream.Rendering.Actors;
using Dream.Rendering.Models;
using Dream.Simulation.Enemies;

namespace Dream.Tests.BugHunt;

// BH-ANIM-2: every enemy's death clip was unreachable.
//
// EnemyClipSet.Resolve(EnemyBehaviorState.Dead) returns the Die clip, but nothing could ever
// evaluate it: an enemy leaves the client the frame it dies (RemoteEnemyStateApplier drops an actor
// the snapshot no longer lists as live, WorldSessionPresentationState.SelectActiveEnemies filters
// out one killed locally, and EnemyRenderer skips !IsActive), so a wolf vanished in one frame and
// its authored 1.3 s death animation never played.
//
// The fix is a render-only lifetime for the dying MODEL (EnemyDeathLinger), not a longer life for
// the entity: the client's actor-pruning invariant is that existence may survive a withholding
// snapshot but a pose may not, and a guard that held stale interpolation buffers alive regressed
// into a wolf rendering at its spawn point for ~60 snapshots. So a dying model keeps no actor, no
// visual state, no interpolation buffer and no pose source - only the matrix it was last drawn at.
//
// Headless: real wolf and hound skeletons over AnimatedModelData.Load, no GL context.
public class AnimEnemyDeathClipTests
{
    private const float FrameSeconds = 1f / 60f;

    private static readonly Lazy<AnimatedModelData> Wolf = new(() => AnimatedModelData.Load(WolfAssetDefinition.ModelPath));
    private static readonly Lazy<AnimatedModelData> Hound = new(() => AnimatedModelData.Load(BeastAssetDefinition.HoundModelPath));

    private static EnemyDeathLinger.LastDrawnEnemy LastDrawn(
        AnimatedModelSkeleton skeleton,
        string modelPath,
        string dieClip,
        Vector3 position) =>
        new(modelPath, "enemy", dieClip, Matrix4.CreateTranslation(position), Vector3.Zero, skeleton);

    private static EnemyDeathLinger.LastDrawnEnemy WolfLastDrawn(Vector3 position) =>
        LastDrawn(Wolf.Value.Skeleton, WolfAssetDefinition.ModelPath, WolfAssetDefinition.DieClip, position);

    // The asset facts the finding rests on: the clip the catalog promises for Dead really is in the
    // shipped GLB, for both enemy models. Without these the linger would have nothing to play.
    [Fact]
    public void TheCatalogsDeathClipExistsInBothShippedModels()
    {
        Assert.Equal(WolfAssetDefinition.DieClip, EnemyVisualCatalog.WolfClips.Resolve(EnemyBehaviorState.Dead));
        Assert.Equal(BeastAssetDefinition.DieClip, EnemyVisualCatalog.HoundClips.Resolve(EnemyBehaviorState.Dead));

        Assert.True(Wolf.Value.Skeleton.TryGetClip(WolfAssetDefinition.DieClip, out _, out float wolfDuration));
        Assert.True(Hound.Value.Skeleton.TryGetClip(BeastAssetDefinition.DieClip, out _, out float houndDuration));
        Assert.True(wolfDuration > 0f);
        Assert.True(houndDuration > 0f);
    }

    [Fact]
    public void AnEnemyThatLeavesTheRegistry_PlaysItsDeathClip()
    {
        EnemyVisualStateCache cache = new();
        EnemyDeathLinger linger = new();
        List<int> departed = [];

        // One frame with the wolf alive and walking.
        EnemyVisualState state = cache.GetOrAdd(5, Wolf.Value.Skeleton);
        EnemyRenderer.AdvanceInstance(state, WolfAssetDefinition.WalkClip, FrameSeconds);
        linger.ObserveDrawn(5, WolfLastDrawn(new Vector3(3f, 0f, 7f)), cache);

        // The next frame has no wolf: it died. The renderer takes the departed enemy's own
        // instance out of its visual state and starts the death clip on it.
        AnimatedModelInstance live = state.Instance!;
        linger.CollectDeparted([], departed);
        Assert.Equal([5], departed);
        AnimatedModelInstance? taken = cache.TakeInstance(5);
        Assert.Same(live, taken);
        linger.Begin(5, WolfLastDrawn(new Vector3(3f, 0f, 7f)), taken!);

        Assert.Equal(1, linger.Count);
        EnemyDeathLinger.DyingModel dying = Assert.Single(linger.Models);
        Assert.Equal(WolfAssetDefinition.DieClip, dying.Instance.CurrentClipName);
        Assert.True(dying.Instance.Playback.IsPlaying);
        // Nothing allocated and nothing pooled for the death: it is the same instance.
        Assert.Equal(0, cache.PooledInstanceCount);
        Assert.Equal(0, cache.Count);
    }

    // The corpse does not loop back into a gait: the death clip is a one-shot with no idle to
    // return to, so it holds its last pose for the one frame before it is released.
    [Fact]
    public void TheDeathClipPlaysThrough_AndTheModelIsReleasedWhenItEnds()
    {
        EnemyVisualStateCache cache = new();
        EnemyDeathLinger linger = new();
        Assert.True(Wolf.Value.Skeleton.TryGetClip(WolfAssetDefinition.DieClip, out _, out float duration));

        linger.Begin(5, WolfLastDrawn(Vector3.Zero), cache.RentInstance(Wolf.Value.Skeleton));
        EnemyDeathLinger.DyingModel dying = Assert.Single(linger.Models);

        int frames = 0;
        int framesWhileStillPlaying = 0;
        while (linger.Count > 0 && frames < 600)
        {
            if (dying.Instance.Playback.IsPlaying)
            {
                framesWhileStillPlaying++;
                Assert.Equal(WolfAssetDefinition.DieClip, dying.Instance.CurrentClipName);
            }

            linger.Advance(FrameSeconds, cache);
            frames++;
        }

        // Released, and released from the clip's own length rather than from the backstop cap.
        Assert.Equal(0, linger.Count);
        Assert.True(framesWhileStillPlaying >= (int)(duration / FrameSeconds) - 1, $"only {framesWhileStillPlaying} frames played of a {duration:F2} s clip");
        Assert.True(frames * FrameSeconds < EnemyDeathLinger.MaxLingerSeconds, $"took {frames * FrameSeconds:F2} s, the cap is {EnemyDeathLinger.MaxLingerSeconds}");
        // No leak: the instance went back to the pool for the next enemy.
        Assert.Equal(1, cache.PooledInstanceCount);
    }

    // Control: an enemy that is still in this frame's set is a live entity and is not a corpse.
    [Fact]
    public void ALiveEnemyIsStillDrawnAsItself_AndNeverStartsDying()
    {
        EnemyVisualStateCache cache = new();
        EnemyDeathLinger linger = new();
        List<int> departed = [];

        for (int frame = 0; frame < 30; frame++)
        {
            EnemyVisualState state = cache.GetOrAdd(5, Wolf.Value.Skeleton);
            state.ObserveMotion(new Vector3(frame * 0.08f, 0f, 0f), FrameSeconds);
            EnemyRenderer.AdvanceInstance(state, WolfAssetDefinition.WalkClip, FrameSeconds);
            linger.ObserveDrawn(5, WolfLastDrawn(new Vector3(frame * 0.08f, 0f, 0f)), cache);
            cache.PruneExcept([5]);
            linger.CollectDeparted([5], departed);
            Assert.Empty(departed);
            linger.Advance(FrameSeconds, cache);
        }

        Assert.Equal(0, linger.Count);
        Assert.Equal(1, cache.Count);
        EnemyVisualState live = cache.GetOrAdd(5, Wolf.Value.Skeleton);
        Assert.Equal(WolfAssetDefinition.WalkClip, live.Instance!.CurrentClipName);
        Assert.True(live.Instance.Playback.IsPlaying);
    }

    // A dying model is not an entity: it has no visual state, so no gait is measured for it and
    // PruneExcept neither sees it nor is confused by it, and it is not in the renderer's live set.
    [Fact]
    public void ADyingModelIsNotAnEntity_AndHoldsNoVisualStateAndNoPose()
    {
        EnemyVisualStateCache cache = new();
        EnemyDeathLinger linger = new();

        EnemyRenderer.AdvanceInstance(cache.GetOrAdd(5, Wolf.Value.Skeleton), WolfAssetDefinition.WalkClip, FrameSeconds);
        Matrix4 lastMatrix = Matrix4.CreateTranslation(new Vector3(3f, 0f, 7f));
        linger.ObserveDrawn(5, WolfLastDrawn(new Vector3(3f, 0f, 7f)), cache);

        linger.Begin(5, WolfLastDrawn(new Vector3(3f, 0f, 7f)), cache.TakeInstance(5)!);
        Assert.True(linger.IsDying(5));
        // Taking the instance dropped the visual state, nothing recreated one, and the last-drawn
        // record was consumed by Begin, so a corpse cannot depart again and start another corpse.
        Assert.Equal(0, cache.Count);
        cache.PruneExcept([]);
        Assert.Equal(0, cache.Count);
        Assert.Equal(0, cache.PooledInstanceCount);
        Assert.Equal(0, linger.LastDrawnCount);

        // The matrix is frozen at the last pose that was actually drawn, for every frame of the
        // clip: there is no pose source behind it to move it and no buffer held alive for it.
        for (int frame = 0; frame < 20 && linger.Count > 0; frame++)
        {
            foreach (EnemyDeathLinger.DyingModel dying in linger.Models)
            {
                Assert.Equal(lastMatrix, dying.Last.ModelMatrix);
            }

            linger.Advance(FrameSeconds, cache);
        }
    }

    // The server reuses enemy actor ids. A respawn under a dying id must draw the new enemy, not a
    // corpse underneath it.
    [Fact]
    public void ARespawnUnderTheSameActorId_ReleasesTheCorpseAtOnce()
    {
        EnemyVisualStateCache cache = new();
        EnemyDeathLinger linger = new();

        linger.Begin(5, WolfLastDrawn(Vector3.Zero), cache.RentInstance(Wolf.Value.Skeleton));
        Assert.Equal(1, linger.Count);

        EnemyVisualState respawned = cache.GetOrAdd(5, Wolf.Value.Skeleton);
        EnemyRenderer.AdvanceInstance(respawned, WolfAssetDefinition.IdleClip, FrameSeconds);
        linger.ObserveDrawn(5, WolfLastDrawn(new Vector3(1f, 0f, 1f)), cache);

        Assert.Equal(0, linger.Count);
        Assert.False(linger.IsDying(5));
        Assert.Equal(WolfAssetDefinition.IdleClip, respawned.Instance!.CurrentClipName);
    }

    // A clip that can never report itself finished (the model was reloaded under the corpse, so
    // playback stops) must not keep a model alive. Two frames, not forever.
    [Fact]
    public void AModelWhoseClipStops_IsReleasedWithinTwoFrames()
    {
        EnemyVisualStateCache cache = new();
        EnemyDeathLinger linger = new();

        linger.Begin(5, WolfLastDrawn(Vector3.Zero), cache.RentInstance(Wolf.Value.Skeleton));
        EnemyDeathLinger.DyingModel dying = Assert.Single(linger.Models);
        dying.Instance.StopAnimation();

        linger.Advance(FrameSeconds, cache);
        Assert.True(dying.ClipEnded);
        linger.Advance(FrameSeconds, cache);
        Assert.Equal(0, linger.Count);
        Assert.Equal(1, cache.PooledInstanceCount);
    }

    // Many deaths at once are all released, and the pool never grows past its own bound.
    [Fact]
    public void AWaveOfDeaths_IsAllReleased_AndLeaksNoModels()
    {
        EnemyVisualStateCache cache = new();
        EnemyDeathLinger linger = new();

        for (int actorId = 0; actorId < 40; actorId++)
        {
            linger.Begin(actorId, WolfLastDrawn(new Vector3(actorId, 0f, 0f)), cache.RentInstance(Wolf.Value.Skeleton));
        }

        Assert.Equal(40, linger.Count);
        for (int frame = 0; frame < 400 && linger.Count > 0; frame++)
        {
            linger.Advance(FrameSeconds, cache);
        }

        Assert.Equal(0, linger.Count);
        Assert.Equal(40, cache.PooledInstanceCount);

        linger.Clear(cache);
        Assert.Equal(0, linger.Count);
        Assert.Equal(0, linger.LastDrawnCount);
    }
}
