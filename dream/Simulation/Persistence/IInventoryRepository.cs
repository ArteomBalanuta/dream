namespace Dream.Simulation.Persistence;

public interface IInventoryRepository
{
    IReadOnlyList<PersistedInventorySlot> LoadInventorySlots();

    // One player's inventory slots, the item instances they reference and the starter kit version
    // the player has received, read in one consistent snapshot. The name matches the slot rows
    // exactly (trimmed), like the profile and world state. The runtime loads inventories per player
    // (join prefetch) instead of all of them at startup.
    PersistedPlayerInventory LoadPlayerInventory(string playerName);

    void SaveInventorySlot(PersistedInventorySlot slot);

    void DeleteInventorySlots(string playerName);

    // Deletes one of the player's slot rows, but only while it still holds expectedItemInstanceId: a
    // row that diverged from the caller's view (an earlier write failed) is left alone.
    void DeleteInventorySlot(string playerName, int slotIndex, long expectedItemInstanceId);

    // Deletes this player's rows for the item, then the item row if no slot still references it.
    void DestroyInventoryItem(string playerName, long itemInstanceId);

    // Like DestroyInventoryItem, but throws when the player has no slot row for the item, so the
    // enclosing write's savepoint rolls back (a sell must not pay for an item that was never persisted).
    // The default (a fake) is the plain destroy.
    void DestroyHeldInventoryItem(string playerName, long itemInstanceId) => DestroyInventoryItem(playerName, itemInstanceId);

    // Records that the player has received the starter kit up to kitVersion (see
    // ToolCatalog.StarterKit). Never lowers a recorded version; DeleteInventorySlots keeps it.
    void SaveStarterKitGrant(string playerName, int kitVersion);

    // Adds delta to the player's wallet (an upsert: gold = gold + delta). A negative delta that would
    // take the wallet below zero, or that finds no row, fails the CHECK so the enclosing closure rolls
    // back. Always a delta, never an absolute value, so a rolled-back action cannot keep the gold. The
    // default implementation (a fake without wallets) does nothing.
    void ApplyWalletDelta(string playerName, long delta)
    {
    }

    // M5 trade. Deletes the player's slot rows for the item without deleting the item row; SQLite THROWS
    // when no row was deleted (the item was never persisted for that player). The default (a fake) does
    // nothing.
    void DetachHeldItem(string fromPlayer, long itemInstanceId)
    {
    }

    // M5 trade. Hands the item to toPlayer in the given slot: UPDATE item_instances owner and state
    // 'Inventory', THROWS unless one row changed; then a plain INSERT of the slot row, NOT the upsert, so an
    // occupied slot throws and never overwrites. The default (a fake) does nothing.
    void AttachItem(string toPlayer, long itemInstanceId, int slotIndex, DateTime utc)
    {
    }

    // Upserts one quickbar slot row of the player (M4). The default (a fake) does nothing.
    void SaveQuickbarSlot(PersistedQuickbarSlot slot)
    {
    }
}
