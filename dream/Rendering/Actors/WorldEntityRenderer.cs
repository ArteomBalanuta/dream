using Dream.Client.Replication;
using Dream.Rendering.Ui;
using Dream.Simulation.Inventory;
using OpenTK.Mathematics;

namespace Dream.Rendering.Actors;

// A flat-coloured axis-aligned box, the placeholder body of a world entity.
public readonly record struct WorldBox(Vector3 Center, Vector3 Size, Vector3 Color);

// A label over a ground item or an NPC. Click is what a click on it does.
public readonly record struct WorldEntityLabel(
    Vector3 WorldAnchor,
    string Name,
    string Sub,
    HudLabelClick Click,
    ItemTier Tier,
    bool Dim);

// What the ground items and NPCs look like for one frame (M3), GL-free so it can be tested: the
// placeholder boxes to draw and the labels to project. The art is a stand-in (dark, flat tints) until
// real models exist. Reads the caches; keeps nothing.
public static class WorldEntityRenderer
{
    // Boxes beyond this horizontal distance from the camera are not drawn.
    public const float DrawDistance = 80f;

    // Ground labels: only items this close to the player are listed, nearest first, at most MaxItemLabels.
    public const float ItemLabelRange = 12f;
    public const int MaxItemLabels = 8;

    // NPC labels show from further off so a vendor can be found.
    public const float NpcLabelRange = 30f;

    public const string PickUpHint = "[E] Pick up";
    public const string TalkHint = "[E] Talk";
    public const string ClaimedHint = "Claimed";

    private static readonly Vector3 ItemSize = new(0.3f, 0.3f, 0.3f);
    private static readonly Vector3 RobeSize = new(0.7f, 1.3f, 0.5f);
    private static readonly Vector3 HeadSize = new(0.34f, 0.34f, 0.34f);
    private static readonly Vector3 RobeColor = new(0.20f, 0.09f, 0.09f);
    private static readonly Vector3 HeadColor = new(0.52f, 0.46f, 0.38f);

    // Bone, dim rose and dim mint: the tier colours of the item names, darkened for the grim palette.
    public static Vector3 ItemColor(ItemRarity rarity) => rarity switch
    {
        ItemRarity.Uncommon => new Vector3(0.62f, 0.30f, 0.38f),
        ItemRarity.Rare => new Vector3(0.24f, 0.62f, 0.48f),
        _ => new Vector3(0.56f, 0.53f, 0.47f)
    };

    public static void CollectBoxes(ClientGroundItemCache ground, ClientNpcCache npcs, Vector3 eye, List<WorldBox> destination)
    {
        ArgumentNullException.ThrowIfNull(ground);
        ArgumentNullException.ThrowIfNull(npcs);
        ArgumentNullException.ThrowIfNull(destination);
        float limit = DrawDistance * DrawDistance;
        for (int n = 0; n < ground.Count; n++)
        {
            ClientGroundItem item = ground[n];
            if (ClientGroundItemCache.HorizontalDistanceSquared(eye, item.Position) > limit)
            {
                continue;
            }

            destination.Add(new WorldBox(item.Position + new Vector3(0f, ItemSize.Y * 0.5f, 0f), ItemSize, ItemColor(item.Rarity)));
        }

        for (int n = 0; n < npcs.Count; n++)
        {
            ClientNpc npc = npcs[n];
            if (ClientGroundItemCache.HorizontalDistanceSquared(eye, npc.Position) > limit)
            {
                continue;
            }

            destination.Add(new WorldBox(npc.Position + new Vector3(0f, RobeSize.Y * 0.5f, 0f), RobeSize, RobeColor));
            destination.Add(new WorldBox(npc.Position + new Vector3(0f, RobeSize.Y + (HeadSize.Y * 0.5f), 0f), HeadSize, HeadColor));
        }
    }

    // NPC labels first (few), then the nearest ground items inside ItemLabelRange. An item another
    // player holds a claim on is greyed and says so.
    public static void CollectLabels(
        ClientGroundItemCache ground,
        ClientNpcCache npcs,
        Vector3 player,
        int localActorId,
        ulong serverTick,
        List<WorldEntityLabel> destination)
    {
        ArgumentNullException.ThrowIfNull(ground);
        ArgumentNullException.ThrowIfNull(npcs);
        ArgumentNullException.ThrowIfNull(destination);
        float npcRange = NpcLabelRange * NpcLabelRange;
        float talkRange = ClientNpcCache.InteractRange * ClientNpcCache.InteractRange;
        for (int n = 0; n < npcs.Count; n++)
        {
            ClientNpc npc = npcs[n];
            float d = ClientGroundItemCache.HorizontalDistanceSquared(player, npc.Position);
            if (d > npcRange)
            {
                continue;
            }

            destination.Add(new WorldEntityLabel(
                npc.Position + new Vector3(0f, RobeSize.Y + HeadSize.Y + 0.3f, 0f),
                npc.DisplayName,
                d <= talkRange ? TalkHint : string.Empty,
                new HudLabelClick(HudLabelClickKind.InteractNpc, npc.ActorId),
                ItemTier.Common,
                Dim: false));
        }

        // The nearest MaxItemLabels items by insertion into a small sorted window.
        Span<float> bestDistance = stackalloc float[MaxItemLabels];
        Span<int> bestIndex = stackalloc int[MaxItemLabels];
        int found = 0;
        float itemRange = ItemLabelRange * ItemLabelRange;
        for (int n = 0; n < ground.Count; n++)
        {
            float d = ClientGroundItemCache.HorizontalDistanceSquared(player, ground[n].Position);
            if (d > itemRange)
            {
                continue;
            }

            int at = found < MaxItemLabels ? found : MaxItemLabels;
            while (at > 0 && bestDistance[at - 1] > d)
            {
                at--;
            }

            if (at >= MaxItemLabels)
            {
                continue;
            }

            int last = Math.Min(found, MaxItemLabels - 1);
            for (int move = last; move > at; move--)
            {
                bestDistance[move] = bestDistance[move - 1];
                bestIndex[move] = bestIndex[move - 1];
            }

            bestDistance[at] = d;
            bestIndex[at] = n;
            found = Math.Min(found + 1, MaxItemLabels);
        }

        float pickUpRange = ClientGroundItemCache.PickupRange * ClientGroundItemCache.PickupRange;
        for (int k = 0; k < found; k++)
        {
            ClientGroundItem item = ground[bestIndex[k]];
            bool heldBack = ClientGroundItemCache.IsHeldBack(in item, localActorId, serverTick);
            string sub = heldBack ? ClaimedHint : bestDistance[k] <= pickUpRange ? PickUpHint : string.Empty;
            destination.Add(new WorldEntityLabel(
                item.Position + new Vector3(0f, 0.6f, 0f),
                item.DisplayName,
                sub,
                new HudLabelClick(HudLabelClickKind.PickUpGround, item.ItemInstanceId),
                ItemTierStyleOf(item.Rarity),
                heldBack));
        }
    }

    private static ItemTier ItemTierStyleOf(ItemRarity rarity) => rarity switch
    {
        ItemRarity.Uncommon => ItemTier.Uncommon,
        ItemRarity.Rare => ItemTier.Rare,
        _ => ItemTier.Common
    };
}
