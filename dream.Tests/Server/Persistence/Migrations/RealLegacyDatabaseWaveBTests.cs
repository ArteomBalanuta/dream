using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using Xunit;
using Xunit.Abstractions;
using Dream.Server;
using Dream.Server.Persistence;
using Dream.Server.Persistence.Migrations;
using Dream.Simulation.Inventory;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using Dream.Tests.Integration;

namespace Dream.Tests.Server.Persistence.Migrations;

// Tester, persistence wave B: real databases written by earlier builds (copied from a developer's
// config folder: the v0 backup taken before the first migration, and the v1 database next to it, each
// with two players, five placed cubes, 17 fracture pieces, a wolf and two torches) go through the
// production bootstrap to schema v3. The v2 case is built from the v1 file with migrations 0001-0002
// plus a lamp and starter grants. Checks: one backup of the right pair of versions, integrity and
// foreign keys clean, every row in world 1 unchanged, the seed and the clock moved onto the worlds
// row, the same clock phase, and a lamp placed afterwards that survives a restart.
public sealed class RealLegacyDatabaseWaveBTests(ITestOutputHelper output)
{
    private const string Session = "wave-b-real";

    private static readonly string[] WorldTables = WorldBundles.WorldTables;

    public static TheoryData<string> Origins => new() { "v0", "v1", "v2" };

