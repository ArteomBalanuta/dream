using System.Text;
using Dream.Server;
using Dream.Server.Persistence;
using Dream.Server.Persistence.Migrations;
using Dream.Simulation.Inventory;
using Dream.Simulation.Persistence;
using Dream.Simulation.World;
using Dream.Tests.Support;
using Microsoft.Data.Sqlite;
using Xunit;
using Xunit.Abstractions;

namespace Dream.Tests.Server.Persistence.Migrations;

// Classic HUD iteration 2: migration 0010 gives player_inventory_slots.slot_index its new meaning (the bag
// is an 8x8 board; a worn item has no bag row). It is data only. Every item id and every worn row survives,
// no worn item keeps a bag row, no anchor falls outside 0..63, a hand-edited row in 16..99 is parked, the
// file stays sound and a backup is taken; then the server's load-time repair places what the remap could
// only give a legal, distinct index.
public sealed class BagGridMigrationTests(ITestOutputHelper output)
{
    private const string Player = "p1";

    private static SqliteSchemaMigrator UpTo(int version) =>
        new([.. SchemaMigrations.LoadFromDirectory(MigrationTestDatabase.MigrationsDirectory).Take(version)]);

    // A v9 database with one character and the given item rows (id, kind) and slot rows (slot, item id).
    private static MigrationTestDatabase V9(IEnumerable<(long Id, string Kind)> items, IEnumerable<(int Slot, long Id)> slots) =>
        AtVersion(9, items, slots);

    // The same at an older schema version (6 is the legacy-e databases'): the slot table has had the same columns since 3.
    private static MigrationTestDatabase AtVersion(int version, IEnumerable<(long Id, string Kind)> items, IEnumerable<(int Slot, long Id)> slots)
    {
        MigrationTestDatabase database = new();
        database.Schema.EnsureSchema(UpTo(version));
        StringBuilder sql = new();
        sql.Append($"INSERT INTO player_profiles (player_name, display_name) VALUES ('{Player}', 'P1');");
        sql.Append($"INSERT INTO player_starter_grants (world_id, player_name, kit_version, updated_utc) VALUES (1, '{Player}', 2, '2026-01-01T00:00:00.000Z');");
        foreach ((long id, string kind) in items)
        {
            sql.Append(
                "INSERT INTO item_instances (world_id, item_instance_id, item_kind, display_name, visual_id, owner_player_name, state, stack_count) " +
                $"VALUES (1, {id}, '{kind}', '{kind}', 'v', '{Player}', 'Inventory', 1);");
        }

        foreach ((int slot, long id) in slots)
        {
            sql.Append($"INSERT INTO player_inventory_slots (world_id, player_name, slot_index, item_instance_id) VALUES (1, '{Player}', {slot}, {id});");
        }

        database.ExecuteScript(sql.ToString());
        return database;
    }

    private static PlayerInventoryService OpenService(ServerPersistenceBundle bundle, PersistenceWriteQueue writes) =>
        new(bundle.World, writes, new ObjectIdAllocator(bundle.World.LoadNextObjectId()));

    private static void AssertSound(MigrationTestDatabase database)
    {
        SqliteConnection.ClearAllPools();
        Assert.Equal("ok", database.Scalar("PRAGMA integrity_check;"));
        using SqliteConnection connection = database.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_key_check;";
        using SqliteDataReader reader = command.ExecuteReader();
        Assert.False(reader.Read(), "foreign_key_check must be clean");
        Assert.Equal(11L, database.Scalar("SELECT MAX(version) FROM schema_version;"));
    }

