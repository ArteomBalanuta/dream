using Dream.Server.Worlds;
using Dream.Simulation.World;

namespace Dream.Server.Persistence.Worlds;

// The worlds table as an IWorldCatalog. Skeleton until workstream B4.
public sealed class SqliteWorldCatalog : IWorldCatalog
{
    public SqliteWorldCatalog(SqliteSchemaBootstrapper schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        Schema = schema;
    }

    internal SqliteSchemaBootstrapper Schema { get; }

    public IReadOnlyList<WorldRecord> List() => throw new NotImplementedException("B4");

    public WorldRecord? TryGet(WorldId id) => throw new NotImplementedException("B4");

    public WorldRecord? TryGetByName(string name) => throw new NotImplementedException("B4");

    public WorldRecord Create(WorldCreateOptions options) => throw new NotImplementedException("B4");

    public WorldRecord Copy(WorldId source, string newName) => throw new NotImplementedException("B4");

    public bool Delete(WorldId id) => throw new NotImplementedException("B4");

    public WorldRecord Resolve(WorldSelector selector, int newWorldSize, out bool created) =>
        throw new NotImplementedException("B4");
}
