using Dream.Server.Persistence.Store;
using Dream.Simulation.Persistence;

namespace Dream.Server.Persistence.Repositories;

// Skeleton from the wave A contract commit; A3 moves the SQL in.
public sealed partial class SqliteInventoryRepository(IWorldStore store)
    : SqliteRepositoryBase(store), IInventoryRepository
{
    public IReadOnlyList<PersistedInventorySlot> LoadInventorySlots() =>
        throw new NotImplementedException("A3");

    public PersistedPlayerInventory LoadPlayerInventory(string playerName) =>
        throw new NotImplementedException("A3");

    public void SaveInventorySlot(PersistedInventorySlot slot) =>
        throw new NotImplementedException("A3");

    public void DeleteInventorySlots(string playerName) =>
        throw new NotImplementedException("A3");
}