    [Fact]
    public void SixteenTwoByThreeItems_MigrateWithEveryRow_AndLoadAsEightPlacedAndEightHeldBack()
    {
        (long, string)[] items = [.. Enumerable.Range(0, 16).Select(i => ((long)(100 + i), "gear.iron-blade"))];
        (int, long)[] slots = [.. Enumerable.Range(0, 16).Select(i => (i, (long)(100 + i)))];
        using MigrationTestDatabase database = V9(items, slots);
        List<SlotRow> before = BagGridMigrationAsserts.ReadSlots(database);
        Assert.Equal(16, before.Count);

        using ServerPersistenceBundle bundle = ServerBootstrap.CreatePersistenceBundle(database.DatabasePath, MigrationTestDatabase.InitScriptPath);
        string backup = Assert.Single(database.Backups());
        Assert.Contains("v0009-to-v0011", backup, StringComparison.Ordinal);
        AssertSound(database);
        List<SlotRow> migrated = BagGridMigrationAsserts.ReadSlots(database);
        BagGridMigrationAsserts.AssertMigrated(before, migrated);
        // Old slot i is the 2x2 block at (i / 4) * 16 + (i % 4) * 2.
        Assert.Equal(
            [0, 2, 4, 6, 16, 18, 20, 22, 32, 34, 36, 38, 48, 50, 52, 54],
            migrated.Select(row => row.Slot).Order());

        // Before the server loads the inventory the rows overlap (a 2x3 over the block below): information
        // for the head line, not a soft finding.
        using (SqliteConnection connection = database.Open())
        {
            WorldIntegrityReport report = WorldIntegrityCheck.Run(connection, WorldDescriptor.Default);
            Assert.Equal(8, report.OverlappingBagItems);
            Assert.False(report.HasSoftFindings);
            Assert.False(report.IsFatal);
        }

        using PersistenceWriteQueue writes = PersistenceWriteQueue.Inline();
        PlayerInventoryService inventory = OpenService(bundle, writes);
        Assert.True(inventory.Ensure(Player));
        Assert.True(writes.Flush(TimeSpan.FromSeconds(30)));
        Assert.Equal(0, writes.FailedWrites);

        PlayerInventorySnapshot snapshot = inventory.BuildSnapshot(Player);
        Assert.Equal([0, 2, 4, 6, 32, 34, 36, 38], snapshot.BagItems().Select(slot => slot.SlotIndex));
        Assert.Equal(8, snapshot.HeldBackItems().Count());
        // Every item id survived into the rows, once.
        List<SlotRow> repaired = BagGridMigrationAsserts.ReadSlots(database);
        Assert.Equal(Enumerable.Range(100, 16).Select(id => (long)id), repaired.Select(row => row.ItemId).Order());
        Assert.Equal(16, repaired.Count);
        Assert.Equal(snapshot.Layout(), BuildFresh(bundle).Layout());

        using (SqliteConnection connection = database.Open())
        {
            WorldIntegrityReport after = WorldIntegrityCheck.Run(connection, WorldDescriptor.Default);
            Assert.Equal(0, after.OverlappingBagItems);
            Assert.Equal(8, after.HeldBackItems);
            Assert.False(after.HasSoftFindings);
            Assert.Equal(0, after.UnknownSlotIndexes);
        }

        output.WriteLine("16 x 2x3: 8 placed, 8 held back, all 16 ids kept; held back drain as room appears (BagLayoutRepairTests)");
    }

    private static PlayerInventorySnapshot BuildFresh(ServerPersistenceBundle bundle)
    {
        using PersistenceWriteQueue writes = PersistenceWriteQueue.Inline();
        PlayerInventoryService fresh = OpenService(bundle, writes);
        Assert.True(fresh.Ensure(Player));
        return fresh.BuildSnapshot(Player);
    }

