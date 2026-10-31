using Dream.Server.Persistence;
using Dream.Server.Persistence.Migrations;
using Dream.Server.Persistence.Repositories;
using Dream.Simulation.Characters;
using Dream.Simulation.Persistence;
using Dream.Tests.Server.Persistence.Migrations;
using Dream.Tests.Server.Worlds;
using Dream.Tests.Support;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Dream.Tests.Server.Persistence;

// Wave G1: the SQLite account repository, the ownership column and the database-level facts around it.
public sealed class SqliteAccountRepositoryTests
{
    private static MigrationTestDatabase Migrated()
    {
        MigrationTestDatabase db = new();
        db.Schema.EnsureSchema();
        SqliteConnection.ClearAllPools();
        return db;
    }

    private static void Profile(MigrationTestDatabase db, string key, string display) =>
        db.ExecuteScript($"INSERT INTO player_profiles (player_name, display_name) VALUES ('{key}', '{display}');");

    private static string Text(MigrationTestDatabase db, string sql) => Convert.ToString(db.Scalar(sql)) ?? string.Empty;

    [Fact]
    public void Lookup_IgnoresAsciiCase_AndReturnsTheKeyNotTheTypedText()
    {
        using MigrationTestDatabase db = Migrated();
        Profile(db, "Bob", "Bob Builder");
        SqliteAccountRepository repository = new(db.Schema);

        CharacterLookup lookup = repository.Lookup("bOB bUILDER", null);

        Assert.NotNull(lookup.Existing);
        Assert.Equal("Bob", lookup.Existing!.Value.Key.Value);
        Assert.Equal("Bob Builder", lookup.Existing.Value.DisplayName);
        Assert.Null(lookup.Existing.Value.Owner);
        Assert.False(lookup.KeyReserved);
        Assert.Equal(0, lookup.OwnedByAccount);
    }

    [Fact]
    public void Lookup_ReportsOwnerAndTheCountOwnedByTheAskingAccount()
    {
        using MigrationTestDatabase db = Migrated();
        Profile(db, "a", "A");
        Profile(db, "b", "B");
        Profile(db, "c", "C");
        SqliteAccountRepository repository = new(db.Schema);
        AccountKey alice = TestAccountTokens.KeyFor("alice");
        AccountKey bob = TestAccountTokens.KeyFor("bob");
        Assert.True(repository.RecordJoin(alice, new CharacterKey("a"), claim: true, DateTime.UtcNow));
        Assert.True(repository.RecordJoin(alice, new CharacterKey("b"), claim: true, DateTime.UtcNow));

        CharacterLookup asAlice = repository.Lookup("a", alice);
        CharacterLookup asBob = repository.Lookup("A", bob);
        CharacterLookup noAccount = repository.Lookup("a", null);
        CharacterLookup unowned = repository.Lookup("c", alice);

        Assert.Equal(alice, asAlice.Existing!.Value.Owner);
        Assert.Equal(2, asAlice.OwnedByAccount);
        Assert.Equal(alice, asBob.Existing!.Value.Owner);
        Assert.Equal(0, asBob.OwnedByAccount);
        Assert.Equal(0, noAccount.OwnedByAccount);
        Assert.Null(unowned.Existing!.Value.Owner);
        Assert.Equal(2, unowned.OwnedByAccount);
    }

    [Fact]
    public void Lookup_KeyReserved_WhenOnlyARetiredKeyMatches()
    {
        using MigrationTestDatabase db = Migrated();
        Profile(db, "OldKey", "New Display");
        SqliteAccountRepository repository = new(db.Schema);

        CharacterLookup reserved = repository.Lookup("OldKey", null);
        CharacterLookup free = repository.Lookup("Nobody", null);

        Assert.Null(reserved.Existing);
        Assert.True(reserved.KeyReserved);
        Assert.Null(free.Existing);
        Assert.False(free.KeyReserved);
    }

