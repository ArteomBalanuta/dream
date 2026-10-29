using Dream.Simulation.World;

namespace Dream.Server.Worlds;

// The worlds stored in one database.
public interface IWorldCatalog
{
    // Every world, ORDER BY world_id.
    IReadOnlyList<WorldRecord> List();

    WorldRecord? TryGet(WorldId id);

    // Names compare ignoring case (NOCASE).
    WorldRecord? TryGetByName(string name);

    // Validates the options; generates the seed when it is null; created_schema_version is the
    // current schema version. Throws WorldNameTakenException on a name conflict.
    WorldRecord Create(WorldCreateOptions options);

    // In one owned transaction: a new world with every world-scoped row of source, the same local
    // ids, and the source's seed and world time.
    WorldRecord Copy(WorldId source, string newName);

    // In one owned transaction: an explicit DELETE per world-scoped table (children first), then
    // the world row. False when the world does not exist.
    bool Delete(WorldId id);

    // Default: world 1, else the lowest id, else create "default" with newWorldSize.
    // Id: must exist (WorldNotFoundException).
    // Name: open it, or create it with newWorldSize.
    // created is true when this call created the world.
    WorldRecord Resolve(WorldSelector selector, int newWorldSize, out bool created);
}
