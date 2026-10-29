using Dream.Server.Persistence.Store;
using Dream.Simulation.Persistence;
using Dream.Simulation.World;

namespace Dream.Server.Persistence.Repositories;

// The next ObjectId of a world. C0 shim: no stored counter exists yet, so it is derived as MAX+1
// over every id column. Workstream A replaces this with the stored worlds.next_object_id.
public sealed class SqliteObjectIdCounterRepository(IWorldStore store, WorldId worldId)
    : SqliteRepositoryBase(store, worldId), IObjectIdCounterRepository
{
    private const string NextIdSql = """
        SELECT 1 + MAX(
            COALESCE((SELECT MAX(cube_id) FROM placed_cube_instances WHERE world_id = $worldId), 0),
            COALESCE((SELECT MAX(enemy_id) FROM enemy_instances WHERE world_id = $worldId), 0),
            COALESCE((SELECT MAX(source_cube_id) FROM cube_fracture_pieces WHERE world_id = $worldId), 0),
            COALESCE((SELECT MAX(item_instance_id) FROM item_instances WHERE world_id = $worldId), 0),
            COALESCE((SELECT MAX(item_instance_id) FROM player_inventory_slots WHERE world_id = $worldId), 0));
        """;

    public long LoadNextObjectId() => QueryInt64(NextIdSql);
}
