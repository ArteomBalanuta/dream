using Dream.Server.Characters;
using Dream.Server.Persistence.Repositories;
using Dream.Simulation.Characters;
using Dream.Tests.Server.Persistence.Migrations;
using Dream.Tests.Support;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Dream.Tests.Server.Characters;

// Wave G1: --characters list | release | rename.
public sealed class CharacterCliTests
{
    private static MigrationTestDatabase Migrated()
    {
        MigrationTestDatabase db = new();
        db.Schema.EnsureSchema();
        SqlitePools.Release(db.DatabasePath);
        return db;
    }

    private static (int Exit, string Out, string Err) Run(MigrationTestDatabase db, Func<IDisposable?>? hold, params string[] args)
    {
        StringWriter output = new();
        StringWriter error = new();
        int exit = CharacterCli.Run(args, () => db.Schema, output, error, hold);
        return (exit, output.ToString(), error.ToString());
    }

    private static void Profile(MigrationTestDatabase db, string key, string display, int level = 1) =>
        db.ExecuteScript($"INSERT INTO player_profiles (player_name, display_name, level) VALUES ('{key}', '{display}', {level});");

    private static void Claim(MigrationTestDatabase db, string account, string key) =>
        Assert.True(new SqliteAccountRepository(db.Schema).RecordJoin(
            TestAccountTokens.KeyFor(account), new CharacterKey(key), claim: true, new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc)));

    private sealed class Lease : IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }

    [Fact]
    public void List_PrintsTabSeparatedRows_AndNeverAHash()
    {
        using MigrationTestDatabase db = Migrated();
        Profile(db, "alice", "Alice", level: 4);
        Profile(db, "legacy", "Legacy");
        Claim(db, "alice", "alice");
        byte[] hash = (byte[])db.Scalar("SELECT token_hash FROM accounts;")!;

        (int exit, string output, _) = Run(db, null, "list");

        Assert.Equal(0, exit);
        string[] lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Equal(2, lines.Length);
        Assert.Equal(["Alice", "alice", "4", "1", "2026-10-01T00:00:00.0000000Z"], lines[0].Split('\t'));
        Assert.Equal(["Legacy", "legacy", "1", "-", "-"], lines[1].Split('\t'));
        Assert.DoesNotContain(Convert.ToHexString(hash), output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Convert.ToBase64String(hash), output, StringComparison.Ordinal);
        Assert.DoesNotContain(TestAccountTokens.For("alice"), output, StringComparison.Ordinal);
    }

    [Fact]
    public void List_PrintsControlCharactersAsQuestionMarks()
    {
        using MigrationTestDatabase db = Migrated();
        db.ExecuteScript("INSERT INTO player_profiles (player_name, display_name) VALUES ('k', 'a' || char(9) || 'b' || char(10) || 'c');");

        (_, string output, _) = Run(db, null, "list");

        Assert.Equal("a?b?c\tk\t1\t-\t-", output.TrimEnd());
    }

    [Fact]
    public void Release_ClearsOwnership_ThenTheOwnersNextClaimWorks()
    {
        using MigrationTestDatabase db = Migrated();
        Profile(db, "Bob", "Bob Builder");
        Claim(db, "alice", "Bob");

        (int exit, string output, _) = Run(db, null, "release", "bob builder");

        Assert.Equal(0, exit);
        Assert.Equal("released\tBob\tBob Builder", output.TrimEnd());
        Assert.Equal(1L, db.Scalar("SELECT COUNT(*) FROM player_profiles WHERE account_id IS NULL;"));
        Assert.Equal(1L, db.Scalar("SELECT COUNT(*) FROM accounts;"));
        Claim(db, "carol", "Bob");
        Assert.Equal(1L, db.Scalar("SELECT COUNT(*) FROM player_profiles WHERE account_id IS NOT NULL;"));
    }

    [Fact]
    public void Release_OfAnUnownedCharacter_SaysSo_AndMatchesTheKeyAsAFallback()
    {
        using MigrationTestDatabase db = Migrated();
        Profile(db, "OldKey", "New Display");

        (int exit, string output, _) = Run(db, null, "release", "OldKey");
        (int missing, _, string error) = Run(db, null, "release", "Nobody");

        Assert.Equal(0, exit);
        Assert.Equal("already-unowned\tOldKey\tNew Display", output.TrimEnd());
        Assert.Equal(2, missing);
        Assert.Contains("Nobody", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Rename_ChangesOnlyTheDisplayName_AndCaseOnlyRenameWorks()
    {
        using MigrationTestDatabase db = Migrated();
        Profile(db, "k", "alice");
        Claim(db, "alice", "k");
        db.ExecuteScript(
            "INSERT INTO player_world_state (world_id, player_name, position_x, position_y, position_z, facing_x, facing_y, facing_z) VALUES (1, 'k', 1, 2, 3, 0, 0, 1);");
        Lease lease = new();

        (int exit, string output, _) = Run(db, () => lease, "rename", "ALICE", "Alice");
        (int second, _, _) = Run(db, () => lease, "rename", "Alice", "  Alicia  ");

        Assert.Equal(0, exit);
        Assert.Equal("renamed\tk\talice\tAlice", output.TrimEnd());
        Assert.Equal(0, second);
        Assert.True(lease.Disposed);
        Assert.Equal("Alicia", db.Scalar("SELECT display_name FROM player_profiles WHERE player_name = 'k';"));
        Assert.Equal(1L, db.Scalar("SELECT COUNT(*) FROM player_world_state WHERE player_name = 'k' AND position_x = 1;"));
        Assert.Equal(1L, db.Scalar("SELECT COUNT(*) FROM player_profiles WHERE account_id IS NOT NULL;"));
    }

    [Fact]
    public void Rename_ToATakenNameOrAnotherCharactersKey_ExitsTwo()
    {
        using MigrationTestDatabase db = Migrated();
        Profile(db, "a", "Alice");
        Profile(db, "b", "Bob");
        Profile(db, "OldKey", "Carol");

        Assert.Equal(2, Run(db, null, "rename", "Alice", "bob").Exit);
        Assert.Equal(2, Run(db, null, "rename", "Alice", "OldKey").Exit);
        Assert.Equal(2, Run(db, null, "rename", "Nobody", "Fresh").Exit);

        Assert.Equal("Alice", db.Scalar("SELECT display_name FROM player_profiles WHERE player_name = 'a';"));
    }

    [Fact]
    public void Rename_ToAnInvalidName_ExitsOne_AndAHeldWorldExitsSeven()
    {
        using MigrationTestDatabase db = Migrated();
        Profile(db, "a", "Alice");

        Assert.Equal(1, Run(db, null, "rename", "Alice", "   ").Exit);
        Assert.Equal(1, Run(db, null, "rename", "Alice", new string('x', 33)).Exit);
        Assert.Equal(1, Run(db, null, "rename", "Alice", "bad​name").Exit);
        (int held, _, string error) = Run(db, () => null, "rename", "Alice", "Alicia");

        Assert.Equal(7, held);
        Assert.Contains("in use", error, StringComparison.Ordinal);
        Assert.Equal("Alice", db.Scalar("SELECT display_name FROM player_profiles WHERE player_name = 'a';"));
    }

    [Fact]
    public void UsageErrors_ExitOne()
    {
        using MigrationTestDatabase db = Migrated();

        Assert.Equal(1, Run(db, null).Exit);
        Assert.Equal(1, Run(db, null, "frobnicate").Exit);
        Assert.Equal(1, Run(db, null, "list", "extra").Exit);
        Assert.Equal(1, Run(db, null, "release").Exit);
        Assert.Equal(1, Run(db, null, "rename", "a").Exit);
    }

    [Fact]
    public void ASchemaNewerThanTheBuild_ExitsThree()
    {
        using MigrationTestDatabase db = Migrated();
        db.ExecuteScript("INSERT INTO schema_version (version, name, applied_utc) VALUES (999, 'future', 'x');");

        int exit = CharacterCli.Run(
            ["list"],
            () =>
            {
                db.Schema.EnsureSchema();
                return db.Schema;
            },
            new StringWriter(),
            new StringWriter());

        Assert.Equal(3, exit);
    }
}
