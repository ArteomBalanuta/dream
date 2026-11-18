using Microsoft.Data.Sqlite;
using OpenTK.Mathematics;
using Xunit;
using Xunit.Abstractions;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Server;
using Dream.Server.Persistence;
using Dream.Server.Persistence.Migrations;
using Dream.Simulation.Inventory;
using Dream.Simulation.Players;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using Dream.Tests.Integration;

namespace Dream.Tests.Server.Persistence.Migrations;

// Tester, persistence wave A x wave T2: a world written by a pre-migration server (the frozen
// init.sql schema), holding a player, a placed cube and a pre-T2 inventory, is opened by the
// production bootstrap. The migration runs with a backup, the old rows load, the player receives
// the torch, and a lamp placed over the wire is saved and survives a restart. A database stamped
// with a future schema version is refused before anything is written.
public sealed class LegacyDatabaseServerStartupTests(ITestOutputHelper output)
{
    private const string Session = "legacy";
    private const string PlayerName = "Name legacy";
    private static readonly string[] PreT2Starter = ["tool.builder-hammer", "tool.field-blade", "tool.stone-maul", "tool.survey-lantern"];

    [Fact]
    public async Task PreMigrationWorld_Migrates_WithABackup_ThenALampIsPlacedAndSaved()
    {
        using MigrationTestDatabase database = new();
        database.BuildWithPreMigrationBootstrapper();
        FillLegacyRows(database);
        Assert.Equal(0L, database.Scalar("SELECT COUNT(*) FROM sqlite_master WHERE name = 'schema_version';"));

        List<string> log = [];
        ServerPersistenceBundle bundle = ServerBootstrap.CreatePersistenceBundle(database.DatabasePath, MigrationTestDatabase.InitScriptPath, log: log.Add);
        foreach (string line in log)
        {
            output.WriteLine(line);
        }

        string backup = Assert.Single(database.Backups());
        Assert.Matches(@"persistence\.sqlite\.v0000-to-v0017\.\d{8}T\d{6}Z\.bak$", backup);
        Assert.Equal((long)MigrationTestDatabase.ShippedLatestVersion, database.Scalar("SELECT MAX(version) FROM schema_version;"));
        Assert.Equal(4242UL, bundle.SimulationSeed);
        Assert.Equal(1234L, bundle.WorldTimeTicks);

        Vector3 lampCell;
        DedicatedServerHost host = ServerBootstrap.CreateAuthoritativeHost(bundle);
        host.Runtime!.ConfigureWorldClock(WorldClockSettings.Default, bundle.WorldTimeTicks);
        await using (WireLoopbackServer server = await WireLoopbackServer.StartAsync(host))
        {
            Assert.Equal(GeometryMaterialId.Stone, server.Runtime.Map.TryGetCubeAtCell(20, 1, 20)?.MaterialId);
            await using WireLoopbackClient client = await server.ConnectAndJoinAsync(Session);
            PlayerInventorySnapshot inventory = server.Runtime.TryCaptureInventorySnapshot(new PlayerSessionId(Session))!.Value;
            // The hammer is worn (migration 0010 dropped its bag row); the blade, maul and lantern sit at the
            // anchors the migration gave them (old slots 1, 2, 3 -> 2, 4, 6) and the torch granted by the
            // top-up took the first fit, the hammer's block at anchor 0. Kits v3 and v4 also top up four potions.
            bool IsPotion(InventorySlotSnapshot slot) => slot.Item?.ItemKind.StartsWith("consumable.", StringComparison.Ordinal) == true
                || slot.Item?.ItemKind.StartsWith("potion.", StringComparison.Ordinal) == true;
            Assert.Equal(4, inventory.Slots.Count(slot => slot.Item is not null && !IsPotion(slot)));
            Assert.Equal(4, inventory.Slots.Count(IsPotion));
            Assert.Equal(0, Assert.Single(inventory.Slots, slot => slot.Item?.ItemKind == "tool.torch").SlotIndex);
            Assert.Equal([0, 2, 4, 6], inventory.Slots.Where(slot => !IsPotion(slot)).Select(slot => slot.SlotIndex).Order());
            Assert.Equal("tool.builder-hammer", inventory.EquippedSlot?.Item?.ItemKind);
            Assert.Equal(new Vector3(30.5f, 1.5f, 30.5f), Assert.Single(server.Runtime.CaptureActorSnapshots(), actor => actor.SessionId == Session).Position);

            lampCell = await CrossWaveT2PersistenceLoopbackTests.PlaceLampAsync(server, client);
            await server.DisconnectAsync(client);
            await TickAsync(server, 2);
        }

        SqlitePools.Release(database.DatabasePath);
        Assert.Equal(1L, database.Scalar("SELECT COUNT(*) FROM placed_cube_instances WHERE material_id = 'Lamp';"));
        Assert.Equal(1L, database.Scalar("SELECT COUNT(*) FROM placed_cube_instances WHERE cell_x = 20 AND cell_z = 20 AND material_id = 'Stone';"));
        Assert.Equal(1L, database.Scalar("SELECT COUNT(*) FROM item_instances WHERE item_kind = 'tool.torch';"));
        // The pre-T2 player is recorded as having received kit v2, so the torch is granted once.
        Assert.Equal((long)ToolCatalog.CurrentStarterKitVersion, database.Scalar($"SELECT kit_version FROM player_starter_grants WHERE player_name = '{PlayerName}';"));
        Assert.Equal("ok", database.Scalar("PRAGMA integrity_check;"));

        // A second start is a no-op migration (no new backup) and the lamp loads.
        using (ServerPersistenceBundle reopened = ServerBootstrap.CreatePersistenceBundle(database.DatabasePath, MigrationTestDatabase.InitScriptPath))
        using (DedicatedServerHost again = ServerBootstrap.CreateAuthoritativeHost(reopened))
        {
            again.Runtime!.WriteCheckpointOnDispose = false;
            Assert.Equal(GeometryMaterialId.Lamp, again.Runtime.Map.TryGetCubeAtCell((int)lampCell.X, (int)lampCell.Y, (int)lampCell.Z)?.MaterialId);
        }

        SqlitePools.Release(database.DatabasePath);
        Assert.Single(database.Backups());
    }