    [Theory]
    [MemberData(nameof(Origins))]
    public async Task RealLegacyDatabase_MigratesToV3_KeepsEverything_AndServesWorld1(string origin)
    {
        using MigrationTestDatabase database = new();
        string fixture = Path.Combine(
            RepoRoot.Find(), "dream.Tests", "Server", "Persistence", "Migrations", "Fixtures",
            origin == "v0" ? "real-legacy-v0.db" : "real-legacy-v1.db");
        File.Copy(fixture, database.DatabasePath);
        Assert.Equal(0, database.Backups().Length);

        string fromVersion = "0000";
        if (origin == "v2")
        {
            // v1 -> v2 with the shipped migrations 0001-0002 only; the old schema script runs first.
            fromVersion = "0002";
            database.Schema.EnsureSchema(new SqliteSchemaMigrator(MigrationSet().Take(2).ToList()));
            foreach (string stale in database.Backups())
            {
                File.Delete(stale);
            }

            database.ExecuteScript("""
                INSERT INTO placed_cube_instances (cube_id, owner_player_name, cell_x, cell_y, cell_z, center_x, center_y, center_z,
                    shape_kind, material_id, current_health, max_health)
                    VALUES (2000, 'codex-client-2', 30.0, 1.0, 40.0, 30.5, 1.5, 40.5, 'SolidCube', 'Lamp', 3, 3);
                INSERT INTO player_starter_grants (player_name, kit_version, updated_utc)
                    VALUES ('codex-client-1', 2, '2026-10-28T21:59:08.000Z'), ('codex-client-2', 2, '2026-10-28T21:59:11.000Z');
                """);
            Assert.Equal(2L, database.Scalar("SELECT MAX(version) FROM schema_version;"));
        }
        else if (origin == "v1")
        {
            fromVersion = "0001";
            Assert.Equal(1L, database.Scalar("SELECT MAX(version) FROM schema_version;"));
        }
        else
        {
            Assert.Equal(0L, database.Scalar("SELECT COUNT(*) FROM sqlite_master WHERE name = 'schema_version';"));
        }

        MigrationTestDatabase.ReleasePooledConnections();
        string before = DumpAll(database);
        List<SlotRow> slotsBefore = BagGridMigrationAsserts.ReadSlots(database);
        string oldSeed = (string)database.Scalar("SELECT value FROM world_metadata WHERE key = 'simulation_seed';")!;
        long oldTime = Convert.ToInt64(database.Scalar("SELECT value FROM world_metadata WHERE key = 'world_time_ticks';"), CultureInfo.InvariantCulture);
        Assert.True(oldTime > 0, "the real database should carry a non-zero world time");
        string[] backupsBeforeStart = database.Backups();

        List<string> log = [];
        long lampsBefore = origin == "v2" ? 1 : 0;

        // Migration happens here.
        WorldClockSettings settings = WorldClockSettings.FromDayLengthMinutes(
            ServerBootstrap.ResolveDayLengthMinutes(Environment.GetEnvironmentVariable(ServerBootstrap.DayLengthEnvironmentVariable)));
        WorldDayPhase phaseBefore = new WorldClock(settings, oldTime).Phase;
        using (ServerPersistenceBundle bundle = ServerBootstrap.CreatePersistenceBundle(
            database.DatabasePath, MigrationTestDatabase.InitScriptPath, log: log.Add))
        {
            foreach (string line in log)
            {
                output.WriteLine(line);
            }

            Assert.DoesNotContain(log, line => line.Contains(oldSeed, StringComparison.Ordinal));
            Assert.Equal(WorldId.Default, bundle.WorldId);
            Assert.Equal(ulong.Parse(oldSeed, CultureInfo.InvariantCulture), bundle.SimulationSeed);
            Assert.Equal(oldTime, bundle.WorldTimeTicks);
            Assert.Equal(56, bundle.Descriptor.Width);

            // Exactly one new backup, named for the version pair, and it is the pre-migration file.
            string backup = Assert.Single(database.Backups().Except(backupsBeforeStart));
            Assert.Matches($@"persistence\.sqlite\.v{fromVersion}-to-v{MigrationTestDatabase.ShippedLatestVersion:D4}\.\d{{8}}T\d{{6}}Z\.bak$", backup);
            using (SqliteConnection backupConnection = MigrationTestDatabase.OpenFile(backup))
            {
                using SqliteCommand command = backupConnection.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name = 'worlds';";
                Assert.Equal(0L, (long)command.ExecuteScalar()!);
            }

            MigrationTestDatabase.ReleasePooledConnections();
            Assert.Equal("ok", database.Scalar("PRAGMA integrity_check;"));
            using (SqliteConnection connection = database.Open())
            {
                using SqliteCommand command = connection.CreateCommand();
                command.CommandText = "PRAGMA foreign_key_check;";
                using SqliteDataReader reader = command.ExecuteReader();
                Assert.False(reader.Read(), "foreign_key_check must be clean");
            }

            AssertObjectIdCounterAndTriggers(database);
            Assert.Equal((long)MigrationTestDatabase.ShippedLatestVersion, database.Scalar("SELECT MAX(version) FROM schema_version;"));
            Assert.Equal(1L, database.Scalar("SELECT COUNT(*) FROM worlds;"));
            Assert.Equal(oldSeed, database.Scalar("SELECT seed FROM worlds WHERE world_id = 1;"));
            Assert.Equal(oldTime, database.Scalar("SELECT world_time_ticks FROM worlds WHERE world_id = 1;"));
            Assert.Equal("default", database.Scalar("SELECT name FROM worlds WHERE world_id = 1;"));
            Assert.Equal(0L, database.Scalar("SELECT COUNT(*) FROM world_metadata WHERE key IN ('simulation_seed', 'world_time_ticks');"));
            foreach (string table in WorldTables)
            {
                Assert.Equal(0L, database.Scalar($"SELECT COUNT(*) FROM {table} WHERE world_id <> 1;"));
            }

            // Cube cells are INTEGER now; the rest of every row is what it was. The inventory slots are
            // compared apart (migration 0010 remaps them): every item and every worn row survives and no
            // worn item keeps a bag row.
            Assert.Equal(before, DumpAll(database));
            BagGridMigrationAsserts.AssertMigrated(slotsBefore, BagGridMigrationAsserts.ReadSlots(database));

            // The clock resumes in the same phase at the same tick, and the wolf is still there.
            using DedicatedServerHost host = ServerBootstrap.CreateAuthoritativeHost(bundle);
            host.Runtime!.WriteCheckpointOnDispose = false;
            Assert.Equal(oldTime, host.Runtime.WorldClock.WorldTicks);
            Assert.Equal(phaseBefore, host.Runtime.WorldClock.Phase);
            Assert.Equal(56, host.Runtime.Map.Width);
            Assert.NotEmpty(host.Runtime.Enemies.ActiveActors);
            if (origin == "v2")
            {
                GeometryMaterialId? at = host.Runtime.Map.TryGetCubeAtCell(30, 1, 40)?.MaterialId;
                Assert.True(
                    at == GeometryMaterialId.Lamp,
                    $"(30,1,40)={at}; (26,1,33)={host.Runtime.Map.TryGetCubeAtCell(26, 1, 33)?.MaterialId}; (23,1,49)={host.Runtime.Map.TryGetCubeAtCell(23, 1, 49)?.MaterialId}");
            }
        }

        MigrationTestDatabase.ReleasePooledConnections();

        // Play: a new session places a lamp, which is saved and restored by the next start.
        OpenTK.Mathematics.Vector3 lampCell;
        using (ServerPersistenceBundle bundle = ServerBootstrap.CreatePersistenceBundle(database.DatabasePath, MigrationTestDatabase.InitScriptPath))
        {
            await using WireLoopbackServer server = await WireLoopbackServer.StartAsync(ServerBootstrap.CreateAuthoritativeHost(bundle));
            await using WireLoopbackClient client = await server.ConnectAndJoinAsync(Session);
            lampCell = await CrossWaveT2PersistenceLoopbackTests.PlaceLampAsync(server, client);
            await server.DisconnectAsync(client);
            for (int i = 0; i < 2; i++)
            {
                await server.TickAsync();
                Assert.True(await server.FlushAllAsync());
            }

            Assert.True(server.Runtime.PersistenceWrites?.Flush(TimeSpan.FromSeconds(30)) ?? true);
        }

        MigrationTestDatabase.ReleasePooledConnections();
        Assert.Equal("ok", database.Scalar("PRAGMA integrity_check;"));
        AssertObjectIdCounterAndTriggers(database);
        Assert.Equal(lampsBefore + 1, database.Scalar("SELECT COUNT(*) FROM placed_cube_instances WHERE material_id = 'Lamp' AND world_id = 1;"));
        Assert.Equal(1L, database.Scalar("SELECT COUNT(*) FROM worlds;"));
        // The two torches of the real players are still theirs; the new player has her own (starter kit).
        Assert.Equal(origin == "v0" ? 0L : 2L, database.Scalar("SELECT COUNT(*) FROM item_instances WHERE item_kind = 'tool.torch' AND owner_player_name LIKE 'codex-client-%' AND world_id = 1;"));
        Assert.Equal(1L, database.Scalar("SELECT COUNT(*) FROM item_instances WHERE item_kind = 'tool.torch' AND owner_player_name = 'Name " + Session + "' AND world_id = 1;"));
        Assert.Equal(
            (long)ToolCatalog.CurrentStarterKitVersion,
            database.Scalar("SELECT kit_version FROM player_starter_grants WHERE player_name = 'Name " + Session + "' AND world_id = 1;"));
        // The real players got a grant row too (v2: kept as inserted; v0/v1: written when ensured is not required here).
        if (origin == "v2")
        {
            Assert.Equal(2L, database.Scalar("SELECT COUNT(*) FROM player_starter_grants WHERE player_name LIKE 'codex-client-%' AND world_id = 1;"));
        }

        using (ServerPersistenceBundle again = ServerBootstrap.CreatePersistenceBundle(database.DatabasePath, MigrationTestDatabase.InitScriptPath))
        using (DedicatedServerHost host = ServerBootstrap.CreateAuthoritativeHost(again))
        {
            host.Runtime!.WriteCheckpointOnDispose = false;
            Assert.Equal(
                GeometryMaterialId.Lamp,
                host.Runtime.Map.TryGetCubeAtCell((int)lampCell.X, (int)lampCell.Y, (int)lampCell.Z)?.MaterialId);
            Assert.True(host.Runtime.WorldClock.WorldTicks >= oldTime);
        }

        MigrationTestDatabase.ReleasePooledConnections();
        Assert.Single(database.Backups());
    }

