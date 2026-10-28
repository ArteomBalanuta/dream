namespace Dream.Simulation.Persistence;

// The whole persisted world: one interface per aggregate, combined for callers (and test fakes) that
// use all of them. The members live on the per-aggregate interfaces.
public interface IWorldPersistenceRepository :
    IPlayerWorldStateRepository,
    IPlacedObjectRepository,
    IRubbleRepository,
    IEnemyRepository,
    IItemRepository,
    IInventoryRepository
{
}
