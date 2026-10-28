using System.Runtime.CompilerServices;
using Xunit;
using Dream.Rendering.Actors;
using Dream.Rendering.Models;
using Dream.Rendering.ViewModels;

namespace Dream.Tests.Rendering.Models;

// Wave 8 tester, GPU-4 (shared AnimatedModelSkeleton, per-actor AnimatedModelInstance) lifecycle
// edges: pooled wolf instances across a model reload, a remote player's handle released and
// requested again, and viewmodel slot swaps. GL is not available, so instances run over skeletons
// from AnimatedModelData.Load; ViewModelSlotAnimation gets an AnimatedModel created without its
// constructor (no GL buffers), which only needs the skeleton for Bind.
public class Wave8AnimatedInstanceLifecycleTests
{
    private const float FrameSeconds = 1f / 60f;

    private static readonly Lazy<AnimatedModelData> Wolf = new(() => AnimatedModelData.Load(WolfAssetDefinition.ModelPath));
    private static readonly Lazy<AnimatedModelData> Player = new(() => AnimatedModelData.Load(PlayerModelAssetDefinition.ModelPath));
    private static readonly Lazy<AnimatedModelData> Hands = new(() => AnimatedModelData.Load(ViewModelAssets.DebugHandsDefinition.ModelPath));
    private static readonly Lazy<AnimatedModelData> HeldItem = new(() => AnimatedModelData.Load(ViewModelAssets.BuilderToolDefinition.ModelPath));

    [Fact]
    public void WolfPool_AcrossAModelReload_NeverHandsAStaleSkeletonInstanceToANewEnemy()
    {
        AnimatedModelSkeleton before = Wolf.Value.Skeleton;
        AnimatedModelSkeleton after = Reload(Wolf.Value, WolfAssetDefinition.ModelPath);
        EnemyVisualStateCache cache = new();

        // Two wolves on the old model; one leaves (its instance is pooled), one stays.
        EnemyRenderer.AdvanceInstance(cache.GetOrAdd(1, before), WolfAssetDefinition.MoveClip, 0.3f);
        EnemyVisualState survivor = cache.GetOrAdd(2, before);
        EnemyRenderer.AdvanceInstance(survivor, WolfAssetDefinition.IdleClip, 0.3f);
        cache.PruneExcept([2]);
        Assert.Equal(1, cache.PooledInstanceCount);

        // ModelAssets.DisposeAll + reload: the survivor rebinds (its old instance goes to the pool),
        // and a new wolf appears. Neither may get an instance of the old skeleton.
        EnemyRenderer.AdvanceInstance(cache.GetOrAdd(2, after), WolfAssetDefinition.IdleClip, FrameSeconds);
        EnemyVisualState arriving = cache.GetOrAdd(3, after);
        EnemyRenderer.AdvanceInstance(arriving, WolfAssetDefinition.MoveClip, 0.1f);

        Assert.Same(after, survivor.Instance!.Skeleton);
        Assert.Same(after, arriving.Instance!.Skeleton);
        Assert.Equal(WolfAssetDefinition.IdleClip, survivor.Instance.CurrentClipName);
        Assert.Equal(0.1f, arriving.Instance.TimeSeconds, 5);
        Assert.NotSame(survivor.Instance, arriving.Instance);

        // The stale instances were dropped from the pool on the way, and pooling of the new
        // skeleton keeps working: a wolf leaving now gives its instance to the next one.
        AnimatedModelInstance reused = arriving.Instance;
        cache.PruneExcept([2]);
        EnemyVisualState next = cache.GetOrAdd(4, after);
        Assert.Same(reused, next.Instance);
        Assert.Null(next.Instance!.CurrentClipName);
        Assert.Equal(0f, next.Instance.TimeSeconds);
        Assert.Equal(0, cache.PooledInstanceCount);
    }

