using Dream.Simulation.Content;
using Dream.Simulation.Inventory;
using Dream.Simulation.Stats;
using Dream.Simulation.World;
using Xunit;

namespace Dream.Tests.Multiplayer.Protocol;

// Inventory snapshots for the wire tests. Protocol v13 does not send an item's text: the reader takes it
// from the shipped content catalog, so a snapshot that is to survive a round trip must carry what the
// catalog says for a kind it knows (the way PlayerInventoryService builds one). These helpers build
// such snapshots.
internal static class InventoryWireSamples
{
    // An item as the service shows it: the definition's text, light and slots, the roll's modifiers when
    // rolled (else the definition's, null when there are none).
    public static InventoryItemSnapshot Item(
        string kind,
        long id,
        ItemRarity rarity = ItemRarity.Common,
        bool rolled = false,
        StatModifier[]? rolledModifiers = null,
        int stackCount = 1)
    {
        Assert.True(ContentCatalog.Shipped.TryGetItem(kind, out ItemDefinition? definition), kind);
        IReadOnlyList<StatModifier> effective = rolled ? rolledModifiers ?? [] : definition.Modifiers;
        return new InventoryItemSnapshot(
            id,
            definition.Key,
            definition.DisplayName,
            definition.VisualId,
            stackCount,
            definition.Description,
            definition.DetailLines,
            effective.Count == 0 ? null : effective,
            definition.HeldLight,
            definition.EquipSlotMask,
            rarity,
            rolled);
    }

    // An item of a kind the catalog does not know, as the service shows it: no text, no stats, no light.
    public static InventoryItemSnapshot Unknown(string kind, long id, string displayName, string visualId, int stackCount = 1)
        => new(id, kind, displayName, visualId, stackCount, ToolCatalog.UnknownItemDescription, []);

    public static InventorySlotSnapshot Bag(int anchor, InventoryItemSnapshot item) => new(anchor, item);

    public static InventorySlotSnapshot Worn(int slotIndex, InventoryItemSnapshot item) => new(slotIndex, item, IsEquipped: true);

    // The inventory a new player joins with, from the real service: the hammer worn, four items in the bag.
    public static PlayerInventorySnapshot Starter(string playerName)
    {
        PlayerInventoryService service = new(worldPersistence: null, writes: null, objectIds: new ObjectIdAllocator());
        Assert.True(service.Ensure(playerName));
        return service.BuildSnapshot(playerName);
    }
}
