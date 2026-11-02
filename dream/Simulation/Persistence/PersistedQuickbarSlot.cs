namespace Dream.Simulation.Persistence;

// One row of player_quickbar_slots (M4). Kind is a QuickbarEntryKind value (0 none, 1 skill, 2 item);
// Key is the skill key or item kind, empty for none.
public sealed record PersistedQuickbarSlot(string PlayerName, int Slot, int Kind, string Key, DateTime UpdatedUtc);
