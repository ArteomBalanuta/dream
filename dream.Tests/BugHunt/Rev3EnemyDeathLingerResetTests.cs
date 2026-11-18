using System.Reflection;
using OpenTK.Mathematics;
using Xunit;
using Dream.Rendering.Actors;
using Dream.Rendering.Models;
using Dream.Simulation.Enemies;

namespace Dream.Tests.BugHunt;

// REV3-1: the render-only death linger (BH-ANIM-2) was never reset, so a world swap spawned a corpse
// for every enemy of the world before it.
//
// EnemyDeathLinger.Clear shipped with no production caller. PlayScene applies a JoinAcceptedMessage
// mid-session (a reconnect, a takeover, a world transfer): the map is replaced and new actors are
// bootstrapped without the renderer being told. The next DrawAtRenderTick then found every OLD actor
// id in the linger's last-drawn table and absent from the new world's id set, which the linger reads
// as a death: BeginDeathLinger started a death clip for each one, frozen at its old-world model
// matrix, and DrawDyingModels drew it - and _casters.Add made it cast shadows - for up to
// MaxLingerSeconds in the new world. ObserveDrawn only ever cleared the ids the new world happened
// to reuse.
//
// Headless: the real wolf skeleton over AnimatedModelData.Load, no GL context and no window.
public class Rev3EnemyDeathLingerResetTests
{
    private const float FrameSeconds = 1f / 60f;

    private static readonly Lazy<AnimatedModelData> Wolf = new(() => AnimatedModelData.Load(WolfAssetDefinition.ModelPath));

    private static EnemyDeathLinger.LastDrawnEnemy WolfLastDrawn(Vector3 position) =>
        new(
            WolfAssetDefinition.ModelPath,
            "enemy",
            WolfAssetDefinition.DieClip,
            Matrix4.CreateTranslation(position),
            Vector3.Zero,
            Wolf.Value.Skeleton);

    // One drawn frame for each id, as DrawAtRenderTick does.
    private static void DrawFrame(EnemyDeathLinger linger, EnemyVisualStateCache cache, params int[] actorIds)
    {
        foreach (int actorId in actorIds)
        {
            EnemyVisualState state = cache.GetOrAdd(actorId, Wolf.Value.Skeleton);
            EnemyRenderer.AdvanceInstance(state, WolfAssetDefinition.WalkClip, FrameSeconds);
            linger.ObserveDrawn(actorId, WolfLastDrawn(new Vector3(actorId, 0f, actorId)), cache);
        }
    }

    // The defect, at the linger: an old world drawn, then replaced. Without the reset every old id is
    // still remembered as last-drawn, so the first frame of the NEW world reports all of them as
    // departed and a corpse is begun for each.
    [Fact]
    public void WithoutTheReset_EveryOldWorldEnemy_WouldBeginACorpseInTheNewWorld()
    {
        EnemyDeathLinger linger = new();
        EnemyVisualStateCache cache = new();
        List<int> departed = [];

        DrawFrame(linger, cache, 11, 12, 13);
        Assert.Equal(3, linger.LastDrawnCount);

        // The new world's first frame: different actor ids, so none of the old ones is "current".
        linger.CollectDeparted([21, 22], departed);
        Assert.Equal([11, 12, 13], departed);
    }

    // The fix, at the linger: a world replacement clears it outright, so the new world's first frame
    // finds nothing to bury.
    [Fact]
    public void AfterTheReset_TheNewWorldsFirstFrame_FindsNoDepartedEnemy()
    {
        EnemyDeathLinger linger = new();
        EnemyVisualStateCache cache = new();
        List<int> departed = [];

        DrawFrame(linger, cache, 11, 12, 13);
        // One of them had already died and is mid-clip when the join lands.
        AnimatedModelInstance? dyingInstance = cache.TakeInstance(13);
        Assert.NotNull(dyingInstance);
        linger.Begin(13, WolfLastDrawn(new Vector3(13f, 0f, 13f)), dyingInstance!);
        Assert.Equal(1, linger.Count);

        linger.Clear(cache);

        Assert.Equal(0, linger.Count);
        Assert.Equal(0, linger.LastDrawnCount);
        Assert.False(linger.IsDying(13));
        Assert.False(linger.TryGetLastDrawn(11, out _));
        // The corpse's instance went back to the pool rather than being dropped on the floor.
        Assert.True(cache.PooledInstanceCount > 0);

        linger.CollectDeparted([21, 22], departed);
        Assert.Empty(departed);
    }

