using Dream.Client.Replication;
using Dream.Rendering.Ui;
using OpenTK.Mathematics;

namespace Dream.Client.Hover.Sources;

// Villagers from the NPC cache as hover targets. Id is the NPC's actor id.
public sealed class NpcHoverSource : IHoverSource
{
    public const float Height = 1.9f;
    public const float Radius = 0.45f;

    private readonly ClientNpcCache _npcs;

    public NpcHoverSource(ClientNpcCache npcs)
    {
        _npcs = npcs ?? throw new ArgumentNullException(nameof(npcs));
    }

    public HoverKind Kind => HoverKind.Npc;

    public bool TryHit(in HoverRay ray, HoverTargetId sticky, float inflate, out HoverHit hit)
    {
        hit = default;
        bool found = false;
        float bestDepth = 0f;
        long bestId = 0;
        for (int i = 0; i < _npcs.Count; i++)
        {
            ClientNpc npc = _npcs[i];
            float radius = sticky.Kind == HoverKind.Npc && sticky.Id == npc.ActorId ? Radius * inflate : Radius;
            Vector3 feet = npc.Position;
            if (!HoverGeometry.RayCapsule(ray.Origin, ray.Direction, feet, Height, radius, out float depth)
                || depth > ray.WallDepth)
            {
                continue;
            }

            if (!found || depth < bestDepth || (depth == bestDepth && npc.ActorId < bestId))
            {
                found = true;
                bestDepth = depth;
                bestId = npc.ActorId;
                hit = new HoverHit(new HoverTargetId(HoverKind.Npc, npc.ActorId), depth, Anchor(in feet));
            }
        }

        return found;
    }

    public bool TryGetLive(HoverTargetId id, out Vector3 anchor)
    {
        anchor = default;
        if (id.Kind != HoverKind.Npc || id.Id < int.MinValue || id.Id > int.MaxValue
            || !_npcs.TryGet((int)id.Id, out ClientNpc npc))
        {
            return false;
        }

        anchor = Anchor(npc.Position);
        return true;
    }

    // Over the head, about where the name label hangs.
    private static Vector3 Anchor(in Vector3 feet) => new(feet.X, feet.Y + Height, feet.Z);
}
