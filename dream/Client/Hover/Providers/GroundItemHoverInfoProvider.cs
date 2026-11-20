using Dream.Client.Replication;
using Dream.Rendering.Ui;
using OpenTK.Mathematics;

namespace Dream.Client.Hover.Providers;

// Ground items: catalog name in its tier colour, rarity, the pick-up or claimed hint.
public sealed class GroundItemHoverInfoProvider : IHoverInfoProvider
{
    public GroundItemHoverInfoProvider(ClientGroundItemCache ground)
    {
        throw new NotImplementedException("world-hover stub");
    }

    // The local player's position, actor id and the estimated server tick (claims); the scene sets them each frame.
    public Vector3 PlayerPosition { get; set; }

    public int LocalActorId { get; set; }

    public ulong ServerTick { get; set; }

    public HoverKind Kind => HoverKind.GroundItem;

    public bool TryDescribe(HoverTargetId id, HoverInfo info) => throw new NotImplementedException("world-hover stub");
}
