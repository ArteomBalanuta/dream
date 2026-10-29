using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using Dream.Server.Persistence;
using Dream.Server.Persistence.Worlds;
using Dream.Server.Worlds;
using Dream.Simulation.Rules;
using Dream.Simulation.World;
using Dream.Tests.Server.Persistence.Migrations;

namespace Dream.Tests.Integration;

// Persistence wave B test support: opens a world of a shared test database as a persistence bundle,
// the way ServerBootstrap.CreatePersistenceBundle does for a selected world (catalog row, repositories
// bound to the world id, the world's descriptor, stored seed and clock), without going through the
// process-wide DREAM_WORLD variable. The bundle is handed to ServerBootstrap.CreateAuthoritativeHost.
internal static class WorldBundles
{
    public const string SkipReason = "needs B1 + B2 + B4 merged";

    public static readonly string[] WorldTables =
    [
        "player_world_state", "placed_cube_instances", "cube_fracture_pieces", "enemy_instances",
        "item_instances", "player_inventory_slots", "player_starter_grants"
    ];

    public static SqliteWorldCatalog OpenCatalog(MigrationTestDatabase database)
    {
        database.Schema.EnsureSchema();
        return new SqliteWorldCatalog(database.Schema);
    }

    public static ServerPersistenceBundle Open(MigrationTestDatabase database, WorldId id)
    {
        SqliteWorldCatalog catalog = OpenCatalog(database);
        WorldRecord record = catalog.TryGet(id) ?? throw new InvalidOperationException($"world {id} does not exist");
        return Bind(database.Schema, record);
    }

    public static ServerPersistenceBundle Bind(SqliteSchemaBootstrapper schema, WorldRecord record)
    {
        WorldId id = record.Descriptor.Id;
        ulong seed = record.Seed ?? new SqliteWorldMetadataRepository(schema, id).GetOrCreateSimulationSeed().Seed;
        SqliteCombatFormulaRepository formulas = new(schema);
        return new ServerPersistenceBundle(
            schema,
            new SqlitePlayerProfileRepository(schema),
            new SqliteWorldPersistenceRepository(schema, id),
            formulas,
            CombatFormulaService.Create(formulas),
            seed,
            record.WorldTimeTicks,
            id,
            record.Descriptor);
    }

    public static long Count(MigrationTestDatabase database, string table, WorldId world)
    {
        return Convert.ToInt64(
            database.Scalar($"SELECT COUNT(*) FROM {table} WHERE world_id = {world};"),
            CultureInfo.InvariantCulture);
    }

    // Every row of one world in one table as text, in primary-key order, for before/after comparisons.
    public static string Dump(MigrationTestDatabase database, string table, WorldId world)
    {
        using SqliteConnection connection = database.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT * FROM {table} WHERE world_id = {world} ORDER BY 1, 2, 3;";
        using SqliteDataReader reader = command.ExecuteReader();
        StringBuilder text = new();
        while (reader.Read())
        {
            for (int i = 0; i < reader.FieldCount; i++)
            {
                text.Append(Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture)).Append('|');
            }

            text.Append('\n');
        }

        return text.ToString();
    }
}
