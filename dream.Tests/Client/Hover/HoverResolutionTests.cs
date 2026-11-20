using Dream.Client.Hover;
using Dream.Client.Hover.Sources;
using Dream.Client.Replication;
using Dream.Multiplayer.Protocol;
using Dream.Rendering.Ui;
using Dream.Simulation.Enemies;
using Dream.Simulation.Inventory;
using OpenTK.Mathematics;
using Xunit;
using static Dream.Tests.Client.Hover.HoverTestSupport;

namespace Dream.Tests.Client.Hover;

// Resolution among the per-kind hits: depth, then kind priority inside the tie distance, then id; and the
// wall.
public sealed class HoverResolutionTests
{
    private static HoverRay Ray(float wall = float.PositiveInfinity) => new(new Vector3(10.5f, 2f, 20.4f), -Vector3.UnitZ, wall);

    private static bool Pick(IReadOnlyList<IHoverSource> sources, in HoverRay ray, out HoverHit hit) =>
        new WorldHoverPicker(sources).TryPick(in ray, HoverTargetId.None, out hit);

    [Fact]
    public void TheNearestDepthWins()
    {
        CountingSource far = new(HoverKind.Enemy, 1, 8f);
        CountingSource near = new(HoverKind.Npc, 2, 5f);

        Assert.True(Pick([far, near], Ray(), out HoverHit hit));
        Assert.Equal(new HoverTargetId(HoverKind.Npc, 2), hit.Target);
    }

    [Fact]
    public void InsideTheTieDistance_TheLowerKindPriorityWins()
    {
        CountingSource item = new(HoverKind.GroundItem, 9, 5.0f);
        CountingSource enemy = new(HoverKind.Enemy, 1, 5.0f + (HoverPriority.DepthTieMetres * 0.9f));

        Assert.True(Pick([item, enemy], Ray(), out HoverHit hit));
        Assert.Equal(HoverKind.Enemy, hit.Target.Kind);

        // Source order does not matter.
        Assert.True(Pick([enemy, item], Ray(), out hit));
        Assert.Equal(HoverKind.Enemy, hit.Target.Kind);
    }

    [Fact]
    public void BeyondTheTieDistance_TheNearerWinsWhateverItsKind()
    {
        CountingSource item = new(HoverKind.GroundItem, 9, 5.0f);
        CountingSource enemy = new(HoverKind.Enemy, 1, 5.0f + (HoverPriority.DepthTieMetres * 1.2f));

        Assert.True(Pick([enemy, item], Ray(), out HoverHit hit));
        Assert.Equal(HoverKind.GroundItem, hit.Target.Kind);
    }

    [Fact]
    public void SameKindStillTied_TheLowerIdWins()
    {
        CountingSource a = new(HoverKind.Enemy, 7, 5f);
        CountingSource b = new(HoverKind.Enemy, 3, 5.1f);

        Assert.True(Pick([a, b], Ray(), out HoverHit hit));
        Assert.Equal(3, hit.Target.Id);
        Assert.True(Pick([b, a], Ray(), out hit));
        Assert.Equal(3, hit.Target.Id);
    }

    [Fact]
    public void TheTieWindowIsMeasuredFromTheNearest_NotChained_InEveryOrder()
    {
        // NPC 0.5, remote player 0.8, enemy 1.1 (above a base depth): the enemy is outside the window of the
        // nearest, so the remote player (priority) beats the NPC.
        float t = HoverPriority.DepthTieMetres;
        CountingSource npc = new(HoverKind.Npc, 1, 5f);
        CountingSource player = new(HoverKind.RemotePlayer, 2, 5f + (t * 0.85f));
        CountingSource enemy = new(HoverKind.Enemy, 3, 5f + (t * 1.7f));
        IHoverSource[] all = [npc, player, enemy];

        int[][] perms = [[0, 1, 2], [0, 2, 1], [1, 0, 2], [1, 2, 0], [2, 0, 1], [2, 1, 0]];
        foreach (int[] p in perms)
        {
            Assert.True(Pick([all[p[0]], all[p[1]], all[p[2]]], Ray(), out HoverHit hit));
            Assert.Equal(new HoverTargetId(HoverKind.RemotePlayer, 2), hit.Target);
        }
    }

    [Fact]
    public void NothingBehindTheWallIsPicked()
    {
        CountingSource behind = new(HoverKind.Enemy, 1, 9f);
        CountingSource front = new(HoverKind.Npc, 2, 4f);

        Assert.False(Pick([behind], Ray(wall: 8f), out _));
        Assert.True(Pick([behind, front], Ray(wall: 8f), out HoverHit hit));
        Assert.Equal(HoverKind.Npc, hit.Target.Kind);
    }

    [Fact]
    public void AnEnemyStandingOnAGroundItemBeatsTheItem()
    {
        // The drop lies on the floor 0.1 m in front of the enemy's feet: nearer along the ray, inside the tie.
        EnemyRegistry registry = new();
        registry.Add(Wolf(2000, new Vector3(10.5f, 1f, 14.5f), height: 2.2f));
        ClientGroundItemCache ground = new();
        ground.Apply(new ServerGroundItems(true, [new GroundItemSnapshot(5, "tool.torch", ItemRarity.Common, new Vector3(10.5f, 1.7f, 14.9f), 0, 0)], []));
        IHoverSource[] sources = [new GroundItemHoverSource(ground), new EnemyHoverSource(registry, null)];

        HoverRay ray = Ray();
        Assert.True(new GroundItemHoverSource(ground).TryHit(in ray, HoverTargetId.None, 1f, out HoverHit item));
        Assert.True(new EnemyHoverSource(registry, null).TryHit(in ray, HoverTargetId.None, 1f, out HoverHit enemy));
        Assert.True(item.Depth < enemy.Depth, "the item is nearer along the ray");
        Assert.True(enemy.Depth - item.Depth <= HoverPriority.DepthTieMetres);

        Assert.True(Pick(sources, in ray, out HoverHit hit));
        Assert.Equal(new HoverTargetId(HoverKind.Enemy, 2000), hit.Target);
    }

    [Fact]
    public void AWallInFrontHidesAnEnemy_ThroughTheWholePipeline()
    {
        FloorWorld world = new();
        EnemyRegistry registry = new();
        registry.Add(Wolf(2000, new Vector3(10.5f, 1f, 8.5f)));
        WorldHover hover = new([new EnemyHoverSource(registry, null)]);

        Assert.Equal(HoverKind.Enemy, hover.Update(Frame(world)).Current.Kind);

        world.Blocks.Add((10, 2, 12));
        Assert.True(hover.Update(Frame(world, force: true)).Current.IsNone);
    }
}
