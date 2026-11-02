namespace Dream.Simulation.Persistence;

// One player's persisted inventory: its slot rows and the item instances those slots reference
// (see IWorldPersistenceRepository.LoadPlayerInventory). Items is distinct by id; an item in two
// slots (a backpack slot and the equipped slot) appears once. StarterKitVersion is the starter kit
// version the player has received (player_starter_grants), or null when none is recorded. Gold is the
// player's wallet (player_wallets), 0 when none is recorded.
// Quickbar is the player's player_quickbar_slots rows, or null when the player never bound one.
// Quests is the player's player_quests rows (M6), or null when the load did not read them.
public sealed record PersistedPlayerInventory(
    IReadOnlyList<PersistedInventorySlot> Slots,
    IReadOnlyList<PersistedItemInstance> Items,
    int? StarterKitVersion = null,
    long Gold = 0,
    IReadOnlyList<PersistedQuickbarSlot>? Quickbar = null,
    IReadOnlyList<PersistedQuest>? Quests = null)
{
    public static PersistedPlayerInventory Empty { get; } = new([], []);
}