    [Fact]
    public void FutureSchemaVersion_IsRefusedByTheBootstrap_AndTheFileIsUntouched()
    {
        using MigrationTestDatabase database = new();
        database.BuildWithPreMigrationBootstrapper();
        FillLegacyRows(database);
        database.Schema.EnsureSchema();
        SqlitePools.Release(database.DatabasePath);
        Assert.Single(database.Backups());
        database.ExecuteScript("INSERT INTO schema_version (version, name, applied_utc) VALUES (99, 'from_the_future', '2030-01-01T00:00:00.0000000Z');");
        SqlitePools.Release(database.DatabasePath);
        string[] filesBefore = Directory.GetFiles(database.RootDirectory).Where(f => !f.EndsWith(".lock", StringComparison.Ordinal)).Order().ToArray();
        byte[] bytesBefore = File.ReadAllBytes(database.DatabasePath);

        List<string> log = [];
        SchemaTooNewException error = Assert.Throws<SchemaTooNewException>(() =>
            ServerBootstrap.CreatePersistenceBundle(database.DatabasePath, MigrationTestDatabase.InitScriptPath, log: log.Add));
        SqlitePools.Release(database.DatabasePath);

        output.WriteLine(error.Message);
        Assert.Equal(99, error.StoredVersion);
        Assert.Contains("99", error.Message, StringComparison.Ordinal);
        // The content digest comes before the database is touched; nothing else may be logged.
        Assert.Empty(log.Where(line => !line.StartsWith("server.content.digest ", StringComparison.Ordinal)));
        Assert.Equal(bytesBefore, File.ReadAllBytes(database.DatabasePath));
        Assert.Equal(filesBefore, Directory.GetFiles(database.RootDirectory).Where(f => !f.EndsWith(".lock", StringComparison.Ordinal)).Order().ToArray());
    }

    // A player, a stone cube at (20, 1, 20), the pre-T2 starter inventory (hammer equipped), a
    // world-state row and the world metadata, as a pre-migration server wrote them.
    private static void FillLegacyRows(MigrationTestDatabase database)
    {
        database.ExecuteScript($"""
            INSERT INTO player_profiles (player_name, display_name, interaction_mode, strength, agility, vitality, energy,
                movement_speed, attack_speed, level, experience, current_hp, max_hp, created_utc, updated_utc)
                VALUES ('{PlayerName}', '{PlayerName}', 'Building', 10, 10, 10, 10, 5.0, 1.0, 1, 0, 100, 100,
                        '2026-01-02T03:04:05.123Z', '2026-01-02T03:04:06.123Z');
            INSERT INTO player_world_state (player_name, position_x, position_y, position_z, facing_x, facing_y, facing_z, current_hp, current_energy, updated_utc)
                VALUES ('{PlayerName}', 30.5, 1.5, 30.5, 0.0, 0.0, 1.0, 100, 10, '2026-01-02T03:04:07.123Z');
            INSERT INTO placed_cube_instances (cube_id, owner_player_name, cell_x, cell_y, cell_z, center_x, center_y, center_z,
                shape_kind, material_id, current_health, max_health)
                VALUES (7, '{PlayerName}', 20.0, 1.0, 20.0, 20.5, 1.5, 20.5, 'SolidCube', 'Stone', 5, 5);
            INSERT INTO world_metadata (key, value) VALUES ('simulation_seed', '4242'), ('world_time_ticks', '1234');
            """);
        using SqliteConnection connection = database.Open();
        for (int i = 0; i < PreT2Starter.Length; i++)
        {
            ToolDefinition tool = ToolCatalog.Resolve(PreT2Starter[i]);
            using SqliteCommand item = connection.CreateCommand();
            item.CommandText = """
                INSERT INTO item_instances (item_instance_id, item_kind, display_name, visual_id, owner_player_name, state, stack_count)
                VALUES ($id, $kind, $name, $visual, $owner, 'Inventory', 1);
                INSERT INTO player_inventory_slots (player_name, slot_index, item_instance_id, updated_utc)
                VALUES ($owner, $slot, $id, '2026-01-02T03:04:08.000Z');
                """;
            item.Parameters.AddWithValue("$id", 11 + i);
            item.Parameters.AddWithValue("$kind", tool.ItemKind);
            item.Parameters.AddWithValue("$name", tool.DisplayName);
            item.Parameters.AddWithValue("$visual", tool.VisualId);
            item.Parameters.AddWithValue("$owner", PlayerName);
            item.Parameters.AddWithValue("$slot", i);
            item.ExecuteNonQuery();
        }

        MigrationTestDatabase.Execute(connection, $"""
            INSERT INTO player_inventory_slots (player_name, slot_index, item_instance_id, updated_utc)
            VALUES ('{PlayerName}', {InventoryConstants.EquippedSlotIndex}, 11, '2026-01-02T03:04:08.000Z');
            """);
    }

    private static async Task TickAsync(WireLoopbackServer server, int ticks)
    {
        for (int i = 0; i < ticks; i++)
        {
            await server.TickAsync();
            Assert.True(await server.FlushAllAsync());
        }
    }
}
