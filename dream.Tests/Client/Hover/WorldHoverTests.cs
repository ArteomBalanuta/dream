using Dream.Client.Hover;
using Dream.Client.Hover.Sources;
using Dream.Client.Replication;
using Dream.Multiplayer.Protocol;
using Dream.Rendering.Ui;
using Dream.Simulation.Combat;
using Dream.Simulation.Enemies;
using Dream.Simulation.Inventory;
using Dream.Simulation.World;
using Dream.Tests.Utilities;
using OpenTK.Mathematics;
using Xunit;
using static Dream.Tests.Client.Hover.HoverTestSupport;

namespace Dream.Tests.Client.Hover;

// The pipeline around the sources: throttle, stickiness, gates and precedence, lifecycle, allocation.
public sealed class WorldHoverTests
{
    private static readonly HoverTargetId EnemyId = new(HoverKind.Enemy, 1);

    [Theory]
    [InlineData(0.025f, 40, 10)]
    [InlineData(1f / 60f, 60, 10)]
    public void AStillPointerOverAStillCamera_PicksAboutTenTimesASecond(float dt, int frames, int expectedPicks)
    {
        FloorWorld world = new();
        CountingSource source = new(HoverKind.Enemy, 1, 5f);
        WorldHover hover = new([source]);

        for (int i = 0; i < frames; i++)
        {
            hover.Update(Frame(world, dt));
        }

        Assert.Equal(expectedPicks, source.TryHitCalls);
        Assert.Equal(EnemyId, hover.Frame.Current);
    }

    [Fact]
    public void ThePointerMovingMoreThanTwoPixels_ForcesAPick_LessDoesNot()
    {
        FloorWorld world = new();
        CountingSource source = new(HoverKind.Enemy, 1, 5f);
        WorldHover hover = new([source]);

        hover.Update(Frame(world));
        Assert.Equal(1, source.TryHitCalls);

        hover.Update(Frame(world, pixel: Centre + new Vector2(1.5f, 0f)));
        Assert.Equal(1, source.TryHitCalls);

        // Drifting 1.5 px a frame still re-picks once it is over 2 px from where the last pick was made.
        hover.Update(Frame(world, pixel: Centre + new Vector2(3f, 0f)));
        Assert.Equal(2, source.TryHitCalls);
    }

    [Fact]
    public void AChangedView_AndAForcePick_EachForceAPick()
    {
        FloorWorld world = new();
        CountingSource source = new(HoverKind.Enemy, 1, 5f);
        WorldHover hover = new([source]);

        hover.Update(Frame(world));
        hover.Update(Frame(world));
        Assert.Equal(1, source.TryHitCalls);

        hover.Update(Frame(world, view: Matrix4.LookAt(new Vector3(10.6f, 2f, 20.5f), new Vector3(10.5f, 2f, 10.5f), Vector3.UnitY)));
        Assert.Equal(2, source.TryHitCalls);

        hover.Update(Frame(world, force: true));
        Assert.Equal(3, source.TryHitCalls);
    }

    [Fact]
    public void AReusedAnswerIsRevalidated_TheAnchorFollows_AndAGoneThingIsGone()
    {
        FloorWorld world = new();
        CountingSource source = new(HoverKind.Enemy, 1, 5f);
        WorldHover hover = new([source]);

        hover.Update(Frame(world));
        Assert.Equal(new Vector3(1f, 2f, 3f), hover.Frame.Anchor);

        source.Anchor = new Vector3(4f, 5f, 6f);
        HoverFrame f = hover.Update(Frame(world));
        Assert.Equal(new Vector3(4f, 5f, 6f), f.Anchor);
        Assert.Equal(1, source.TryHitCalls);

        source.Live = false;
        f = hover.Update(Frame(world));
        Assert.True(f.Current.IsNone);
        Assert.Equal(HoverPhase.Exited, f.Phase);
        Assert.Equal(1, source.TryHitCalls);
    }