    [Fact]
    public void RemotePlayer_HandleReleasedAndRequestedAgain_ReplaysTheClipOnceAndThenLetsItRun()
    {
        AnimatedModelSkeleton skeleton = Player.Value.Skeleton;
        PlayerAnimationClipTracker tracker = new();
        AnimatedModelInstance first = new(skeleton);
        Assert.True(tracker.TryApplyClip(7, PlayerModelAssetDefinition.WalkClip, first));
        first.Update(0.25f);

        // Far away: RemotePlayerRenderer.ReleaseFarModels disposes the handle (the actor stays
        // active, so the tracker keeps its record). Back in range: a new handle, a new instance.
        AnimatedModelInstance second = new(skeleton);
        Assert.True(tracker.TryApplyClip(7, PlayerModelAssetDefinition.WalkClip, second));
        Assert.True(second.IsPlayingLoop(PlayerModelAssetDefinition.WalkClip));

        // Replayed once, not restarted every frame: the clip time keeps advancing.
        for (int frame = 0; frame < 10; frame++)
        {
            Assert.False(tracker.TryApplyClip(7, PlayerModelAssetDefinition.WalkClip, second));
            second.Update(FrameSeconds);
        }

        Assert.Equal(10 * FrameSeconds, second.TimeSeconds, 4);

        // A clip that changed while the handle was released starts on the next instance.
        AnimatedModelInstance third = new(skeleton);
        Assert.True(tracker.TryApplyClip(7, PlayerModelAssetDefinition.IdleClip, third));
        Assert.True(third.IsPlayingLoop(PlayerModelAssetDefinition.IdleClip));
    }

    [Fact]
    public void ViewModelSlots_KeepTheirInstanceAcrossSameModelSwaps_AndRebindOnAModelChangeOrReload()
    {
        AnimatedModel hands = ModelWithoutGl(Hands.Value.Skeleton);
        AnimatedModel heldItem = ModelWithoutGl(HeldItem.Value.Skeleton);
        AnimatedModel heldItemReloaded = ModelWithoutGl(Reload(HeldItem.Value, ViewModelAssets.BuilderToolDefinition.ModelPath));
        ViewModelSlotAnimation handsSlot = new();
        ViewModelSlotAnimation heldSlot = new();

        AnimatedModelInstance handsInstance = handsSlot.Bind(hands);
        AnimatedModelInstance heldInstance = heldSlot.Bind(heldItem);
        Assert.NotSame(handsInstance, heldInstance);
        heldInstance.PlayLoop("Idle");
        heldInstance.Update(0.2f);

        // Every built-in held tool uses DebugHeldItem.gltf: swapping tools keeps the instance and
        // its clip time (ViewModelRenderer.ApplyAnimation then restarts only on a clip change).
        Assert.Equal(ViewModelAssets.BuilderToolDefinition.ModelPath, ViewModelAssets.BladeToolDefinition.ModelPath);
        Assert.Same(heldInstance, heldSlot.Bind(heldItem));
        Assert.Equal(0.2f, heldSlot.Bind(heldItem).TimeSeconds, 4);
        Assert.Same(handsInstance, handsSlot.Bind(hands));

        // A reload (new skeleton) rebinds to a fresh, stopped instance. ViewModelRenderer.Update runs
        // before Draw starts the clip, so the first frame after a swap or reload draws the rest pose
        // (Update returns false on a stopped instance). Cosmetic: one frame.
        AnimatedModelInstance rebound = heldSlot.Bind(heldItemReloaded);
        Assert.NotSame(heldInstance, rebound);
        Assert.Same(heldItemReloaded.Skeleton, rebound.Skeleton);
        Assert.Null(rebound.CurrentClipName);
        Assert.False(rebound.Update(FrameSeconds));
    }

    // A real reload: a second decode, so the skeleton, its ModelRoot and its clips are all new.
    private static AnimatedModelSkeleton Reload(AnimatedModelData data, string path)
    {
        AnimatedModelSkeleton reloaded = AnimatedModelData.Load(path).Skeleton;
        Assert.NotSame(data.Skeleton, reloaded);
        return reloaded;
    }

    private static AnimatedModel ModelWithoutGl(AnimatedModelSkeleton skeleton)
    {
        AnimatedModel model = (AnimatedModel)RuntimeHelpers.GetUninitializedObject(typeof(AnimatedModel));
        typeof(AnimatedModel)
            .GetField("_skeleton", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(model, skeleton);
        return model;
    }
}
