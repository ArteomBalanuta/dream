namespace Dream.Multiplayer.Protocol;

// Empties an equipment slot (SlotIndex 100..111); the item goes back into the bag. TargetCell is the
// bag anchor (0..63) the item should land on, which must be free for the item's whole footprint; null
// (the field is left out of the wire form) lets the server take the first fit.
public sealed record InventoryUnequipIntent(int SlotIndex, int? TargetCell = null) : IPlayerActionPayload;