    [Fact]
    public void AnEnemyAtTheEdgeOfItsVolume_DoesNotFlicker_WhileItJittersFiveCentimetres()
    {
        FloorWorld world = new();
        EnemyRegistry registry = new();
        registry.Add(Wolf(1, new Vector3(10.5f, 1f, 14.5f), height: 2.2f));
        PoseSource poses = new();
        WorldHover hover = new([new EnemyHoverSource(registry, poses)]);

        // 0.5 m off the ray: outside the 0.45 m body, inside the 1.25x sticky body.
        poses.Feet[1] = new Vector3(11.0f, 1f, 14.5f);
        Assert.True(hover.Update(Frame(world, force: true)).Current.IsNone, "a fresh hover needs the normal volume");

        poses.Feet[1] = new Vector3(10.8f, 1f, 14.5f);
        Assert.Equal(EnemyId, hover.Update(Frame(world, force: true)).Current);

        int transitions = 0;
        for (int i = 0; i < 60; i++)
        {
            float edge = 10.5f + 0.5f + (i % 2 == 0 ? 0.05f : -0.05f);
            poses.Feet[1] = new Vector3(edge, 1f, 14.5f);
            HoverFrame f = hover.Update(Frame(world, force: true));
            Assert.Equal(EnemyId, f.Current);
            if (f.Phase is HoverPhase.Entered or HoverPhase.Exited)
            {
                transitions++;
            }
        }

        Assert.Equal(0, transitions);
    }

    [Fact]
    public void ASticky_IsOverruledByAHitInFrontOfItByMoreThanTheTie()
    {
        FloorWorld world = new();
        CountingSource far = new(HoverKind.Enemy, 1, 8f);
        CountingSource near = new(HoverKind.Npc, 2, 4f) { Hits = false };
        WorldHover hover = new([far, near]);

        Assert.Equal(EnemyId, hover.Update(Frame(world, force: true)).Current);

        near.Hits = true;
        HoverFrame f = hover.Update(Frame(world, force: true));
        Assert.Equal(new HoverTargetId(HoverKind.Npc, 2), f.Current);
        Assert.Equal(EnemyId, f.Previous);
    }

    [Fact]
    public void AGate_ClearsTheHoverAndThePanelAtOnce()
    {
        FloorWorld world = new();
        WorldHover hover = new([new CountingSource(HoverKind.Enemy, 1, 5f)]);
        for (int i = 0; i < 10; i++)
        {
            hover.Update(Frame(world, 0.05f));
        }

        Assert.True(hover.Frame.PanelVisible);

        HoverFrame f = hover.Update(Frame(world, gated: true));
        Assert.True(f.Current.IsNone);
        Assert.False(f.PanelVisible);

        // The pick is fresh when the gate opens again.
        f = hover.Update(Frame(world));
        Assert.Equal(EnemyId, f.Current);
        Assert.Equal(HoverPhase.Entered, f.Phase);
    }

    [Fact]
    public void OverUiWithNoLabel_ClearsTheHover_ButThePanelLingers()
    {
        FloorWorld world = new();
        WorldHover hover = new([new CountingSource(HoverKind.Enemy, 1, 5f)]);
        for (int i = 0; i < 10; i++)
        {
            hover.Update(Frame(world, 0.05f));
        }

        HoverFrame f = hover.Update(Frame(world, 0.05f, overUi: true));
        Assert.True(f.Current.IsNone);
        Assert.Equal(HoverPhase.Exited, f.Phase);
        Assert.True(f.PanelVisible);
        Assert.Equal(EnemyId, f.PanelTarget);
    }

    [Fact]
    public void ALabelTarget_BeatsTheRay_AndIsTheLabelOrigin_EvenOverTheUi()
    {
        FloorWorld world = new();
        CountingSource enemy = new(HoverKind.Enemy, 1, 5f);
        CountingSource npc = new(HoverKind.Npc, 2, 9f) { Hits = false, Anchor = new Vector3(7f, 8f, 9f) };
        WorldHover hover = new([enemy, npc]);
        HoverTargetId label = new(HoverKind.Npc, 2);

        HoverFrame f = hover.Update(Frame(world, label: label));
        Assert.Equal(label, f.Current);
        Assert.Equal(HoverOrigin.Label, f.Origin);
        Assert.Equal(new Vector3(7f, 8f, 9f), f.Anchor);

        f = hover.Update(Frame(world, label: label, overUi: true));
        Assert.Equal(label, f.Current);
        Assert.Equal(HoverOrigin.Label, f.Origin);
        Assert.Equal(0, enemy.TryHitCalls);
    }