    [Fact]
    public void RecordJoin_KeepsCreatedUtc_AndMovesLastSeenUtc()
    {
        using MigrationTestDatabase db = Migrated();
        SqliteAccountRepository repository = new(db.Schema);
        AccountKey alice = TestAccountTokens.KeyFor("alice");
        DateTime first = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
        DateTime second = first.AddDays(3);

        Assert.True(repository.RecordJoin(alice, new CharacterKey("none"), claim: false, first));
        string created = Text(db, "SELECT created_utc FROM accounts;");
        string seen = Text(db, "SELECT last_seen_utc FROM accounts;");
        Assert.True(repository.RecordJoin(alice, new CharacterKey("none"), claim: false, second));

        Assert.Equal(1L, db.Scalar("SELECT COUNT(*) FROM accounts;"));
        Assert.Equal(created, Text(db, "SELECT created_utc FROM accounts;"));
        Assert.Equal(SqliteSchemaBootstrapper.FormatUtc(first), created);
        Assert.Equal(SqliteSchemaBootstrapper.FormatUtc(second), Text(db, "SELECT last_seen_utc FROM accounts;"));
        Assert.NotEqual(seen, Text(db, "SELECT last_seen_utc FROM accounts;"));
        Assert.Equal(32L, db.Scalar("SELECT length(token_hash) FROM accounts;"));
    }

    [Fact]
    public void RecordJoin_NeverTakesAnOwnedRow_AndReturnsFalse()
    {
        using MigrationTestDatabase db = Migrated();
        Profile(db, "k", "K");
        SqliteAccountRepository repository = new(db.Schema);
        AccountKey alice = TestAccountTokens.KeyFor("alice");
        AccountKey bob = TestAccountTokens.KeyFor("bob");
        Assert.True(repository.RecordJoin(alice, new CharacterKey("k"), claim: true, DateTime.UtcNow));
        object? owner = db.Scalar("SELECT account_id FROM player_profiles WHERE player_name = 'k';");

        Assert.False(repository.RecordJoin(bob, new CharacterKey("k"), claim: true, DateTime.UtcNow));
        Assert.False(repository.RecordJoin(bob, new CharacterKey("missing"), claim: true, DateTime.UtcNow));

        Assert.Equal(owner, db.Scalar("SELECT account_id FROM player_profiles WHERE player_name = 'k';"));
        // The losing account is still recorded as seen.
        Assert.Equal(2L, db.Scalar("SELECT COUNT(*) FROM accounts;"));
    }

    [Fact]
    public void ProfileSave_LeavesAccountIdAlone()
    {
        using MigrationTestDatabase db = Migrated();
        SqlitePlayerProfileRepository profiles = new(db.Schema);
        PersistedPlayerProfile created = profiles.GetOrCreate("K");
        SqliteAccountRepository accounts = new(db.Schema);
        Assert.True(accounts.RecordJoin(TestAccountTokens.KeyFor("alice"), new CharacterKey("K"), claim: true, DateTime.UtcNow));
        object? owner = db.Scalar("SELECT account_id FROM player_profiles WHERE player_name = 'K';");
        Assert.NotNull(owner);
        Assert.IsNotType<DBNull>(owner);

        profiles.Save(profiles.TryGet("K")!.Value with { DisplayName = "K" });
        profiles.Save(created);

        Assert.Equal(owner, db.Scalar("SELECT account_id FROM player_profiles WHERE player_name = 'K';"));
    }

