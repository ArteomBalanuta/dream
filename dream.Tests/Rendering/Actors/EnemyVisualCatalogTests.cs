using OpenTK.Mathematics;
using Xunit;
using Dream.Rendering.Actors;
using Dream.Rendering.Models;
using Dream.Simulation.Content;
using Dream.Simulation.Enemies;

namespace Dream.Tests.Rendering.Actors;

public sealed class EnemyVisualCatalogTests
{
    [Fact]
    public void TheWolf_IsUnscaled_AndUntinted()
    {
        EnemyVisual wolf = EnemyVisualCatalog.Resolve("enemy.wolf");

        Assert.Equal(WolfAssetDefinition.ModelPath, wolf.ModelPath);
        Assert.Equal(1.0f, wolf.ScaleMultiplier);
        Assert.Equal(Vector3.Zero, wolf.TintOffset);
    }

    [Fact]
    public void TheDireWolf_IsLarger_AndDarker()
    {
        EnemyVisual dire = EnemyVisualCatalog.Resolve("enemy.dire-wolf");

        Assert.Equal(WolfAssetDefinition.ModelPath, dire.ModelPath);
        Assert.Equal(1.35f, dire.ScaleMultiplier);
        Assert.Equal(new Vector3(-0.45f, -0.45f, -0.35f), dire.TintOffset);
    }

    [Fact]
    public void TheHound_IsItsOwnModel_WithItsOwnClips()
    {
        EnemyVisual hound = EnemyVisualCatalog.Resolve(EnemyVisualCatalog.HoundKey);

        Assert.Equal(BeastAssetDefinition.HoundModelPath, hound.ModelPath);
        Assert.NotEqual(WolfAssetDefinition.ModelPath, hound.ModelPath);
        Assert.Equal(BeastAssetDefinition.IdleClip, hound.Clips.Idle);
        Assert.Equal(BeastAssetDefinition.MoveClip, hound.Clips.Move);
        Assert.Equal(BeastAssetDefinition.AttackClip, hound.Clips.Attack);
        // The wolf pack has no death clip; the forged hound does.
        Assert.Equal(BeastAssetDefinition.DieClip, hound.Clips.Die);
        Assert.NotEqual(hound.Clips.Idle, hound.Clips.Die);
    }

    // The hound took GraveHowler's key off enemy.dire-wolf, so it must keep the dire wolf's size and
    // tint: the model changed, the enemy did not. Both models normalise to the same world height, so
    // a 1.0 multiplier here would quietly draw the level-8 top-tier enemy at the plain wolf's size.
    [Fact]
    public void TheHound_KeepsTheDireWolfsSizeAndTint_BecauseOnlyTheModelChanged()
    {
        EnemyVisual hound = EnemyVisualCatalog.Resolve(EnemyVisualCatalog.HoundKey);
        EnemyVisual dire = EnemyVisualCatalog.Resolve(EnemyVisualCatalog.DireWolfKey);

        Assert.Equal(dire.ScaleMultiplier, hound.ScaleMultiplier);
        Assert.Equal(dire.TintOffset, hound.TintOffset);
        Assert.Equal(1.35f, hound.ScaleMultiplier);
        Assert.Equal(new Vector3(-0.45f, -0.45f, -0.35f), hound.TintOffset);
        // And it is not the plain wolf's look, which is what the regression looked like.
        Assert.NotEqual(EnemyVisualCatalog.Default.ScaleMultiplier, hound.ScaleMultiplier);
        Assert.NotEqual(EnemyVisualCatalog.Default.TintOffset, hound.TintOffset);
    }

