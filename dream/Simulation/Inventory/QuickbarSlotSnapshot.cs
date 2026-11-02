namespace Dream.Simulation.Inventory;

public enum QuickbarEntryKind { None = 0, Skill = 1, Item = 2 }

// One quickbar slot (0..InventoryConstants.QuickbarSlots - 1). Key is a skill key, an item kind, or
// empty for None.
public readonly record struct QuickbarSlotSnapshot(int Slot, QuickbarEntryKind Kind, string Key);