    [Fact]
    public void WorldsCopyAndExport_LeaveAccountsAndProfilesUnchanged()
    {
        using WorldCatalogTestDatabase db = new();
        db.Catalog.Create(new Dream.Server.Worlds.WorldCreateOptions("Alpha", 20));
        db.Execute("INSERT INTO player_profiles (player_name, display_name) VALUES ('k', 'K');");
        new SqliteAccountRepository(db.Schema).RecordJoin(TestAccountTokens.KeyFor("alice"), new CharacterKey("k"), claim: true, DateTime.UtcNow);
        string Dump() =>
            Convert.ToString(db.Scalar("SELECT group_concat(account_id || ':' || hex(token_hash) || ':' || created_utc || ':' || last_seen_utc) FROM accounts;"))
            + "|" + Convert.ToString(db.Scalar("SELECT group_concat(player_name || ':' || display_name || ':' || ifnull(account_id, 'n') || ':' || updated_utc) FROM player_profiles;"));
        string before = Dump();

        Dream.Server.Worlds.WorldRecord copy = db.Catalog.Copy(new Dream.Simulation.World.WorldId(1), "Beta");

        Assert.Equal(before, Dump());
        Assert.NotEqual(1L, copy.Descriptor.Id.Value);
        Assert.Equal(1L, db.Scalar("SELECT COUNT(*) FROM accounts;"));
        Assert.Equal(1L, db.Scalar("SELECT COUNT(*) FROM player_profiles;"));
    }

    [Fact]
    public void RestoringAV6File_ThenStarting_GivesV7WithNoAccounts()
    {
        using MigrationTestDatabase source = new();
        source.Schema.EnsureSchema(new SqliteSchemaMigrator(
            [.. SchemaMigrations.LoadFromDirectory(MigrationTestDatabase.MigrationsDirectory).Take(6)]));
        SqliteConnection.ClearAllPools();
        using MigrationTestDatabase target = new();
        target.Schema.EnsureSchema();
        SqliteConnection.ClearAllPools();

        int exit = DatabaseCli.Restore(
            target.DatabasePath, MigrationTestDatabase.InitScriptPath, source.DatabasePath, new StringWriter(), new StringWriter());
        Assert.Equal(0, exit);
        target.Schema.EnsureSchema();
        SqliteConnection.ClearAllPools();

        Assert.Equal((long)MigrationTestDatabase.ShippedLatestVersion, target.Scalar("SELECT MAX(version) FROM schema_version;"));
        Assert.Equal(0L, target.Scalar("SELECT COUNT(*) FROM accounts;"));
    }

    [Fact]
    public void RestoringAV7Backup_BringsBackItsAccountsAsTheyWere()
    {
        using MigrationTestDatabase source = Migrated();
        Profile(source, "k", "K");
        new SqliteAccountRepository(source.Schema).RecordJoin(
            TestAccountTokens.KeyFor("alice"), new CharacterKey("k"), claim: true, new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc));
        string backup = Path.Combine(source.RootDirectory, "backup.sqlite");
        Assert.Equal(0, DatabaseCli.Backup(source.DatabasePath, backup, new StringWriter(), new StringWriter()));
        SqliteConnection.ClearAllPools();

        using MigrationTestDatabase target = Migrated();
        Profile(target, "other", "Other");
        target.ExecuteScript("INSERT INTO accounts (token_hash, created_utc, last_seen_utc) VALUES (zeroblob(32), 'x', 'x');");
        SqliteConnection.ClearAllPools();
        int exit = DatabaseCli.Restore(
            target.DatabasePath, MigrationTestDatabase.InitScriptPath, backup, new StringWriter(), new StringWriter());
        Assert.Equal(0, exit);
        SqliteConnection.ClearAllPools();

        Assert.Equal(1L, target.Scalar("SELECT COUNT(*) FROM accounts;"));
        Assert.Equal(
            SqliteSchemaBootstrapper.FormatUtc(new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc)),
            Text(target, "SELECT created_utc FROM accounts;"));
        Assert.Equal(1L, target.Scalar("SELECT COUNT(*) FROM player_profiles WHERE player_name = 'k' AND account_id IS NOT NULL;"));
        Assert.Equal(0L, target.Scalar("SELECT COUNT(*) FROM player_profiles WHERE player_name = 'other';"));
    }
}
