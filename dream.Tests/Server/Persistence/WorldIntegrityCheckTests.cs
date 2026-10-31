using Microsoft.Data.Sqlite;
using Xunit;
using Xunit.Abstractions;
using Dream.Server;
using Dream.Server.Persistence;
using Dream.Simulation.Content;
using Dream.Simulation.World;
using Dream.Tests.Server.Persistence.Migrations;

namespace Dream.Tests.Server.Persistence;

// Persistence wave F3: the integrity check a world runs when it loads. Foreign-key violations and
// corruption stop the start (exit code 6); everything else is counted and logged.
public sealed class WorldIntegrityCheckTests(ITestOutputHelper output)
{
    private const string Profile = "INSERT INTO player_profiles (player_name, display_name) VALUES ('p1', 'P1');";

    private static string Item(long id, string kind) =>
        "INSERT INTO item_instances (world_id, item_instance_id, item_kind, display_name, visual_id, owner_player_name, state, stack_count) " +
        $"VALUES (1, {id}, '{kind}', 'Item', 'v', 'p1', 'Owned', 1);";

    private static string Slot(int slot, long item) =>
        $"INSERT INTO player_inventory_slots (world_id, player_name, slot_index, item_instance_id) VALUES (1, 'p1', {slot}, {item});";

    private static MigrationTestDatabase Migrated()
    {
        MigrationTestDatabase db = new();
        db.Schema.EnsureSchema();
        SqliteConnection.ClearAllPools();
        return db;
    }

    private static WorldIntegrityReport Check(MigrationTestDatabase db, WorldIntegrityOptions? options = null)
    {
        using SqliteConnection connection = db.Schema.CreateOpenConnection();
        WorldDescriptor descriptor = WorldDescriptor.Default;
        return WorldIntegrityCheck.Run(connection, descriptor, options);
    }

    [Fact]
    public void ACleanDatabase_HasNoFindings()
    {
        using MigrationTestDatabase db = Migrated();
        WorldIntegrityReport report = Check(db);
        Assert.False(report.IsFatal);
        Assert.False(report.HasSoftFindings);
        Assert.Contains("no findings", Assert.Single(report.ToLogLines(WorldDescriptor.Default)), StringComparison.Ordinal);
    }

    [Fact]
    public void AForeignKeyViolation_IsFatal_AndTheServerExitsWithCode6()
    {
        using MigrationTestDatabase db = Migrated();
        db.ExecuteScript(
            "PRAGMA foreign_keys = OFF; " +
            "INSERT INTO player_inventory_slots (world_id, player_name, slot_index, item_instance_id) VALUES (1, 'ghost', 0, 999);");

        WorldIntegrityReport report = Check(db);
        Assert.True(report.IsFatal);
        Assert.True(report.ForeignKeyViolations >= 1);

        List<string> log = [];
        using Dream.Server.DedicatedServerHost? host = ServerBootstrap.TryCreateServerModeHost(
            () => ServerBootstrap.CreateAuthoritativeHost(
                ServerBootstrap.CreatePersistenceBundle(db.DatabasePath, MigrationTestDatabase.InitScriptPath)),
            log.Add,
            out int exitCode);
        Assert.Null(host);
        Assert.Equal(ServerBootstrap.DatabaseIntegrityExitCode, exitCode);
        Assert.Equal(6, exitCode);
        Assert.Contains(log, l => l.Contains("foreign-key violation", StringComparison.Ordinal));
        // The refusal released the world.
        Assert.False(Dream.Server.Hosting.WorldHostLease.IsHeld(db.DatabasePath, WorldId.Default));
    }

    [Fact]
    public void ADamagedFile_IsReportedAsCorruption()
    {
        using MigrationTestDatabase db = Migrated();
        db.ExecuteScript("PRAGMA wal_checkpoint(TRUNCATE);");
        SqliteConnection.ClearAllPools();
        byte[] bytes = File.ReadAllBytes(db.DatabasePath);
        // Overwrite the middle of the second page onward with noise, leaving the header readable.
        for (int i = 4096 + 100; i < Math.Min(bytes.Length, 4096 * 4); i++)
        {
            bytes[i] = 0xA5;
        }

        File.WriteAllBytes(db.DatabasePath, bytes);
        WorldIntegrityReport report = Check(db, new WorldIntegrityOptions { QuickCheck = true });
        Assert.True(report.IsFatal);
        Assert.NotNull(report.CorruptionMessage);
        output.WriteLine(report.FatalMessage);
    }