    [Fact]
    public void TheWolfsClips_KeepTheirPackPrefix_AndItsDeathIsItsOwnClip()
    {
        EnemyVisual wolf = EnemyVisualCatalog.Resolve(null);

        Assert.Equal("AnimalArmature|Idle", wolf.Clips.Idle);
        Assert.Equal("AnimalArmature|Walk", wolf.Clips.Move);
        Assert.Equal("AnimalArmature|Gallop", wolf.Clips.Sprint);
        // INVERTED on purpose. This line used to be Assert.Equal(Idle, Die), pinning the alias that
        // stood in for a death clip nobody had checked for; the pack has carried
        // `AnimalArmature|Death` all along and ForgedActorAssetTests asserts it against the GLB.
        Assert.Equal("AnimalArmature|Death", wolf.Clips.Die);
        Assert.NotEqual(wolf.Clips.Idle, wolf.Clips.Die);
        Assert.Equal(wolf.Clips.Move, wolf.Clips.Resolve(Dream.Simulation.Enemies.EnemyBehaviorState.Chasing));
        Assert.Equal(wolf.Clips.Attack, wolf.Clips.Resolve(Dream.Simulation.Enemies.EnemyBehaviorState.Attacking));
        Assert.Equal(wolf.Clips.Die, wolf.Clips.Resolve(Dream.Simulation.Enemies.EnemyBehaviorState.Dead));
        Assert.Equal(wolf.Clips.Idle, wolf.Clips.Resolve(Dream.Simulation.Enemies.EnemyBehaviorState.Idle));
    }

    // ---- gaits (contract C-F) ----

    [Fact]
    public void AMovingEnemy_WalksBelowTheGaitThreshold_AndGallopsAboveIt()
    {
        EnemyClipSet clips = EnemyVisualCatalog.WolfClips;

        Assert.Equal(clips.Move, clips.Resolve(EnemyBehaviorState.Chasing, 1.5f));
        Assert.Equal(clips.Sprint, clips.Resolve(EnemyBehaviorState.Chasing, 5.0f));
        Assert.Equal(EnemyVisualCatalog.GaitThresholdSpeed, clips.SprintSpeedThreshold);
        Assert.Equal(0.4f, clips.GaitHysteresisSpeed);
    }

    // The gait threshold sits in the empty middle between every shipped patrol walk and every shipped chase,
    // clear of both: a patrol at or below the walk-keeping edge and a chase at or above the gallop-starting threshold.
    [Fact]
    public void TheGaitThreshold_SeparatesEveryShippedPatrolFromEveryShippedChase_ByTheHysteresis()
    {
        Assert.NotEmpty(ContentCatalog.Shipped.Enemies);
        foreach (EnemyTemplate template in ContentCatalog.Shipped.Enemies)
        {
            Assert.True(
                ClearsTheGaitBand(template.PatrolSpeed, template.MoveSpeed),
                $"{template.Key} patrols at {template.PatrolSpeed} and chases at {template.MoveSpeed}");
        }

        // Synthetic negatives: a chase inside the band, and a patrol inside it, both fail the check.
        Assert.False(ClearsTheGaitBand(1.0f, EnemyVisualCatalog.GaitThresholdSpeed - 0.2f));
        Assert.False(ClearsTheGaitBand(EnemyVisualCatalog.GaitThresholdSpeed - 0.2f, 3.0f));
    }

    private static bool ClearsTheGaitBand(float patrolSpeed, float chaseSpeed) =>
        patrolSpeed <= EnemyVisualCatalog.GaitThresholdSpeed - EnemyVisualCatalog.GaitHysteresisSpeed
        && chaseSpeed >= EnemyVisualCatalog.GaitThresholdSpeed;

    // The behaviour-only overload is the one WolfAnimationResolver and every pre-gait caller use; it
    // must stay exactly the zero-speed case, or an enemy with no measured pose would gallop on the spot.
    [Theory]
    [InlineData(EnemyBehaviorState.Idle)]
    [InlineData(EnemyBehaviorState.Chasing)]
    [InlineData(EnemyBehaviorState.Attacking)]
    [InlineData(EnemyBehaviorState.Dead)]
    public void TheBehaviourOnlyResolve_IsTheZeroSpeedCase(EnemyBehaviorState state)
    {
        foreach (EnemyClipSet clips in new[] { EnemyVisualCatalog.WolfClips, EnemyVisualCatalog.HoundClips })
        {
            Assert.Equal(clips.Resolve(state, 0f), clips.Resolve(state));
        }

        Assert.Equal(EnemyVisualCatalog.WolfClips.Move, EnemyVisualCatalog.WolfClips.Resolve(EnemyBehaviorState.Chasing));
    }

