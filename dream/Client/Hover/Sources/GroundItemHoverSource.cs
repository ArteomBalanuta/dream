using Dream.Client.Replication;
using Dream.Rendering.Ui;
using OpenTK.Mathematics;

namespace Dream.Client.Hover.Sources;

// Ground items as hover targets. Id is the item instance id.
public sealed class GroundItemHoverSource : IHoverSource
{
    public GroundItemHoverSource(ClientGroundItemCache ground)
    {
        throw new NotImplementedException("world-hover stub");
    }

    public HoverKind Kind => HoverKind.GroundItem;

    public bool TryHit(in HoverRay ray, HoverTargetId sticky, float inflate, out HoverHit hit) =>
        throw new NotImplementedException("world-hover stub");

    public bool TryGetLive(HoverTargetId id, out Vector3 anchor) => throw new NotImplementedException("world-hover stub");
}