    // worlds.next_object_id is above every object id of every kind, and the four triggers that keep it
    // there survived the migration.
    private static void AssertObjectIdCounterAndTriggers(MigrationTestDatabase database)
    {
        Assert.Equal(
            1L,
            database.Scalar("""
                SELECT CASE WHEN w.next_object_id > COALESCE(MAX(m.id), 0) THEN 1 ELSE 0 END
                FROM worlds w, (
                    SELECT object_id AS id FROM placed_cube_instances WHERE world_id = 1
                    UNION ALL SELECT object_id FROM cube_fracture_pieces WHERE world_id = 1
                    UNION ALL SELECT object_id FROM enemy_instances WHERE world_id = 1
                    UNION ALL SELECT item_instance_id FROM item_instances WHERE world_id = 1
                ) m
                WHERE w.world_id = 1;
                """));
        Assert.Equal(
            5L,
            database.Scalar("SELECT COUNT(*) FROM sqlite_master WHERE type = 'trigger' AND name LIKE '%advance_object_id';"));
    }

    private static IReadOnlyList<ISchemaMigration> MigrationSet() => SchemaMigrations.LoadFromDirectory(MigrationTestDatabase.MigrationsDirectory);

    // Every row of the seven per-world tables without world_id, numbers normalised (REAL cells became
    // INTEGER), keyed by the columns the pre-migration table had, in primary-key order.
    private static readonly HashSet<string> RenumberedColumns =
        ["cube_id", "source_cube_id", "enemy_id", "object_id", "origin_object_id"];

