namespace Dream.Multiplayer.Protocol;

// Equips the item at SlotIndex: a bag anchor (0..63), or an equipment slot (100..111) to move a worn
// item to another equipment slot, which needs a target and swaps with what the target holds when that
// item may occupy the source slot. TargetSlotIndex is an equipment slot index (100..111); without one
// the item goes to the first slot its definition lists (so a torch click still goes to the main
// hand), except that an item whose first slot is a ring takes the first empty ring slot. A null
// target is left off the wire.
public sealed record InventoryEquipIntent(int SlotIndex, int? TargetSlotIndex = null) : IPlayerActionPayload;
