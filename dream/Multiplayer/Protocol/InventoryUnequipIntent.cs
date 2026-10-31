namespace Dream.Multiplayer.Protocol;

// Empties an equipment slot (SlotIndex 100..103); the item stays in the backpack.
public sealed record InventoryUnequipIntent(int SlotIndex) : IPlayerActionPayload;
