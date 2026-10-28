namespace Dream.Simulation.Persistence;

// One player's persisted inventory: its slot rows and the item instances those slots reference
// (see IWorldPersistenceRepository.LoadPlayerInventory). Items is distinct by id; an item in two
// slots (a backpack slot and the equipped slot) appears once.
public sealed record PersistedPlayerInventory(
    IReadOnlyList<PersistedInventorySlot> Slots,
    IReadOnlyList<PersistedItemInstance> Items)
{
    public static PersistedPlayerInventory Empty { get; } = new([], []);
}
