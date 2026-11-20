using Dream.Client.Presentation;
using Dream.Rendering.Actors;
using Dream.Rendering.Ui;
using Dream.Simulation.Enemies;
using OpenTK.Mathematics;

namespace Dream.Client.Hover.Sources;

// Live enemies as hover targets: a vertical capsule at the render pose (what the player sees), the combat
// anchor as the anchor. Id is the enemy's combat actor id.
public sealed class EnemyHoverSource : IHoverSource
{
    // Even a small enemy is given at least this much radius, so it is easy to point at.
    public const float MinRadius = 0.45f;

    private readonly EnemyRegistry _enemies;
    private readonly IEnemyRenderPoseSource? _poseSource;

    public EnemyHoverSource(EnemyRegistry enemies, IEnemyRenderPoseSource? poseSource)
    {
        _enemies = enemies ?? throw new ArgumentNullException(nameof(enemies));
        _poseSource = poseSource;
    }

    // The tick the enemies are drawn at this frame; the scene sets it before WorldHover.Update.
    public double RenderTick { get; set; }

    public HoverKind Kind => HoverKind.Enemy;

    public bool TryHit(in HoverRay ray, HoverTargetId sticky, float inflate, out HoverHit hit)
    {
        hit = default;
        bool found = false;
        float bestDepth = 0f;
        long bestId = 0;
        IReadOnlyList<EnemyActor> actors = _enemies.ActiveActors;
        for (int i = 0; i < actors.Count; i++)
        {
            EnemyActor actor = actors[i];
            if (!actor.IsActive)
            {
                continue;
            }

            int id = actor.Combatant.Actor.Id;
            float radius = MathF.Max(actor.CollisionRadius, MinRadius);
            if (sticky.Kind == HoverKind.Enemy && sticky.Id == id)
            {
                radius *= inflate;
            }

            Vector3 feet = EnemyRenderer.ResolveRenderPose(actor, _poseSource, RenderTick).Position;
            if (!HoverGeometry.RayCapsule(ray.Origin, ray.Direction, feet, actor.CollisionHeight, radius, out float depth)
                || depth > ray.WallDepth)
            {
                continue;
            }

            if (!found || depth < bestDepth || (depth == bestDepth && id < bestId))
            {
                found = true;
                bestDepth = depth;
                bestId = id;
                hit = new HoverHit(new HoverTargetId(HoverKind.Enemy, id), depth, actor.Combatant.GetCombatAnchorWorldPosition());
            }
        }

        return found;
    }

    public bool TryGetLive(HoverTargetId id, out Vector3 anchor)
    {
        anchor = default;
        if (id.Kind != HoverKind.Enemy || id.Id < int.MinValue || id.Id > int.MaxValue
            || !_enemies.TryGetEnemyByActorId((int)id.Id, out EnemyActor? actor) || actor is null || !actor.IsActive)
        {
            return false;
        }

        anchor = actor.Combatant.GetCombatAnchorWorldPosition();
        return true;
    }
}