    // The reset is render-only: it moves nothing but this renderer's own state. Clear takes the
    // visual-state pool and nothing else, and the renderer's entry point is a no-argument call on the
    // renderer itself - no actor, no pose source, no interpolation buffer is involved.
    [Fact]
    public void TheResetIsRenderOnly_AndTakesNothingButThePool()
    {
        MethodInfo clear = typeof(EnemyRenderer).GetMethod(nameof(EnemyRenderer.ClearDeathLinger))!;

        Assert.Empty(clear.GetParameters());
        Assert.Equal(typeof(void), clear.ReturnType);
    }

    // The wiring the finding is actually about: GL cannot open in a test, so the call is pinned as
    // source. The join path must clear the linger, in the block that handles JoinAcceptedMessage,
    // beside the presenter reset that releases the old world's interpolation buffers.
    [Fact]
    public void TheJoinPath_ClearsTheDeathLinger()
    {
        string source = Read("dream/Client/Scenes/PlayScene.Replication.cs");

        int join = source.IndexOf("ClientJoinAcceptedApplier.ApplyToPresenter(", StringComparison.Ordinal);
        int clear = source.IndexOf("ClearDeathLinger()", StringComparison.Ordinal);
        Assert.True(join > 0, "The join no longer resets the presenter.");
        Assert.True(clear > join, "The join must clear the renderer's death linger when it replaces the world.");
        Assert.Contains("_render?.Enemies.ClearDeathLinger();", source, StringComparison.Ordinal);
    }

    // Control: an ordinary death is untouched. The departed enemy's own instance plays the die clip
    // and the model is released the frame after the clip reports itself finished.
    [Fact]
    public void Control_AnOrdinaryDeath_StillPlaysItsClip_AndIsStillReleased()
    {
        EnemyDeathLinger linger = new();
        EnemyVisualStateCache cache = new();
        List<int> departed = [];

        DrawFrame(linger, cache, 7);
        linger.CollectDeparted([], departed);
        Assert.Equal([7], departed);

        AnimatedModelInstance? instance = cache.TakeInstance(7);
        Assert.NotNull(instance);
        linger.Begin(7, WolfLastDrawn(new Vector3(7f, 0f, 7f)), instance!);

        EnemyDeathLinger.DyingModel dying = Assert.Single(linger.Models);
        Assert.Equal(WolfAssetDefinition.DieClip, dying.Instance.CurrentClipName);
        Assert.True(dying.Instance.Playback.IsPlaying);

        // Stepped frame by frame until it ends, and in any case inside the hard cap.
        int frames = 0;
        while (linger.Count > 0 && frames < (int)((EnemyDeathLinger.MaxLingerSeconds + 1f) / FrameSeconds))
        {
            linger.Advance(FrameSeconds, cache);
            frames++;
        }

        Assert.Equal(0, linger.Count);
        Assert.True(frames > 1, "A death that is released on its first frame plays no clip at all.");
        Assert.Equal(1, cache.PooledInstanceCount);
    }

    // Control: a respawn under a reused actor id still releases the corpse, which is the guarantee the
    // reset must not have replaced.
    [Fact]
    public void Control_ARespawnUnderAReusedId_StillReleasesTheCorpse()
    {
        EnemyDeathLinger linger = new();
        EnemyVisualStateCache cache = new();

        DrawFrame(linger, cache, 4);
        AnimatedModelInstance? instance = cache.TakeInstance(4);
        linger.Begin(4, WolfLastDrawn(Vector3.Zero), instance!);
        Assert.True(linger.IsDying(4));

        // The server reuses actor id 4 for a fresh enemy: the next drawn frame buries the corpse.
        DrawFrame(linger, cache, 4);

        Assert.False(linger.IsDying(4));
        Assert.Equal(0, linger.Count);
    }

    private static string Read(string relative) =>
        File.ReadAllText(Path.Combine(RepoRoot(), relative.Replace('/', Path.DirectorySeparatorChar)));

    private static string RepoRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "dream", "dream.csproj")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