    [Fact]
    public void ALabelForAThingThatIsGone_FallsBackToTheRay_OrNothingOverTheUi()
    {
        FloorWorld world = new();
        CountingSource enemy = new(HoverKind.Enemy, 1, 5f);
        CountingSource npc = new(HoverKind.Npc, 2, 9f) { Live = false };
        WorldHover hover = new([enemy, npc]);
        HoverTargetId label = new(HoverKind.Npc, 2);

        HoverFrame f = hover.Update(Frame(world, label: label));
        Assert.Equal(EnemyId, f.Current);
        Assert.Equal(HoverOrigin.Ray, f.Origin);

        f = hover.Update(Frame(world, label: label, overUi: true));
        Assert.True(f.Current.IsNone);
    }

    [Fact]
    public void NoCameraOrNoWorld_IsNothingHit()
    {
        CountingSource source = new(HoverKind.Enemy, 1, 5f);
        WorldHover hover = new([source]);

        Assert.True(hover.Update(Frame(new FloorWorld(), camera: false)).Current.IsNone);
        Assert.True(hover.Update(Frame(null)).Current.IsNone);
        Assert.Equal(0, source.TryHitCalls);
    }

    [Fact]
    public void ADyingEnemyUnderTheCursor_ExitsAtOnce_AndTheInfoPanelHoldsItAsDefeated()
    {
        FloorWorld world = new();
        EnemyRegistry registry = new();
        registry.Add(Wolf(1, new Vector3(10.5f, 1f, 14.5f)));
        WorldHover hover = new([new EnemyHoverSource(registry, null)]);
        for (int i = 0; i < 6; i++)
        {
            hover.Update(Frame(world, 0.05f));
        }

        Assert.Equal(EnemyId, hover.Frame.PanelTarget);

        registry.Remove(1);
        HoverFrame f = hover.Update(Frame(world, 0.05f));
        Assert.Equal(HoverPhase.Exited, f.Phase);
        Assert.Equal(EnemyId, f.PanelTarget);

        // Past the 0.15 s linger, still held (the defeated hold is 0.25 s).
        for (int i = 0; i < 4; i++)
        {
            f = hover.Update(Frame(world, 0.05f));
        }

        Assert.Equal(EnemyId, f.PanelTarget);
        Assert.Equal(1f, f.PanelAlpha);
    }

    [Fact]
    public void Clear_DropsEverythingWithNoLinger()
    {
        FloorWorld world = new();
        WorldHover hover = new([new CountingSource(HoverKind.Enemy, 1, 5f)]);
        for (int i = 0; i < 10; i++)
        {
            hover.Update(Frame(world, 0.05f));
        }

        hover.Clear();
        Assert.Equal(default, hover.Frame);
    }

    [Fact]
    public void UpdateAllocatesNothing_WithTargetsEnteringAndLeaving()
    {
        FloorWorld world = new();
        EnemyRegistry registry = new();
        registry.Add(Wolf(1, new Vector3(10.5f, 1f, 14.5f)));
        registry.Add(Wolf(2, new Vector3(14.5f, 1f, 14.5f)));
        PoseSource poses = new();
        ClientGroundItemCache ground = new();
        ground.Apply(new ServerGroundItems(true, [new GroundItemSnapshot(5, "tool.torch", ItemRarity.Common, new Vector3(10.5f, 1.5f, 12.5f), 0, 0)], []));
        ClientNpcCache npcs = new();
        npcs.ApplyActors([new ActorSnapshot(300, CreatureKind.Npc, null, new Vector3(10.5f, 1f, 11f), -Vector3.UnitZ, 1, 1, true, DisplayName: "Mara")]);
        RemotePlayerHoverSource remotes = new();
        WorldHover hover = new([
            new EnemyHoverSource(registry, poses),
            new NpcHoverSource(npcs),
            new GroundItemHoverSource(ground),
            remotes]);
        HoverTargetId label = new(HoverKind.Npc, 300);

        long bytes = AllocationAssert.AtMost(0, () =>
        {
            for (int i = 0; i < 120; i++)
            {
                // The wolf steps on and off the ray; now and then the pointer moves, a label is hot, or a gate shuts.
                poses.Feet[1] = new Vector3(i % 40 < 20 ? 10.5f : 13f, 1f, 14.5f);
                hover.Update(Frame(
                    world,
                    1f / 60f,
                    pixel: Centre + new Vector2(i % 30 == 0 ? 6f : 0f, 0f),
                    gated: i % 50 == 49,
                    overUi: i % 37 == 36,
                    label: i % 37 == 36 ? label : default));
            }
        });

        Assert.Equal(0, bytes);
    }
}