    [Fact]
    public void AHandEditedRowIn16To99_IsParkedAtPlus20000_AndItsItemSurvives()
    {
        using MigrationTestDatabase database = V9(
            [(1, "gear.iron-band"), (2, "gear.iron-band"), (3, "gear.iron-band"), (4, "gear.iron-band")],
            [(3, 1), (16, 2), (50, 3), (99, 4)]);
        List<SlotRow> before = BagGridMigrationAsserts.ReadSlots(database);

        using ServerPersistenceBundle bundle = ServerBootstrap.CreatePersistenceBundle(database.DatabasePath, MigrationTestDatabase.InitScriptPath);

        AssertSound(database);
        List<SlotRow> migrated = BagGridMigrationAsserts.ReadSlots(database);
        BagGridMigrationAsserts.AssertMigrated(before, migrated);
        Assert.Equal([6, 20016, 20050, 20099], migrated.Select(row => row.Slot).Order());

        // The server shows only the bag row and keeps the parked ones.
        using PersistenceWriteQueue writes = PersistenceWriteQueue.Inline();
        PlayerInventoryService inventory = OpenService(bundle, writes);
        Assert.True(inventory.Ensure(Player));
        Assert.Equal([6], inventory.BuildSnapshot(Player).BagItems().Select(slot => slot.SlotIndex));
        Assert.True(writes.Flush(TimeSpan.FromSeconds(30)));
        Assert.Equal(4, BagGridMigrationAsserts.ReadSlots(database).Count);
        using SqliteConnection connection = database.Open();
        Assert.Equal(3, WorldIntegrityCheck.Run(connection, WorldDescriptor.Default).UnknownSlotIndexes);
    }

    [Fact]
    public void AWornItemsBagRow_IsDeleted_TheWornRowStays_AndAnItemWornByAnotherPlayerKeepsItsOwnRow()
    {
        // Items 1..4 are worn (rows 100..103) with their bag rows (slots 0..3, the alias); item 5 is only in the
        // bag. Item 6 is worn by p1 and sits in the bag of p2 (hand-edited sharing): p2's row is not an alias.
        using MigrationTestDatabase database = V9(
            [(1, "tool.builder-hammer"), (2, "tool.field-blade"), (3, "gear.leather-cap"), (4, "gear.leather-vest"), (5, "tool.torch"), (6, "gear.iron-band")],
            [(0, 1), (1, 2), (2, 3), (3, 4), (4, 5), (100, 1), (101, 2), (102, 3), (103, 4), (104, 6)]);
        database.ExecuteScript("INSERT INTO player_profiles (player_name, display_name) VALUES ('p2', 'P2'); INSERT INTO player_inventory_slots (world_id, player_name, slot_index, item_instance_id) VALUES (1, 'p2', 0, 6);");
        List<SlotRow> before = BagGridMigrationAsserts.ReadSlots(database);

        using ServerPersistenceBundle bundle = ServerBootstrap.CreatePersistenceBundle(database.DatabasePath, MigrationTestDatabase.InitScriptPath);

        AssertSound(database);
        List<SlotRow> migrated = BagGridMigrationAsserts.ReadSlots(database);
        BagGridMigrationAsserts.AssertMigrated(before, migrated);
        Assert.Equal([100, 101, 102, 103, 104], migrated.Where(row => row.Player == Player && row.Slot >= 100).Select(row => row.Slot).Order());
        // p1's item 5 (old slot 4) is at the second block row's first column; p2's own row for item 6 stays at anchor 0.
        Assert.Equal([16], migrated.Where(row => row.Player == Player && row.Slot < 100).Select(row => row.Slot));
        Assert.Equal([0], migrated.Where(row => row.Player == "p2").Select(row => row.Slot));
    }

