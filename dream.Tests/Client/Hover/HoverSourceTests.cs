using Dream.Client.Hover;
using Dream.Client.Hover.Sources;
using Dream.Client.Presentation;
using Dream.Client.Replication;
using Dream.Multiplayer.Protocol;
using Dream.Rendering.Ui;
using Dream.Simulation.Combat;
using Dream.Simulation.Enemies;
using Dream.Simulation.Inventory;
using Dream.Simulation.World;
using OpenTK.Mathematics;
using Xunit;
using static Dream.Tests.Client.Hover.HoverTestSupport;

namespace Dream.Tests.Client.Hover;

// The hit volumes and liveness of each kind's source.
public sealed class HoverSourceTests
{
    // Along -Z at head height (y = 2) down the line x = 10.5, offset sideways by dx.
    private static HoverRay Ray(float dx = 0f, float y = 2f, float wall = float.PositiveInfinity) =>
        new(new Vector3(10.5f + dx, y, 20.4f), -Vector3.UnitZ, wall);

    [Fact]
    public void TheCapsuleMathHitsTheBodyAndMissesAroundIt()
    {
        Vector3 feet = new(0f, 0f, 0f);
        Vector3 forward = -Vector3.UnitZ;

        // Straight at the middle, 1 m up: front surface at 5 - 0.45.
        Assert.True(HoverGeometry.RayCapsule(new Vector3(0f, 1f, 5f), forward, feet, 1.9f, 0.45f, out float depth));
        Assert.Equal(4.55f, depth, 3);

        // Just wide of the radius, over the head, below the feet.
        Assert.False(HoverGeometry.RayCapsule(new Vector3(0.5f, 1f, 5f), forward, feet, 1.9f, 0.45f, out _));
        Assert.False(HoverGeometry.RayCapsule(new Vector3(0f, 2.5f, 5f), forward, feet, 1.9f, 0.45f, out _));
        Assert.False(HoverGeometry.RayCapsule(new Vector3(0f, -0.5f, 5f), forward, feet, 1.9f, 0.45f, out _));

        // Behind the ray's start is not a hit.
        Assert.False(HoverGeometry.RayCapsule(new Vector3(0f, 1f, -5f), forward, feet, 1.9f, 0.45f, out _));

        // A steep ray from above lands on the head cap.
        Vector3 down = new Vector3(0f, -1f, 0.001f).Normalized();
        Assert.True(HoverGeometry.RayCapsule(new Vector3(0f, 6f, 0f), down, feet, 1.9f, 0.45f, out depth));
        Assert.Equal(6f - 1.9f, depth, 1);
    }

    [Fact]
    public void AnEnemyIsHitAtItsRenderPose_NotItsAuthoritativePosition()
    {
        EnemyRegistry registry = new();
        EnemyActor wolf = Wolf(2000, new Vector3(10.5f, 1f, 14.5f));
        registry.Add(wolf);
        PoseSource poses = new();
        EnemyHoverSource source = new(registry, poses);
        HoverRay ray = Ray();

        // No pose: the authoritative feet, on the ray.
        Assert.True(source.TryHit(in ray, HoverTargetId.None, 1f, out HoverHit hit));
        Assert.Equal(new HoverTargetId(HoverKind.Enemy, 2000), hit.Target);
        Assert.Equal(wolf.Combatant.GetCombatAnchorWorldPosition(), hit.Anchor);

        // Drawn 3 m to the side: the ray no longer meets it.
        poses.Feet[2000] = new Vector3(13.5f, 1f, 14.5f);
        Assert.False(source.TryHit(in ray, HoverTargetId.None, 1f, out _));

        // Drawn 3 m the other side of the ray, the ray through there meets it.
        HoverRay side = Ray(dx: 3f);
        Assert.True(source.TryHit(in side, HoverTargetId.None, 1f, out _));
    }

    [Fact]
    public void ASmallEnemyHasAtLeastTheMinimumRadius_AndTheEnemyIsFeetUp()
    {
        EnemyRegistry registry = new();
        registry.Add(Wolf(2000, new Vector3(10.5f, 1f, 14.5f), radius: 0.2f, height: 1.2f));
        EnemyHoverSource source = new(registry, null);

        HoverRay wide = Ray(dx: 0.40f, y: 1.6f);
        Assert.True(source.TryHit(in wide, HoverTargetId.None, 1f, out _));
        HoverRay tooWide = Ray(dx: 0.60f, y: 1.6f);
        Assert.False(source.TryHit(in tooWide, HoverTargetId.None, 1f, out _));

        // Feet at y = 1, so a ray under the feet or above CollisionHeight misses.
        HoverRay below = Ray(y: 0.7f);
        Assert.False(source.TryHit(in below, HoverTargetId.None, 1f, out _));
        HoverRay above = Ray(y: 2.5f);
        Assert.False(source.TryHit(in above, HoverTargetId.None, 1f, out _));
    }

