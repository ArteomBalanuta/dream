namespace Dream.Simulation.Persistence;

public readonly record struct PersistedInventorySlot(
    string PlayerName,
    int SlotIndex,
    long ItemInstanceId,
    DateTime UpdatedUtc);
