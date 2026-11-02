namespace Dream.Multiplayer.Protocol;

// M3: sell the bag item at SlotIndex to a vendor NPC. ItemInstanceId must match what the slot holds
// now, so a second sell of the same item fails. Answers with InventoryChangedResult.
public sealed record VendorSellIntent(int NpcActorId, int SlotIndex, long ItemInstanceId) : IPlayerActionPayload;
