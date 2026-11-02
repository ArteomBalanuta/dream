namespace Dream.Multiplayer.Protocol;

// M3: buy one item of a kind from a vendor NPC. The vendor keeps no state: every buy rechecks the NPC
// id, the range, the stock, the gold and the bag room, and answers with InventoryChangedResult.
public sealed record VendorBuyIntent(int NpcActorId, string ItemKind) : IPlayerActionPayload;