    [Fact]
    public void ARemovedEnemyIsNeitherHitNorLive()
    {
        EnemyRegistry registry = new();
        registry.Add(Wolf(2000, new Vector3(10.5f, 1f, 14.5f)));
        EnemyHoverSource source = new(registry, null);
        HoverTargetId id = new(HoverKind.Enemy, 2000);

        Assert.True(source.TryGetLive(id, out _));
        registry.Remove(2000);
        HoverRay ray = Ray();
        Assert.False(source.TryGetLive(id, out _));
        Assert.False(source.TryHit(in ray, HoverTargetId.None, 1f, out _));
        Assert.False(source.TryGetLive(new HoverTargetId(HoverKind.Npc, 2000), out _));
        Assert.False(source.TryGetLive(new HoverTargetId(HoverKind.Enemy, long.MaxValue), out _));
    }

    [Fact]
    public void ADeadEnemyIsNeitherHitNorLive()
    {
        EnemyRegistry registry = new();
        EnemyActor wolf = Wolf(2000, new Vector3(10.5f, 1f, 14.5f));
        registry.Add(wolf);
        EnemyHoverSource source = new(registry, null);

        wolf.Combatant.ApplyDamage(10_000);
        Assert.False(wolf.IsActive);

        HoverRay ray = Ray();
        Assert.False(source.TryHit(in ray, HoverTargetId.None, 1f, out _));
        Assert.False(source.TryGetLive(new HoverTargetId(HoverKind.Enemy, 2000), out _));
    }

    [Fact]
    public void TheNpcCapsuleIs1_9MetresTall_AndAnchoredOverTheHead()
    {
        ClientNpcCache npcs = new();
        npcs.ApplyActors([new ActorSnapshot(300, CreatureKind.Npc, null, new Vector3(10.5f, 1f, 14.5f), -Vector3.UnitZ, 1, 1, true, DisplayName: "Mara")]);
        NpcHoverSource source = new(npcs);

        HoverRay body = Ray(y: 2.8f);
        Assert.True(source.TryHit(in body, HoverTargetId.None, 1f, out HoverHit hit));
        Assert.Equal(new HoverTargetId(HoverKind.Npc, 300), hit.Target);
        Assert.True(hit.Anchor.Y > 2.5f);

        HoverRay over = Ray(y: 3.1f);
        Assert.False(source.TryHit(in over, HoverTargetId.None, 1f, out _));
        HoverRay wide = Ray(dx: 0.5f);
        Assert.False(source.TryHit(in wide, HoverTargetId.None, 1f, out _));

        Assert.True(source.TryGetLive(hit.Target, out Vector3 anchor));
        Assert.Equal(hit.Anchor, anchor);
        npcs.Clear();
        Assert.False(source.TryGetLive(hit.Target, out _));
    }

    [Fact]
    public void AGroundItemIsASphereAtItsPosition_AndIsLiveWhileCached()
    {
        ClientGroundItemCache ground = new();
        ground.Apply(new ServerGroundItems(true, [new GroundItemSnapshot(5, "tool.torch", ItemRarity.Common, new Vector3(10.5f, 1.5f, 14.5f), 0, 0)], []));
        GroundItemHoverSource source = new(ground);

        HoverRay near = Ray(dx: 0.4f, y: 1.5f);
        Assert.True(source.TryHit(in near, HoverTargetId.None, 1f, out HoverHit hit));
        Assert.Equal(new HoverTargetId(HoverKind.GroundItem, 5), hit.Target);
        Assert.Equal(new Vector3(10.5f, 1.5f, 14.5f), hit.Anchor);

        HoverRay far = Ray(dx: 0.5f, y: 1.5f);
        Assert.False(source.TryHit(in far, HoverTargetId.None, 1f, out _));
        Assert.True(source.TryHit(in far, hit.Target, HoverTuning.StickyInflate, out _), "the sticky item is inflated");

        Assert.True(source.TryGetLive(hit.Target, out _));
        ground.Clear();
        Assert.False(source.TryGetLive(hit.Target, out _));
    }

    [Fact]
    public void ARemotePlayerIsA1_8MetreCapsuleAtItsPose_AndOnlyWhileAlive()
    {
        RemotePlayerPresentationState Player(bool alive) =>
            new(7, "s", "Ann", new InterpolatedActorPose(new Vector3(10.5f, 1f, 14.5f), -Vector3.UnitZ), 10, 10, alive);

        RemotePlayerHoverSource source = new() { Players = [Player(true)] };
        HoverRay ray = Ray();

        Assert.True(source.TryHit(in ray, HoverTargetId.None, 1f, out HoverHit hit));
        Assert.Equal(new HoverTargetId(HoverKind.RemotePlayer, 7), hit.Target);
        Assert.Equal(new Vector3(10.5f, 2f, 14.5f), hit.Anchor);

        HoverRay wide = Ray(dx: 0.45f);
        Assert.False(source.TryHit(in wide, HoverTargetId.None, 1f, out _));
        HoverRay over = Ray(y: 3f);
        Assert.False(source.TryHit(in over, HoverTargetId.None, 1f, out _));
        Assert.True(source.TryGetLive(hit.Target, out Vector3 anchor));
        Assert.Equal(hit.Anchor, anchor);

        source.Players = [Player(false)];
        Assert.False(source.TryHit(in ray, HoverTargetId.None, 1f, out _));
        Assert.False(source.TryGetLive(hit.Target, out _));
    }
}