    private static string DumpAll(MigrationTestDatabase database)
    {
        StringBuilder text = new();
        using SqliteConnection connection = database.Open();
        // player_inventory_slots is not dumped: migration 0010 changes the meaning of slot_index (see
        // BagGridMigrationAsserts).
        foreach (string table in WorldTables.Concat(["player_profiles", "combat_formulas"]).Where(name => name != "player_inventory_slots"))
        {
            List<string> columns = [];
            using (SqliteCommand pragma = connection.CreateCommand())
            {
                pragma.CommandText = $"PRAGMA table_info({table});";
                using SqliteDataReader reader = pragma.ExecuteReader();
                while (reader.Read())
                {
                    string name = reader.GetString(1);
                    // Migration 0013 sets every current_energy high (QuickbarManaMigrationTests pins it).
                    if (name == "current_energy")
                    {
                        continue;
                    }

                    // Migration 0005 renumbers the ids of cubes, rubble and enemies, so they are not compared.
                    if (name != "world_id" && name != "account_id" && name != "rolled_modifiers" && name != "camp_key" && !RenumberedColumns.Contains(name))
                    {
                        columns.Add(name);
                    }
                }
            }

            if (columns.Count == 0)
            {
                continue; // table does not exist yet (starter grants before v2)
            }

            StringBuilder tableText = new();
            using SqliteCommand command = connection.CreateCommand();
            // Migration 0009 seeds four enemy and armor formulas and 0011 lowers the default experience
            // curve; the other legacy rows must not change.
            string filter = table == "combat_formulas"
                ? " WHERE formula_key NOT IN ('enemy.attack_damage', 'enemy.max_health', 'enemy.attack_interval_seconds', 'player.damage_taken_factor', 'player.experience_to_next_level')"
                : string.Empty;
            command.CommandText = $"SELECT {string.Join(", ", columns)} FROM {table}{filter} ORDER BY {string.Join(", ", columns)};";
            using SqliteDataReader rows = command.ExecuteReader();
            while (rows.Read())
            {
                for (int i = 0; i < rows.FieldCount; i++)
                {
                    object value = rows.GetValue(i);
                    tableText.Append(value switch
                    {
                        long l => ((double)l).ToString("R", CultureInfo.InvariantCulture),
                        double d => d.ToString("R", CultureInfo.InvariantCulture),
                        DBNull => "<null>",
                        _ => Convert.ToString(value, CultureInfo.InvariantCulture)
                    }).Append('|');
                }

                tableText.Append('\n');
            }

            if (tableText.Length > 0)
            {
                text.Append("== ").Append(table).Append('\n').Append(tableText);
            }
        }

        return text.ToString();
    }
}
