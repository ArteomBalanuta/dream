using Dream.Client.Replication;
using Dream.Rendering.Ui;
using OpenTK.Mathematics;

namespace Dream.Client.Hover.Sources;

// Ground items as hover targets. Id is the item instance id.
public sealed class GroundItemHoverSource : IHoverSource
{
    // A small thing gets a generous target.
    public const float Radius = 0.45f;

    private readonly ClientGroundItemCache _ground;

    public GroundItemHoverSource(ClientGroundItemCache ground)
    {
        _ground = ground ?? throw new ArgumentNullException(nameof(ground));
    }

    public HoverKind Kind => HoverKind.GroundItem;

    public bool TryHit(in HoverRay ray, HoverTargetId sticky, float inflate, out HoverHit hit)
    {
        hit = default;
        bool found = false;
        float bestDepth = 0f;
        long bestId = 0;
        for (int i = 0; i < _ground.Count; i++)
        {
            ClientGroundItem item = _ground[i];
            float radius = sticky.Kind == HoverKind.GroundItem && sticky.Id == item.ItemInstanceId ? Radius * inflate : Radius;
            Vector3 centre = item.Position;
            if (!HoverGeometry.RayVerticalSegment(ray.Origin, ray.Direction, centre, 0f, radius, out float depth)
                || depth > ray.WallDepth)
            {
                continue;
            }

            if (!found || depth < bestDepth || (depth == bestDepth && item.ItemInstanceId < bestId))
            {
                found = true;
                bestDepth = depth;
                bestId = item.ItemInstanceId;
                hit = new HoverHit(new HoverTargetId(HoverKind.GroundItem, item.ItemInstanceId), depth, centre);
            }
        }

        return found;
    }

    public bool TryGetLive(HoverTargetId id, out Vector3 anchor)
    {
        anchor = default;
        if (id.Kind != HoverKind.GroundItem || !_ground.TryGet(id.Id, out ClientGroundItem item))
        {
            return false;
        }

        anchor = item.Position;
        return true;
    }
}
