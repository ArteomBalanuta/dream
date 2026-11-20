using Dream.Client.Replication;
using Dream.Rendering.Actors;
using Dream.Rendering.Ui;
using Dream.Simulation.Inventory;
using OpenTK.Mathematics;

namespace Dream.Client.Hover.Providers;

// Ground items: catalog name in its tier colour, rarity, the pick-up or claimed hint.
public sealed class GroundItemHoverInfoProvider : IHoverInfoProvider
{
    private readonly ClientGroundItemCache _ground;

    public GroundItemHoverInfoProvider(ClientGroundItemCache ground)
    {
        ArgumentNullException.ThrowIfNull(ground);
        _ground = ground;
    }

    // The local player's position, actor id and the estimated server tick (claims); the scene sets them each frame.
    public Vector3 PlayerPosition { get; set; }

    public int LocalActorId { get; set; }

    public ulong ServerTick { get; set; }

    public HoverKind Kind => HoverKind.GroundItem;

    public bool TryDescribe(HoverTargetId id, HoverInfo info)
    {
        if (id.Kind != HoverKind.GroundItem || !_ground.TryGet(id.Id, out ClientGroundItem item))
        {
            return false;
        }

        float range = ClientGroundItemCache.PickupRange;
        info.Clear();
        info.Target = id;
        info.Disposition = HoverDisposition.Item;
        info.Title = item.DisplayName;
        switch (item.Rarity)
        {
            case ItemRarity.Uncommon:
                info.Tier = ItemTier.Uncommon;
                info.Subtitle = "Uncommon";
                break;
            case ItemRarity.Rare:
                info.Tier = ItemTier.Rare;
                info.Subtitle = "Rare";
                break;
            default:
                info.Tier = ItemTier.Common;
                info.Subtitle = "Common";
                break;
        }

        info.Hint = ClientGroundItemCache.IsHeldBack(in item, LocalActorId, ServerTick)
            ? WorldEntityRenderer.ClaimedHint
            : ClientGroundItemCache.HorizontalDistanceSquared(PlayerPosition, item.Position) <= range * range
                ? WorldEntityRenderer.PickUpHint
                : string.Empty;
        return true;
    }
}