    [Fact]
    public void AFileThatIsNotADatabase_StopsTheServerWithCode6()
    {
        using MigrationTestDatabase db = new();
        File.WriteAllBytes(db.DatabasePath, Enumerable.Repeat((byte)0x41, 8192).ToArray());

        List<string> log = [];
        using Dream.Server.DedicatedServerHost? host = ServerBootstrap.TryCreateServerModeHost(
            () => ServerBootstrap.CreateAuthoritativeHost(
                ServerBootstrap.CreatePersistenceBundle(db.DatabasePath, MigrationTestDatabase.InitScriptPath)),
            log.Add,
            out int exitCode);

        Assert.Null(host);
        Assert.Equal(ServerBootstrap.DatabaseIntegrityExitCode, exitCode);
        Assert.Contains(log, l => l.Contains("damaged", StringComparison.Ordinal));
    }

    [Fact]
    public void SoftFindings_AreCounted_NotFatal_AndTheServerStillStarts()
    {
        using MigrationTestDatabase db = Migrated();
        db.ExecuteScript(
            Profile +
            // An equipped slot (100) with no matching backpack slot.
            Item(10, "tool.mystery") + Slot(100, 10) +
            // A cube outside the 56x8x56 world, and one rubble piece outside.
            "INSERT INTO placed_cube_instances (world_id, object_id, cell_x, cell_y, cell_z, material_id, current_health, max_health) VALUES (1, 20, 99, 1, 3, 'stone', 5, 5);" +
            "INSERT INTO enemy_instances (world_id, object_id, enemy_kind, display_name, position_x, position_y, position_z, facing_x, facing_y, facing_z, current_hp, max_hp) " +
            "VALUES (1, 21, 'dragon', 'Dragon', 1, 1, 1, 0, 0, 1, 10, 10);");

        WorldIntegrityOptions strict = new()
        {
            IsKnownItemKind = kind => kind != "tool.mystery",
            IsKnownEnemyKind = kind => kind == "wolf"
        };
        WorldIntegrityReport report = Check(db, strict);

        Assert.False(report.IsFatal);
        Assert.Equal(1, report.ObjectsOutOfBounds);
        Assert.Equal(1, report.EquippedWithoutBackpackItem);
        Assert.Equal(0, report.SlotsMissingItem);
        Assert.Equal(1, report.UnknownItemKinds);
        Assert.Equal(1, report.UnknownEnemyKinds);
        Assert.Contains("tool.mystery", report.UnknownKindNames);
        Assert.Contains("dragon", report.UnknownKindNames);
        string[] lines = report.ToLogLines(WorldDescriptor.Default).ToArray();
        output.WriteLine(string.Join(Environment.NewLine, lines));
        Assert.Contains(lines, l => l.Contains("outside the world's bounds", StringComparison.Ordinal));

        // Accept-all predicates (the default) see no unknown kinds.
        WorldIntegrityReport lenient = Check(db);
        Assert.Equal(0, lenient.UnknownItemKinds);
        Assert.Equal(0, lenient.UnknownEnemyKinds);

        // And the production start goes ahead, logging the counts.
        List<string> log = [];
        using ServerPersistenceBundle bundle = ServerBootstrap.CreatePersistenceBundle(
            db.DatabasePath, MigrationTestDatabase.InitScriptPath, log: log.Add);
        Assert.Contains(log, l => l.Contains("integrity check", StringComparison.Ordinal));
        Assert.Contains(log, l => l.Contains("outside the world's bounds", StringComparison.Ordinal));
    }

    [Fact]
    public void ABackpackItemThatIsEquipped_IsNotAFinding()
    {
        using MigrationTestDatabase db = Migrated();
        db.ExecuteScript(Profile + Item(10, "tool.a") + Slot(3, 10) + Slot(100, 10));
        WorldIntegrityReport report = Check(db);
        Assert.False(report.HasSoftFindings);
        Assert.Equal(0, report.EquippedWithoutBackpackItem);
    }

