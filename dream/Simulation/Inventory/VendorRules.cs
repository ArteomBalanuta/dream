using OpenTK.Mathematics;
using Dream.Simulation.Content;

namespace Dream.Simulation.Inventory;

// The pure rules of vendor trade (M3): what a sold item is worth and when a player is near enough.
public static class VendorRules
{
    // How close a player must stand to an NPC (horizontal metres) to open its window, buy or sell. Checked
    // again on every buy and sell, because the server keeps no vendor session.
    public const float NpcInteractRange = 6f;

    // The gold a vendor pays for an item of the definition and rarity: its sellPrice times 1, 2 or 4. 0
    // means the vendor does not buy it (no sell price, or an unknown rarity). A vendor stocks unrolled
    // (Common) items only, each priced above its sell price (ContentCatalog), so buying then selling back
    // always loses gold.
    public static long SellPrice(ItemDefinition definition, ItemRarity rarity)
    {
        ArgumentNullException.ThrowIfNull(definition);
        long multiplier = rarity switch
        {
            ItemRarity.Common => 1,
            ItemRarity.Uncommon => 2,
            ItemRarity.Rare => 4,
            _ => 0,
        };
        return definition.SellPrice * multiplier;
    }

    // Horizontal distance only: an NPC stands on the floor and the player's feet may be a step above it.
    public static bool InRange(Vector3 player, Vector3 npc)
    {
        float dx = player.X - npc.X;
        float dz = player.Z - npc.Z;
        return (dx * dx) + (dz * dz) <= NpcInteractRange * NpcInteractRange;
    }
}
