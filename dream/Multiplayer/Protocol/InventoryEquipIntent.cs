namespace Dream.Multiplayer.Protocol;

// Equips the item in backpack slot SlotIndex. TargetSlotIndex is an equipment slot index (100..103);
// without one the item goes to the first slot its definition lists (so a torch click still goes to
// the main hand). A null target is left off the wire.
public sealed record InventoryEquipIntent(int SlotIndex, int? TargetSlotIndex = null) : IPlayerActionPayload;