    [Fact]
    public void ASlotPointingAtAMissingItem_IsCountedAndAlsoAForeignKeyViolation()
    {
        using MigrationTestDatabase db = Migrated();
        db.ExecuteScript("PRAGMA foreign_keys = OFF; " + Profile + Slot(0, 777));
        WorldIntegrityReport report = Check(db);
        Assert.Equal(1, report.SlotsMissingItem);
        Assert.True(report.IsFatal);
    }

    [Fact]
    public void TheKindPredicates_CanBeGivenToOneStart()
    {
        using MigrationTestDatabase db = Migrated();
        db.ExecuteScript(Profile + Item(10, "tool.mystery") + Slot(0, 10));
        List<string> log = [];
        using ServerPersistenceBundle bundle = ServerBootstrap.CreatePersistenceBundle(
            db.DatabasePath,
            MigrationTestDatabase.InitScriptPath,
            log: log.Add,
            integrityOptions: new WorldIntegrityOptions { IsKnownItemKind = _ => false });
        Assert.Contains(log, l => l.Contains("unknown kinds: 1 item", StringComparison.Ordinal));
    }

    // The production default: the shipped content decides which kinds are known.
    [Fact]
    public void AProductionStart_ChecksKindsAgainstTheShippedContent()
    {
        using MigrationTestDatabase db = Migrated();
        string known = ContentCatalog.Shipped.Items[0].Key;
        db.ExecuteScript(
            Profile + Item(10, "tool.mystery") + Item(11, known) +
            "INSERT INTO enemy_instances (world_id, object_id, enemy_kind, display_name, position_x, position_y, position_z, facing_x, facing_y, facing_z, current_hp, max_hp) " +
            $"VALUES (1, 21, 'dragon', 'Dragon', 1, 1, 1, 0, 0, 1, 10, 10), (1, 22, '{ContentCatalog.WolfKey}', 'Wolf', 1, 1, 1, 0, 0, 1, 10, 10);");

        List<string> log = [];
        using ServerPersistenceBundle bundle = ServerBootstrap.CreatePersistenceBundle(
            db.DatabasePath, MigrationTestDatabase.InitScriptPath, log: log.Add);

        string line = Assert.Single(log, l => l.Contains("unknown kinds", StringComparison.Ordinal));
        Assert.Contains("unknown kinds: 1 item row(s), 1 enemy row(s)", line, StringComparison.Ordinal);
        Assert.Contains("tool.mystery", line, StringComparison.Ordinal);
        Assert.Contains("dragon", line, StringComparison.Ordinal);
    }

    // A real database from before worlds existed (real-legacy-v1.db), migrated to the current schema by
    // the production bootstrap. A build that knows no kinds at all and bounds nothing must still open it:
    // soft problems are logged, never refused.
    [Fact]
    public void ARealLegacyDatabase_IsNeverRefusedForSoftFindings()
    {
        using MigrationTestDatabase db = new();
        File.Copy(
            Path.Combine(Dream.Tests.Integration.RepoRoot.Find(), "dream.Tests", "Server", "Persistence", "Migrations", "Fixtures", "real-legacy-v1.db"),
            db.DatabasePath);
        WorldIntegrityOptions knowsNothing = new()
        {
            IsKnownItemKind = _ => false,
            IsKnownEnemyKind = _ => false
        };
        List<string> log = [];
        using (ServerPersistenceBundle bundle = ServerBootstrap.CreatePersistenceBundle(
            db.DatabasePath, MigrationTestDatabase.InitScriptPath, log: log.Add, integrityOptions: knowsNothing))
        {
            Assert.Equal(WorldId.Default, bundle.WorldId);
        }

        foreach (string line in log)
        {
            output.WriteLine(line);
        }

        Assert.Contains(log, l => l.Contains("integrity check", StringComparison.Ordinal));
        WorldIntegrityReport report = Check(db, knowsNothing);
        Assert.False(report.IsFatal);
        output.WriteLine($"legacy soft findings: oob={report.ObjectsOutOfBounds} unknownItems={report.UnknownItemKinds} unknownEnemies={report.UnknownEnemyKinds}");
    }
}
