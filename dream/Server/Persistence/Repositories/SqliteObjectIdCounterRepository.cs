using System.Globalization;
using Dream.Multiplayer.Diagnostics;
using Dream.Server.Persistence.Store;
using Dream.Simulation.Persistence;
using Dream.Simulation.World;

namespace Dream.Server.Persistence.Repositories;

// The next ObjectId of a world: worlds.next_object_id, which the AFTER INSERT triggers of the object
// tables (cubes, enemies, rubble, terrain overrides) raise in the same statement as every insert. LoadNextObjectId is also a heal guard
// for a database edited outside the server: it returns max(stored, highest id in use + 1) over the
// object tables and the inventory slots, and writes the raised value back, logging when it had to.
public sealed class SqliteObjectIdCounterRepository(IWorldStore store, WorldId worldId)
    : SqliteRepositoryBase(store, worldId), IObjectIdCounterRepository
{
    internal const string HealedLogCategory = "server.persistence.object-id-counter-healed";

    private const string StoredSql = "SELECT next_object_id FROM worlds WHERE world_id = $worldId;";

    private const string HighestInUseSql = """
        SELECT MAX(
            COALESCE((SELECT MAX(object_id) FROM placed_cube_instances WHERE world_id = $worldId), 0),
            COALESCE((SELECT MAX(object_id) FROM enemy_instances WHERE world_id = $worldId), 0),
            COALESCE((SELECT MAX(object_id) FROM cube_fracture_pieces WHERE world_id = $worldId), 0),
            COALESCE((SELECT MAX(origin_object_id) FROM cube_fracture_pieces WHERE world_id = $worldId), 0),
            COALESCE((SELECT MAX(object_id) FROM terrain_cell_overrides WHERE world_id = $worldId), 0),
            COALESCE((SELECT MAX(item_instance_id) FROM item_instances WHERE world_id = $worldId), 0),
            COALESCE((SELECT MAX(item_instance_id) FROM player_inventory_slots WHERE world_id = $worldId), 0));
        """;

    // Only raises: a concurrent trigger that already moved the counter past the heal is kept.
    private const string RaiseSql =
        "UPDATE worlds SET next_object_id = $next WHERE world_id = $worldId AND next_object_id < $next;";

    public long LoadNextObjectId()
    {
        long stored = QueryInt64(StoredSql);
        long needed = QueryInt64(HighestInUseSql) + 1;
        if (needed <= stored)
        {
            return stored;
        }

        Execute(RaiseSql, needed, static (command, next) => command.BindInt64("$next", next));
        NetworkDiagnosticsLog.Write(
            HealedLogCategory,
            string.Create(CultureInfo.InvariantCulture, $"world={WorldIdValue} stored={stored} healed={needed}"));
        return needed;
    }
}
