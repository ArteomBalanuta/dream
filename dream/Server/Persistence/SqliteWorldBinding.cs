using Dream.Simulation.World;

namespace Dream.Server.Persistence;

// C0 guard for the world-bound repository constructors: until workstream B1 binds the SQL to a
// world id, the tables hold world 1 only, so any other world is refused instead of silently
// reading or writing world 1's rows.
internal static class SqliteWorldBinding
{
    public static SqliteSchemaBootstrapper RequireDefaultWorld(SqliteSchemaBootstrapper schema, WorldId worldId)
    {
        if (worldId != WorldId.Default)
        {
            throw new NotSupportedException(
                $"World {worldId} is not supported by this schema yet; only world {WorldId.Default} is.");
        }

        return schema;
    }
}
