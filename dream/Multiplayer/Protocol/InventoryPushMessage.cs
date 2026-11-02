using Dream.Simulation.Inventory;

namespace Dream.Multiplayer.Protocol;

// One loot outcome for the display: the item's name and rarity, and whether it was lost (backpack full).
// Gone (only with Lost) means the overflow could not even land on the ground, so it is truly lost.
public readonly record struct LootNotice(string ItemDisplayName, ItemRarity Rarity, bool Lost, bool Gone = false);

// Wave I (protocol v11). An unprompted, server-only message to one session after loot lands: a fresh
// inventory snapshot (null when nothing changed, for example every drop was lost) and the notices to
// show, at most MaxNotices. It travels outside AuthoritativeActionResult so tick goldens, which hash
// every action result, do not move.
public sealed record InventoryPushMessage(
    PlayerInventorySnapshot? Inventory,
    IReadOnlyList<LootNotice> Notices) : IServerToClientMessage
{
    public const string Kind = "server.inventory-push";
    public const int MaxNotices = 8;

    public string MessageKind => Kind;
}