    [Fact]
    public void ThePlaybackRate_IsOneAtEachGaitsReferenceSpeed_AndClamped()
    {
        EnemyClipSet clips = EnemyVisualCatalog.WolfClips;

        Assert.Equal(1.0f, clips.ResolvePlaybackSpeed(EnemyBehaviorState.Chasing, 1.4f), 4);
        Assert.Equal(1.0f, clips.ResolvePlaybackSpeed(EnemyBehaviorState.Chasing, 5.0f), 4);
        // Dragged along at a crawl or snapped across the floor, the stride stays inside 0.6..1.6.
        Assert.Equal(EnemyClipSet.MinPlaybackSpeed, clips.ResolvePlaybackSpeed(EnemyBehaviorState.Chasing, 0.1f), 4);
        // But NO measurement at all is not a crawl: it plays as authored, so a caller with no pose
        // to measure (and the frame an enemy is first drawn) does not animate in slow motion.
        Assert.Equal(1.0f, clips.ResolvePlaybackSpeed(EnemyBehaviorState.Chasing, 0f), 4);
        Assert.Equal(1.0f, clips.ResolvePlaybackSpeed(EnemyBehaviorState.Chasing, -3f), 4);
        // A chase just above the gait threshold is far below the gallop's authored speed: the floor, not a skate.
        Assert.Equal(EnemyClipSet.MinPlaybackSpeed, clips.ResolvePlaybackSpeed(EnemyBehaviorState.Chasing, 2.9f), 4);
        Assert.Equal(EnemyClipSet.MaxPlaybackSpeed, clips.ResolvePlaybackSpeed(EnemyBehaviorState.Chasing, 40f), 4);
        Assert.Equal(1.0f, clips.ResolvePlaybackSpeed(EnemyBehaviorState.Chasing, float.NaN), 4);
        // Only a moving enemy scales its stride.
        Assert.Equal(1.0f, clips.ResolvePlaybackSpeed(EnemyBehaviorState.Idle, 5.0f), 4);
        Assert.Equal(1.0f, clips.ResolvePlaybackSpeed(EnemyBehaviorState.Attacking, 5.0f), 4);
        Assert.Equal(1.0f, clips.ResolvePlaybackSpeed(EnemyBehaviorState.Dead, 5.0f), 4);
    }

    // The hysteresis band, swept the way PlayerAnimationStateMachineTests sweeps cursor jitter: a speed
    // that dithers by the whole band around the threshold must not keep renaming the clip, because a
    // rename resets clip time and the stride visibly stutters.
    [Fact]
    public void AGaitDoesNotFlip_WhileTheSpeedDithersInsideTheHysteresisBand()
    {
        EnemyClipSet clips = EnemyVisualCatalog.WolfClips;
        float band = clips.GaitHysteresisSpeed;

        // Below the threshold by less than the band, from a walk: never starts the gallop.
        bool sprinting = false;
        for (int i = 0; i < 400; i++)
        {
            float speed = clips.SprintSpeedThreshold - 0.001f - band * 0.5f * (1f + MathF.Sin(i));
            sprinting = clips.ResolveSprinting(speed, sprinting);
            Assert.False(sprinting);
        }

        // Rising past the threshold starts it exactly once, and the same dither then never ends it.
        int flips = 0;
        bool previous = false;
        for (int i = 0; i < 400; i++)
        {
            float speed = clips.SprintSpeedThreshold + band * MathF.Sin(i);
            sprinting = clips.ResolveSprinting(speed, sprinting);
            if (sprinting != previous)
            {
                flips++;
                previous = sprinting;
            }
        }

        Assert.Equal(1, flips);
        Assert.True(sprinting);
        // And it does come back once the speed really has dropped out of the band.
        Assert.False(clips.ResolveSprinting(clips.SprintSpeedThreshold - band - 0.01f, sprinting: true));
    }

