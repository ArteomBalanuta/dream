using Dream.Server.Persistence.Store;
using Dream.Simulation.Persistence;
using Dream.Simulation.World;

namespace Dream.Server.Persistence.Repositories;

// player_quests (migration 0014). Contract stub: the SQL is written by M6 workstream B; until then
// every member keeps the interface default (no quests are stored or loaded).
public sealed class SqliteQuestRepository(IWorldStore store, WorldId worldId)
    : SqliteRepositoryBase(store, worldId), IQuestRepository
{
}
