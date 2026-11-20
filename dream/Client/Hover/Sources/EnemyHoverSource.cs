using Dream.Client.Presentation;
using Dream.Rendering.Ui;
using Dream.Simulation.Enemies;
using OpenTK.Mathematics;

namespace Dream.Client.Hover.Sources;

// Live enemies as hover targets: a vertical capsule at the render pose (what the player sees), the combat
// anchor as the anchor. Id is the enemy's combat actor id.
public sealed class EnemyHoverSource : IHoverSource
{
    public EnemyHoverSource(EnemyRegistry enemies, IEnemyRenderPoseSource? poseSource)
    {
        throw new NotImplementedException("world-hover stub");
    }

    // The tick the enemies are drawn at this frame; the scene sets it before WorldHover.Update.
    public double RenderTick { get; set; }

    public HoverKind Kind => HoverKind.Enemy;

    public bool TryHit(in HoverRay ray, HoverTargetId sticky, float inflate, out HoverHit hit) =>
        throw new NotImplementedException("world-hover stub");

    public bool TryGetLive(HoverTargetId id, out Vector3 anchor) => throw new NotImplementedException("world-hover stub");
}