    // Only GraveHowler moves to the hound this wave; the other three templates keep the wolf.
    [Fact]
    public void OnlyTheGraveHowler_PointsAtTheHound()
    {
        string root = AppContext.BaseDirectory;
        while (root != null && !File.Exists(Path.Combine(root, "dream", "config", "content", "enemies.json")))
        {
            root = Path.GetDirectoryName(root)!;
        }

        string json = File.ReadAllText(Path.Combine(root!, "dream", "config", "content", "enemies.json"));

        Assert.Equal(1, json.Split("enemy.hound", StringSplitOptions.None).Length - 1);
        int howler = json.IndexOf("GraveHowler", StringComparison.Ordinal);
        int hound = json.IndexOf("enemy.hound", StringComparison.Ordinal);
        Assert.True(howler > 0 && hound > howler);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("enemy.unknown")]
    [InlineData("ENEMY.DIRE-WOLF")]
    [InlineData("enemy.dire-wolf ")]
    public void UnknownOrNullKeys_DrawTheDefaultWolf(string? key)
    {
        Assert.Equal(EnemyVisualCatalog.Resolve("enemy.wolf"), EnemyVisualCatalog.Resolve(key));
    }

    [Fact]
    public void AnOversizedKey_DrawsTheDefaultWolf_AndDoesNotThrow()
    {
        string huge = new('d', 10_000);

        EnemyVisual visual = default;
        Exception? thrown = Record.Exception(() => visual = EnemyVisualCatalog.Resolve(huge));

        Assert.Null(thrown);
        Assert.Equal(EnemyVisualCatalog.Resolve(null), visual);
    }

    [Fact]
    public void Resolving_AllocatesNothing()
    {
        Dream.Tests.Utilities.AllocationAssert.AtMost(0, () =>
        {
            _ = EnemyVisualCatalog.Resolve("enemy.dire-wolf");
            _ = EnemyVisualCatalog.Resolve(null);
            _ = EnemyVisualCatalog.Resolve("nope");
        });
    }

    [Fact]
    public void TheShader_AddsAnOffsetThatIsHarmlessWhenUnset_InBothVariants()
    {
        string root = AppContext.BaseDirectory;
        while (root != null && !File.Exists(Path.Combine(root, "dream", "shaders", "enemyFragment.glsl")))
        {
            root = Path.GetDirectoryName(root)!;
        }

        string source = File.ReadAllText(Path.Combine(root!, "dream", "shaders", "enemyFragment.glsl"));

        Assert.Contains("uniform vec3 uActorTintOffset;", source, StringComparison.Ordinal);
        // base * (1 + offset): an unset uniform is zero, so the multiplier is exactly one.
        Assert.Contains("vec3(1.0) + uActorTintOffset", source, StringComparison.Ordinal);
        // The tint sits before the MASK discard path, so the one code path serves both variants.
        Assert.True(
            source.IndexOf("vec3(1.0) + uActorTintOffset", StringComparison.Ordinal) <
            source.IndexOf("vec3 n = normalize", StringComparison.Ordinal));
    }

    // GL cannot open in a test: the reset itself is checked by a person (players must not darken
    // after a Dire Wolf is drawn). This pins that the call is still in the renderer.
    [Fact]
    public void TheEnemyRenderer_ResetsTheTintAfterTheEnemies()
    {
        string root = AppContext.BaseDirectory;
        while (root != null && !File.Exists(Path.Combine(root, "dream", "Rendering", "Actors", "EnemyRenderer.cs")))
        {
            root = Path.GetDirectoryName(root)!;
        }

        string source = File.ReadAllText(Path.Combine(root!, "dream", "Rendering", "Actors", "EnemyRenderer.cs"));

        int reset = source.IndexOf("ResetTint();", StringComparison.Ordinal);
        Assert.True(reset > 0);
        Assert.True(reset < source.IndexOf("_visualStates.PruneExcept", StringComparison.Ordinal));
        // The reset covers the base program and its MASK variant.
        Assert.Contains("maskProgram.SetVector3(TintUniformName, Vector3.Zero)", source, StringComparison.Ordinal);
    }
}
