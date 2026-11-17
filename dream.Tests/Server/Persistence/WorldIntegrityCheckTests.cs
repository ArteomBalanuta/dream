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
        SqlitePools.Release(db.DatabasePath);
        return db;
    }

    // For the tests that seed one item in several slot rows (the finding the check reports): the unique
    // index of migration 0012 would refuse those rows, so it is dropped in their setup.
    private static MigrationTestDatabase MigratedWithSharedRows()
    {
        MigrationTestDatabase db = Migrated();
        db.ExecuteScript("DROP INDEX ux_player_inventory_slots_item;");
        SqlitePools.Release(db.DatabasePath);
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
        SqlitePools.Release(db.DatabasePath);
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
        using MigrationTestDatabase db = MigratedWithSharedRows();
        db.ExecuteScript(
            Profile +
            // A worn item (100) that also still has a bag row.
            Item(10, "tool.mystery") + Slot(3, 10) + Slot(100, 10) +
            // A cube outside the 56x8x56 world, and one rubble piece outside.
            "INSERT INTO placed_cube_instances (world_id, object_id, cell_x, cell_y, cell_z, material_id, current_health, max_health) VALUES (1, 20, 99, 1, 3, 'stone', 5, 5);" +
            "INSERT INTO cube_fracture_pieces (world_id, object_id, origin_object_id, piece_index, parent_cell_x, parent_cell_y, parent_cell_z, center_x, center_y, center_z, side_length, material_id) " +
            "VALUES (1, 22, 20, 0, 99, 1, 3, 99.5, 1.5, 3.5, 0.5, 'stone');" +
            "INSERT INTO enemy_instances (world_id, object_id, enemy_kind, display_name, position_x, position_y, position_z, facing_x, facing_y, facing_z, current_hp, max_hp) " +
            "VALUES (1, 21, 'dragon', 'Dragon', 1, 1, 1, 0, 0, 1, 10, 10);");

        WorldIntegrityOptions strict = new()
        {
            IsKnownItemKind = kind => kind != "tool.mystery",
            IsKnownEnemyKind = kind => kind == "wolf"
        };
        WorldIntegrityReport report = Check(db, strict);

        Assert.False(report.IsFatal);
        // The cube and its rubble piece.
        Assert.Equal(2, report.ObjectsOutOfBounds);
        Assert.Equal(1, report.EquippedWithBagRow);
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

    // Classic HUD iteration 2: a worn item has only its worn row, so a bag row beside it is the finding.
    [Fact]
    public void AWornItemThatStillHasABagRow_IsASoftFinding_NotASharedReference()
    {
        using MigrationTestDatabase db = MigratedWithSharedRows();
        db.ExecuteScript(Profile + Item(10, "tool.a") + Slot(3, 10) + Slot(100, 10));
        WorldIntegrityReport report = Check(db);
        Assert.True(report.HasSoftFindings);
        Assert.False(report.IsFatal);
        Assert.Equal(1, report.EquippedWithBagRow);
        Assert.Equal(0, report.SharedItemReferences);
        Assert.Contains(report.ToLogLines(WorldDescriptor.Default), l => l.Contains("still have a bag row", StringComparison.Ordinal));
    }

    [Fact]
    public void AWornItemWithNoBagRow_IsNotAFinding()
    {
        using MigrationTestDatabase db = Migrated();
        db.ExecuteScript(Profile + Item(10, "tool.a") + Slot(100, 10));
        WorldIntegrityReport report = Check(db);
        Assert.False(report.HasSoftFindings);
        Assert.Equal(0, report.EquippedWithBagRow);
    }

    private const string SecondProfile = "INSERT INTO player_profiles (player_name, display_name) VALUES ('p2', 'P2');";

    private static string SlotOf(string player, int slot, long item) =>
        $"INSERT INTO player_inventory_slots (world_id, player_name, slot_index, item_instance_id) VALUES (1, '{player}', {slot}, {item});";

    [Fact]
    public void OneItemInSeveralBackpackSlots_IsASoftFinding_ThatIsNeverFatal()
    {
        using MigrationTestDatabase db = MigratedWithSharedRows();
        db.ExecuteScript(Profile + Item(10, "tool.a") + Slot(1, 10) + Slot(2, 10) + Slot(3, 10));
        WorldIntegrityReport report = Check(db);
        Assert.Equal(2, report.SharedItemReferences);
        Assert.True(report.HasSoftFindings);
        Assert.False(report.IsFatal);
        Assert.Contains(report.ToLogLines(WorldDescriptor.Default), l => l.Contains("2 extra inventory slot reference", StringComparison.Ordinal));
    }

    [Fact]
    public void OneItemHeldByTwoPlayers_IsASoftFinding_AndAnAliasIsNotCountedTwice()
    {
        using MigrationTestDatabase db = MigratedWithSharedRows();
        db.ExecuteScript(
            Profile + SecondProfile + Item(10, "tool.a") +
            SlotOf("p1", 0, 10) + SlotOf("p1", 100, 10) + SlotOf("p2", 4, 10));
        WorldIntegrityReport report = Check(db);
        // p1's bag row beside its worn row is EquippedWithBagRow; the one reference by p2 is the shared one.
        Assert.Equal(1, report.SharedItemReferences);
        Assert.Equal(1, report.EquippedWithBagRow);
        Assert.False(report.IsFatal);
    }

    [Fact]
    public void WornItemsOfTwoPlayers_AreNotSharedReferences()
    {
        using MigrationTestDatabase db = Migrated();
        db.ExecuteScript(Profile + SecondProfile + Item(10, "tool.a") + Item(11, "tool.b") + Item(12, "tool.c") + Item(13, "tool.d") +
            SlotOf("p1", 3, 10) + SlotOf("p1", 100, 11) + SlotOf("p2", 0, 12) + SlotOf("p2", 100, 13));
        WorldIntegrityReport report = Check(db);
        Assert.Equal(0, report.SharedItemReferences);
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

    [Fact]
    public void SixCharactersOnOneAccount_IsASoftFinding_AndTheServerStillStarts()
    {
        using MigrationTestDatabase db = Migrated();
        db.ExecuteScript(
            "INSERT INTO accounts (account_id, token_hash, created_utc, last_seen_utc) VALUES (1, zeroblob(32), 'x', 'x');" +
            string.Concat(Enumerable.Range(1, 6).Select(i =>
                $"INSERT INTO player_profiles (player_name, display_name, account_id) VALUES ('c{i}', 'C{i}', 1);")));

        WorldIntegrityReport report = Check(db);

        Assert.Equal(1, report.AccountsOverLimit);
        Assert.True(report.HasSoftFindings);
        Assert.False(report.IsFatal);
        Assert.Contains(report.ToLogLines(WorldDescriptor.Default), l => l.Contains("more than 5 characters", StringComparison.Ordinal));
    }

    [Fact]
    public void FiveCharactersOnOneAccount_IsNotAFinding()
    {
        using MigrationTestDatabase db = Migrated();
        db.ExecuteScript(
            "INSERT INTO accounts (account_id, token_hash, created_utc, last_seen_utc) VALUES (1, zeroblob(32), 'x', 'x');" +
            string.Concat(Enumerable.Range(1, 5).Select(i =>
                $"INSERT INTO player_profiles (player_name, display_name, account_id) VALUES ('c{i}', 'C{i}', 1);")));

        Assert.Equal(0, Check(db).AccountsOverLimit);
    }

    [Fact]
    public void ADroppedUniqueIndexWithDuplicateDisplayNames_IsASoftFinding()
    {
        using MigrationTestDatabase db = Migrated();
        db.ExecuteScript(
            "DROP INDEX ux_player_profiles_display_name;" +
            "INSERT INTO player_profiles (player_name, display_name) VALUES ('k1', 'Bob'), ('k2', 'BOB'), ('k3', 'Carol');");

        WorldIntegrityReport report = Check(db);

        Assert.Equal(1, report.DuplicateDisplayNames);
        Assert.True(report.HasSoftFindings);
        Assert.False(report.IsFatal);
    }

    [Fact]
    public void UnownedCharacters_AreCountedInTheHeadLine_ButAreNoSoftFinding()
    {
        using MigrationTestDatabase db = Migrated();
        db.ExecuteScript("INSERT INTO player_profiles (player_name, display_name) VALUES ('k1', 'Bob'), ('k2', 'Carol');");

        WorldIntegrityReport report = Check(db);

        Assert.Equal(2, report.UnownedCharacters);
        Assert.False(report.HasSoftFindings);
        string line = Assert.Single(report.ToLogLines(WorldDescriptor.Default));
        Assert.Contains("2 unowned character(s)", line, StringComparison.Ordinal);
        Assert.Contains("no findings", line, StringComparison.Ordinal);
    }

    [Fact]
    public void ABadAccountId_IsFatalThroughTheForeignKeyCheck()
    {
        using MigrationTestDatabase db = Migrated();
        db.ExecuteScript(
            "PRAGMA foreign_keys = OFF; INSERT INTO player_profiles (player_name, display_name, account_id) VALUES ('k', 'K', 99);");

        WorldIntegrityReport report = Check(db);

        Assert.True(report.IsFatal);
        Assert.True(report.ForeignKeyViolations >= 1);
    }

    // Equipment slots 100..111 (twelve since classic HUD iteration 2).
    [Theory]
    [InlineData(100)]
    [InlineData(101)]
    [InlineData(103)]
    [InlineData(108)]
    [InlineData(111)]
    public void AWornItemWithNoBagRow_IsNotAFinding_InAnyOfTheTwelveSlots(int equipmentSlot)
    {
        using MigrationTestDatabase db = Migrated();
        db.ExecuteScript(Profile + Item(10, "tool.a") + Slot(equipmentSlot, 10));
        WorldIntegrityReport report = Check(db);
        Assert.False(report.HasSoftFindings);
        Assert.Equal(0, report.EquippedWithBagRow);
        Assert.Equal(0, report.SharedItemReferences);
        Assert.Equal(0, report.UnknownSlotIndexes);
    }

    [Fact]
    public void OneItemInTwoEquipmentSlots_IsCountedOnItsOwn()
    {
        using MigrationTestDatabase db = MigratedWithSharedRows();
        db.ExecuteScript(Profile + Item(10, "tool.a") + Slot(100, 10) + Slot(101, 10));
        WorldIntegrityReport report = Check(db);
        Assert.Equal(1, report.ItemsInTwoEquipmentSlots);
        Assert.Equal(0, report.SharedItemReferences);
        Assert.Equal(0, report.UnknownSlotIndexes);
        Assert.True(report.HasSoftFindings);
        Assert.False(report.IsFatal);
        Assert.Contains(report.ToLogLines(WorldDescriptor.Default), l => l.Contains("another equipment slot", StringComparison.Ordinal));
    }

    [Fact]
    public void OneItemInTwoEquipmentSlots_WithNoBackpackRow_IsNotAlsoASharedReference()
    {
        using MigrationTestDatabase db = MigratedWithSharedRows();
        db.ExecuteScript(Profile + Item(10, "tool.a") + Slot(100, 10) + Slot(101, 10));
        WorldIntegrityReport report = Check(db);
        Assert.Equal(1, report.ItemsInTwoEquipmentSlots);
        Assert.Equal(0, report.SharedItemReferences);
    }

    [Theory]
    [InlineData(64)]
    [InlineData(99)]
    [InlineData(112)]
    [InlineData(199)]
    [InlineData(216)]
    [InlineData(300)]
    [InlineData(20016)]
    public void ASlotIndexOutsideTheKnownRanges_IsCountedOnItsOwn(int slotIndex)
    {
        using MigrationTestDatabase db = Migrated();
        db.ExecuteScript(Profile + Item(10, "tool.a") + Slot(slotIndex, 10));
        WorldIntegrityReport report = Check(db);
        Assert.Equal(1, report.UnknownSlotIndexes);
        Assert.Equal(0, report.ItemsInTwoEquipmentSlots);
        Assert.Equal(0, report.EquipmentSlotMismatches);
        Assert.True(report.HasSoftFindings);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(63)]
    [InlineData(100)]
    [InlineData(111)]
    [InlineData(200)]
    [InlineData(215)]
    public void ASlotIndexInAKnownRange_IsNotUnknown(int slotIndex)
    {
        using MigrationTestDatabase db = Migrated();
        db.ExecuteScript(Profile + Item(10, "tool.a") + Slot(slotIndex, 10));
        Assert.Equal(0, Check(db).UnknownSlotIndexes);
    }

    // Information only, like the unowned characters: the server repairs the layout when it loads the
    // inventory, so a freshly migrated database must not read as having soft findings.
    [Fact]
    public void BagRowsThatOverlap_AreCountedInTheHeadLine_ButAreNoSoftFinding()
    {
        using MigrationTestDatabase db = Migrated();
        db.ExecuteScript(
            Profile + Item(10, "gear.iron-blade") + Item(11, "gear.iron-blade") + Item(12, "gear.leather-cap") +
            Item(13, "tool.torch") + Item(14, "gear.iron-blade") +
            // A 2x3 blade at anchor 0 covers cells 0,1,8,9,16,17: the cap at 9 and the blade at 16 are under it.
            Slot(0, 10) + Slot(16, 11) + Slot(9, 12) +
            // A torch (1x2) at the last column fits; a blade (2 wide) at anchor 7 runs off the right edge.
            Slot(23, 13) + Slot(7, 14));
        WorldIntegrityReport report = Check(db, ServerBootstrap.IntegrityOptionsFor(ContentCatalog.Shipped));

        // Anchor 0 is kept; 9 (under it) and 16 (under it) overlap; 7 (a 2x3 at column 7) is off the board.
        Assert.Equal(3, report.OverlappingBagItems);
        Assert.False(report.HasSoftFindings);
        Assert.False(report.IsFatal);
        Assert.Contains("3 bag item(s) to re-home on load", report.ToLogLines(WorldDescriptor.Default).First(), StringComparison.Ordinal);
    }

    [Fact]
    public void ABagOfItemsThatFit_HasNoOverlap_AndTwoPlayersAreCountedApart()
    {
        using MigrationTestDatabase db = Migrated();
        db.ExecuteScript(
            Profile + SecondProfile + Item(10, "gear.iron-blade") + Item(11, "gear.iron-blade") + Item(12, "gear.iron-blade") +
            SlotOf("p1", 0, 10) + SlotOf("p1", 2, 11) +
            // The same anchor for another player is not an overlap.
            SlotOf("p2", 0, 12));
        WorldIntegrityReport report = Check(db, ServerBootstrap.IntegrityOptionsFor(ContentCatalog.Shipped));
        Assert.Equal(0, report.OverlappingBagItems);
    }

    [Fact]
    public void HeldBackRows_AreCountedInTheHeadLine_ButAreNoSoftFinding()
    {
        using MigrationTestDatabase db = Migrated();
        db.ExecuteScript(Profile + Item(10, "gear.iron-blade") + Item(11, "gear.iron-blade") + Slot(200, 10) + Slot(215, 11));
        WorldIntegrityReport report = Check(db);
        Assert.Equal(2, report.HeldBackItems);
        Assert.Equal(0, report.UnknownSlotIndexes);
        Assert.False(report.HasSoftFindings);
        Assert.Contains("2 held-back item(s)", report.ToLogLines(WorldDescriptor.Default).First(), StringComparison.Ordinal);
    }

    [Fact]
    public void AKindInASlotItDoesNotAllow_IsAMismatch_AndTheDefaultAcceptsAll()
    {
        using MigrationTestDatabase db = Migrated();
        db.ExecuteScript(Profile + Item(10, "gear.leather-cap") + Slot(100, 10) + Item(11, "tool.torch") + Slot(101, 11));

        Assert.Equal(0, Check(db).EquipmentSlotMismatches);

        WorldIntegrityReport report = Check(db, ServerBootstrap.IntegrityOptionsFor(ContentCatalog.Shipped));

        // The cap in the main hand; the torch is fine in the off hand.
        Assert.Equal(1, report.EquipmentSlotMismatches);
        Assert.True(report.HasSoftFindings);
        Assert.False(report.IsFatal);
        Assert.Contains(report.ToLogLines(WorldDescriptor.Default), l => l.Contains("slot their kind does not allow", StringComparison.Ordinal));
    }

    [Fact]
    public void AnUnknownKindInTheMainHand_IsNotAlsoASlotMismatch()
    {
        using MigrationTestDatabase db = Migrated();
        db.ExecuteScript(Profile + Item(10, "tool.retired") + Slot(100, 10));
        WorldIntegrityReport report = Check(db, ServerBootstrap.IntegrityOptionsFor(ContentCatalog.Shipped));
        Assert.Equal(0, report.EquipmentSlotMismatches);
        Assert.Equal(1, report.UnknownItemKinds);
    }

    [Fact]
    public void ADamagedStoredRoll_IsCounted_AndGoodAndMissingRollsAreNot()
    {
        using MigrationTestDatabase db = Migrated();
        db.ExecuteScript(
            Profile + Item(10, "gear.iron-blade") + Item(11, "gear.iron-blade") + Item(12, "tool.torch") +
            "UPDATE item_instances SET rolled_modifiers = '{\"v\":1,\"r\":\"Rare\",\"m\":[[\"Strength\",\"Flat\",6]]}' WHERE item_instance_id = 10;" +
            "UPDATE item_instances SET rolled_modifiers = '{\"v\":1,\"r\":\"Rare\",\"m\":[[\"Luck\",\"Flat\",6]]}' WHERE item_instance_id = 11;");

        WorldIntegrityReport report = Check(db);

        Assert.Equal(1, report.DamagedItemRolls);
        Assert.True(report.HasSoftFindings);
        Assert.False(report.IsFatal);
        Assert.Contains(report.ToLogLines(WorldDescriptor.Default), l => l.Contains("damaged stored roll", StringComparison.Ordinal));
    }

    [Fact]
    public void ACleanMigratedDatabaseWithRolledGear_HasNoSoftFinding()
    {
        using MigrationTestDatabase db = Migrated();
        db.ExecuteScript(
            Profile + Item(10, "gear.iron-blade") + Slot(100, 10) + Item(11, "gear.ward-lantern") + Slot(101, 11) +
            "UPDATE item_instances SET rolled_modifiers = '{\"v\":1,\"r\":\"Rare\",\"m\":[[\"Strength\",\"Flat\",6]]}' WHERE item_instance_id = 10;");

        WorldIntegrityReport report = Check(db, ServerBootstrap.IntegrityOptionsFor(ContentCatalog.Shipped));

        Assert.False(report.HasSoftFindings);
    }
}