    // The legacy-e databases are schema v6: all four later migrations (accounts, item rolls, enemy formulas, this
    // one) run in one step with one backup, and the slot rows come out the same as from a v9 file.
    [Fact]
    public void AV6Database_MigratesInOneStep_WithOneBackup_AndTheSameGuarantees()
    {
        using MigrationTestDatabase database = AtVersion(
            6,
            [(1, "tool.builder-hammer"), (2, "tool.field-blade"), (3, "tool.stone-maul"), (4, "tool.survey-lantern"), (5, "tool.torch"), (6, "gear.iron-band")],
            [(0, 1), (1, 2), (2, 3), (3, 4), (4, 5), (100, 1), (16, 6)]);
        List<SlotRow> before = BagGridMigrationAsserts.ReadSlots(database);
        Assert.Equal(6L, database.Scalar("SELECT MAX(version) FROM schema_version;"));

        using ServerPersistenceBundle bundle = ServerBootstrap.CreatePersistenceBundle(database.DatabasePath, MigrationTestDatabase.InitScriptPath);

        Assert.Contains("v0006-to-v0011", Assert.Single(database.Backups()), StringComparison.Ordinal);
        AssertSound(database);
        List<SlotRow> migrated = BagGridMigrationAsserts.ReadSlots(database);
        BagGridMigrationAsserts.AssertMigrated(before, migrated);
        // The hammer's alias (slot 0) is gone, its worn row stays; the others are at their blocks; 16 is parked.
        Assert.Equal([2, 4, 6, 16, 100, 20016], migrated.Select(row => row.Slot).Order());
        Assert.Equal(6L, database.Scalar("SELECT COUNT(DISTINCT item_instance_id) FROM player_inventory_slots;"));
    }

    [Fact]
    public void TheMigration_IsDataOnly_TheSchemaIsTheV9One()
    {
        using MigrationTestDatabase database = V9([(1, "gear.iron-band")], [(0, 1), (100, 1)]);
        IReadOnlyList<string> v9;
        using (SqliteConnection connection = database.Open())
        {
            v9 = SqliteSchemaSnapshot.Capture(connection);
        }

        SqliteConnection.ClearAllPools();
        SchemaMigrationResult result = database.Schema.EnsureSchema();

        Assert.Equal(new SchemaMigrationResult(9, 11, result.BackupPath), result);
        SqliteConnection.ClearAllPools();
        using SqliteConnection after = database.Open();
        // Migration 0011 then appends player_profiles.stat_points; every other table is the v9 one.
        static bool NotProfiles(string line) =>
            !line.StartsWith("master table player_profiles on ", StringComparison.Ordinal)
            && !line.StartsWith("column player_profiles ", StringComparison.Ordinal);
        Assert.Equal(v9.Where(NotProfiles), SqliteSchemaSnapshot.Capture(after).Where(NotProfiles));
        Assert.Equal(1L, database.Scalar("SELECT COUNT(*) FROM pragma_table_info('player_profiles') WHERE name = 'stat_points';"));
        Assert.Equal(1L, database.Scalar("SELECT COUNT(*) FROM item_instances;"));
        Assert.Equal(5L, database.Scalar("SELECT COUNT(*) FROM sqlite_master WHERE type = 'trigger' AND name LIKE '%advance_object_id';"));
    }

    [Fact]
    public void ARowAlreadyInTheTemporaryRange_FailsTheMigrationLoudly_RollsBack_AndKeepsTheBackup()
    {
        // No build ever wrote 10005, so this is hand-made: the first remap step would move bag slot 5 onto it.
        using MigrationTestDatabase database = V9(
            [(1, "gear.iron-band"), (2, "gear.iron-band")],
            [(5, 1), (10005, 2)]);
        List<SlotRow> before = BagGridMigrationAsserts.ReadSlots(database);

        Exception error = Assert.ThrowsAny<Exception>(() => database.Schema.EnsureSchema());

        output.WriteLine(error.Message);
        SqliteConnection.ClearAllPools();
        Assert.Equal(9L, database.Scalar("SELECT MAX(version) FROM schema_version;"));
        Assert.Equal(before, BagGridMigrationAsserts.ReadSlots(database));
        Assert.Single(database.Backups());
        Assert.Equal("ok", database.Scalar("PRAGMA integrity_check;"));
    }

    [Fact]
    public void ANewDatabase_NeedsNoRemap_AndHasNoSlotRows()
    {
        using MigrationTestDatabase database = new();

        database.Schema.EnsureSchema();

        Assert.Equal(0L, database.Scalar("SELECT COUNT(*) FROM player_inventory_slots;"));
        Assert.Empty(database.Backups());
        Assert.Equal(11L, database.Scalar("SELECT MAX(version) FROM schema_version;"));
    }
}
