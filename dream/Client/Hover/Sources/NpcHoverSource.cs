using Dream.Client.Replication;
using Dream.Rendering.Ui;
using OpenTK.Mathematics;

namespace Dream.Client.Hover.Sources;

// Villagers from the NPC cache as hover targets. Id is the NPC's actor id.
public sealed class NpcHoverSource : IHoverSource
{
    public NpcHoverSource(ClientNpcCache npcs)
    {
        throw new NotImplementedException("world-hover stub");
    }

    public HoverKind Kind => HoverKind.Npc;

    public bool TryHit(in HoverRay ray, HoverTargetId sticky, float inflate, out HoverHit hit) =>
        throw new NotImplementedException("world-hover stub");

    public bool TryGetLive(HoverTargetId id, out Vector3 anchor) => throw new NotImplementedException("world-hover stub");
}
