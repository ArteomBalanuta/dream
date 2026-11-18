using Microsoft.Data.Sqlite;
using OpenTK.Mathematics;
using Xunit;
using Dream.Multiplayer.Diagnostics;
using Dream.Server.Persistence;
using Dream.Server.Persistence.Repositories;
using Dream.Server.Persistence.Worlds;
using Dream.Server.Worlds;
using Dream.Simulation.Enemies;
using Dream.Simulation.Persistence;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using Dream.Simulation.World.MapSources;
using Dream.Tests.Integration;
using static Dream.Tests.Integration.ObjectIdTestSupport;
using static Dream.Tests.Integration.PersistentWorldSeedDeterminismIntegrationTests;

namespace Dream.Tests.Server.Worlds;

// Persistence wave E (C): a real database (opened through the production bootstrap, so world 1 is the
// generated default), the catalog over it, and a bridge rig that restores any world of it, generated
// or stored, the way a hosted runtime does.
internal sealed class TerrainCliFixture : IDisposable
{
    public const string Builder = "builder";

    public TerrainCliFixture()
    {
        Database = new TempDatabase();
        Schema = new SqliteSchemaBootstrapper(
            Database.DatabasePath,
            Path.Combine(RepoRoot.Find(), "dream", "config", "player-profiles.init.sql"));
        using (Database.OpenBundle())
        {
        }

        SqlitePools.Release(Database.DatabasePath);
        Catalog = new SqliteWorldCatalog(Schema);
    }

    public TempDatabase Database { get; }

    public SqliteSchemaBootstrapper Schema { get; }

    public SqliteWorldCatalog Catalog { get; }

    public (int Exit, string Out, string Err) Cli(params string[] args)
    {
        StringWriter output = new();
        StringWriter error = new();
        int exit = WorldCli.Run(args, () => Catalog, output, error);
        return (exit, output.ToString(), error.ToString());
    }

    public long Scalar(string sql)
    {
        SqlitePools.Release(Database.DatabasePath);
        return ObjectIdTestSupport.Scalar(Database.DatabasePath, sql);
    }

    public void Exec(string sql)
    {
        SqlitePools.Release(Database.DatabasePath);
        using SqliteConnection connection = ObjectIdTestSupport.Open(Database.DatabasePath, readOnly: false);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    // Opens a restored rig on world 1 (generated), fractures the given number of raised terrain cells
    // (flat ones, then sculpture ones), stores everything and closes it again.
    public List<GeometryCellKey> FractureTerrain(int flat, int sculpture = 0)
    {
        List<GeometryCellKey> cells = [];
        using TerrainRig rig = OpenRig(WorldId.Default);
        rig.Restore();
        foreach (GeometryCellKey cell in RaisedCells(rig.Map, sculpture: false).Take(flat)
            .Concat(RaisedCells(rig.Map, sculpture: true).Take(sculpture)).ToList())
        {
            rig.FractureTerrain(cell);
            cells.Add(cell);
        }

        return cells;
    }

    public TerrainRig OpenRig(WorldId id)
    {
        WorldTerrainSource source = Catalog.ReadTerrainSource(id);
        return new TerrainRig(Schema, source);
    }

    public static IEnumerable<GeometryCellKey> RaisedCells(Map map, bool sculpture)
    {
        return map.GeometryCells
            .Where(pair => pair.Key.Y > 0
                && (pair.Value.SubdivisionState != null) == sculpture
                && map.IsSourceCell(pair.Key)
                && map.Cubes[pair.Key.X, pair.Key.Y, pair.Key.Z] != null)
            .Select(pair => pair.Key)
            .OrderBy(key => (key.X, key.Z, key.Y));
    }

    public void Dispose()
    {
        SqlitePools.Release(Database.DatabasePath);
        Database.Dispose();
    }
}

// A runtime persistence bridge over one world's repositories and a map built from that world's source
// (the generator, or its stored chunks): RestoreWorld applies overrides, cubes, rubble and enemies.
internal sealed class TerrainRig : IDisposable
{
    private readonly SqlitePlayerProfileRepository _profiles;
    private readonly SqliteWorldPersistenceRepository _world;

    public TerrainRig(SqliteSchemaBootstrapper schema, WorldTerrainSource source)
    {
        WorldDescriptor descriptor = source.Record.Descriptor;
        Map = new Map(MapSources.ForWorld(descriptor, source.Seed, source.StoredChunks), physics: null, headless: true);
        _profiles = new SqlitePlayerProfileRepository(schema);
        _world = new SqliteWorldPersistenceRepository(schema, descriptor.Id);
        Host = new TerrainEditSqlitePersistenceTests.FakeHost(Map);
        Bridge = new RuntimePersistenceBridge(
            Host,
            _profiles,
            _world,
            new TerrainEditSqlitePersistenceTests.NullCheckpointWriter(),
            PersistenceWriteQueue.Inline(),
            WorldObjects,
            Enemies,
            new SceneEventHub(),
            ObjectIds,
            new ObjectIdentityMap());
        ObjectIds.Restore(_world.LoadNextObjectId());
    }

    public Map Map { get; }

    public TerrainEditSqlitePersistenceTests.FakeHost Host { get; }

    public ObjectIdAllocator ObjectIds { get; } = new();

    public WorldObjectRegistry WorldObjects { get; } = new();

    public EnemyRegistry Enemies { get; } = new();

    public RuntimePersistenceBridge Bridge { get; }

    // Restores the world and returns the diagnostics lines it logged.
    public List<(string Category, string Message)> Restore()
    {
        List<(string Category, string Message)> lines = [];
        using (NetworkDiagnosticsLog.CaptureOnCurrentThread((category, message) => lines.Add((category, message))))
        {
            Bridge.RestoreWorld();
        }

        return lines;
    }

    public void FractureTerrain(GeometryCellKey cell)
    {
        Cube cube = Map.Cubes[cell.X, cell.Y, cell.Z]!;
        WorldObjectRecord record = WorldObjects.RegisterCube(cube, Map, maxHealth: 1);
        Assert.True(Map.IsSourceCell(cell));
        Assert.True(Map.FractureCubeIntoPieces(cell));
        Bridge.DeletePersistedPlacedCube(record.Actor.Id);
        Bridge.PersistTerrainCellRemoved(record, cell);
        Bridge.PersistFracturePieces(record, cell);
        WorldObjects.Unregister(cube, preserveDetachedRecord: false);
    }

    // A player-owned cube placed in the (empty) cell and stored.
    public void PlaceCube(GeometryCellKey cell)
    {
        _profiles.GetOrCreate(TerrainCliFixture.Builder);
        Cube placed = Map.TryAddCube(new Vector3(cell.X, cell.Y, cell.Z))!;
        Assert.NotNull(placed);
        WorldObjectRecord record = WorldObjects.RegisterCube(
            placed, Map, maxHealth: 5, ownerPlayerName: TerrainCliFixture.Builder);
        Bridge.PersistPlacedCube(record, GeometryMaterialId.Grass);
    }

    public void Dispose()
    {
        _profiles.Dispose();
        _world.Dispose();
    }
}
