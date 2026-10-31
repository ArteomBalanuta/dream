using Microsoft.Data.Sqlite;
using Xunit;
using Dream.Server;
using Dream.Server.Hosting;
using Dream.Server.Persistence;
using Dream.Simulation.World;
using Dream.Tests.Server.Persistence.Migrations;

namespace Dream.Tests.Server.Persistence;

// Persistence wave F3: `--db backup <path>` and `--db restore <path> --yes`.
public sealed class DatabaseCliTests
{
    private static (int Exit, string Out, string Err) Run(MigrationTestDatabase db, params string[] args)
    {
        StringWriter output = new();
        StringWriter error = new();
        int exit = DatabaseCli.Run(args, db.DatabasePath, MigrationTestDatabase.InitScriptPath, output, error);
        SqliteConnection.ClearAllPools();
        return (exit, output.ToString(), error.ToString());
    }

    private static MigrationTestDatabase WithPlayer(string name)
    {
        MigrationTestDatabase db = new();
        db.Schema.EnsureSchema();
        db.ExecuteScript($"INSERT INTO player_profiles (player_name, display_name) VALUES ('{name}', '{name}');");
        SqliteConnection.ClearAllPools();
        return db;
    }

    private static string Backup(MigrationTestDatabase db) => Path.Combine(db.RootDirectory, "copy", "backup.db");

    [Fact]
    public void Usage_IsEnforced()
    {
        using MigrationTestDatabase db = new();
        Assert.Equal(DatabaseCli.ExitUsage, Run(db).Exit);
        Assert.Equal(DatabaseCli.ExitUsage, Run(db, "backup").Exit);
        Assert.Equal(DatabaseCli.ExitUsage, Run(db, "restore", "x").Exit);
        Assert.Equal(DatabaseCli.ExitUsage, Run(db, "frob").Exit);
    }

    [Fact]
    public void Backup_CopiesTheWholeFile_WhileAServerHoldsTheWorld()
    {
        using MigrationTestDatabase db = WithPlayer("alice");
        using ServerPersistenceBundle server = ServerBootstrap.CreatePersistenceBundle(db.DatabasePath, MigrationTestDatabase.InitScriptPath);
        string target = Backup(db);

        (int exit, string output, string error) = Run(db, "backup", target);

        Assert.True(exit == 0, error);
        Assert.StartsWith("backup\t", output, StringComparison.Ordinal);
        Assert.True(File.Exists(target));
        Assert.False(File.Exists(target + ".partial"));
        using SqliteConnection copy = MigrationTestDatabase.OpenFile(target);
        using SqliteCommand command = copy.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM player_profiles WHERE player_name = 'alice';";
        Assert.Equal(1L, command.ExecuteScalar());
        // The live world is still held: a backup takes no lease.
        Assert.True(WorldHostLease.IsHeld(db.DatabasePath, WorldId.Default));
    }

    [Fact]
    public void Backup_RefusesAnExistingDestination_AndAMissingDatabase()
    {
        using MigrationTestDatabase db = WithPlayer("alice");
        string target = Backup(db);
        Assert.Equal(0, Run(db, "backup", target).Exit);
        Assert.Equal(DatabaseCli.ExitSourceOrTargetInvalid, Run(db, "backup", target).Exit);

        using MigrationTestDatabase empty = new();
        Assert.Equal(DatabaseCli.ExitSourceOrTargetInvalid, Run(empty, "backup", Backup(empty)).Exit);
    }

    [Fact]
    public void Restore_NeedsYes()
    {
        using MigrationTestDatabase db = WithPlayer("alice");
        Assert.Equal(DatabaseCli.ExitUsage, Run(db, "restore", Backup(db)).Exit);
    }

    [Fact]
    public void Restore_SwapsTheFile_AndKeepsThePreviousDatabase()
    {
        using MigrationTestDatabase db = WithPlayer("alice");
        string target = Backup(db);
        Assert.Equal(0, Run(db, "backup", target).Exit);
        db.ExecuteScript("INSERT INTO player_profiles (player_name, display_name) VALUES ('bob', 'bob');");
        SqliteConnection.ClearAllPools();

        (int exit, string output, string error) = Run(db, "restore", target, "--yes");

        Assert.True(exit == 0, error);
        Assert.Contains("previous database kept as", output, StringComparison.Ordinal);
        Assert.Equal(1L, db.Scalar("SELECT COUNT(*) FROM player_profiles WHERE player_name = 'alice';"));
        Assert.Equal(0L, db.Scalar("SELECT COUNT(*) FROM player_profiles WHERE player_name = 'bob';"));
        string safety = Assert.Single(Directory.GetFiles(db.RootDirectory, "*.pre-restore.*.bak"));
        using SqliteConnection kept = MigrationTestDatabase.OpenFile(safety);
        using SqliteCommand command = kept.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM player_profiles WHERE player_name = 'bob';";
        Assert.Equal(1L, command.ExecuteScalar());
        Assert.False(File.Exists(db.DatabasePath + "-wal"));
        // The restored file opens.
        using ServerPersistenceBundle bundle = ServerBootstrap.CreatePersistenceBundle(db.DatabasePath, MigrationTestDatabase.InitScriptPath);
    }

    [Fact]
    public void Restore_IsRefused_WhileAnyWorldIsHeld()
    {
        using MigrationTestDatabase db = WithPlayer("alice");
        string target = Backup(db);
        Assert.Equal(0, Run(db, "backup", target).Exit);
        db.ExecuteScript("INSERT INTO player_profiles (player_name, display_name) VALUES ('bob', 'bob');");
        SqliteConnection.ClearAllPools();

        using ServerPersistenceBundle server = ServerBootstrap.CreatePersistenceBundle(db.DatabasePath, MigrationTestDatabase.InitScriptPath);
        (int exit, _, string error) = Run(db, "restore", target, "--yes");

        Assert.Equal(DatabaseCli.ExitWorldHeld, exit);
        Assert.Contains("Stop it first", error, StringComparison.Ordinal);
        Assert.Equal(1L, db.Scalar("SELECT COUNT(*) FROM player_profiles WHERE player_name = 'bob';"));
    }

    [Fact]
    public void Restore_RefusesASourceFromANewerBuild_AndACorruptOne_AndLeavesTheDatabaseAlone()
    {
        using MigrationTestDatabase db = WithPlayer("alice");
        string newer = Path.Combine(db.RootDirectory, "newer.db");
        Assert.Equal(0, Run(db, "backup", newer).Exit);
        using (SqliteConnection connection = MigrationTestDatabase.OpenFile(newer))
        {
            MigrationTestDatabase.Execute(
                connection,
                $"INSERT INTO schema_version (version, name, applied_utc) VALUES ({MigrationTestDatabase.ShippedLatestVersion + 1}, 'future', 'x');");
        }

        SqliteConnection.ClearAllPools();
        (int exit, _, string error) = Run(db, "restore", newer, "--yes");
        Assert.Equal(DatabaseCli.ExitSchemaTooNew, exit);
        Assert.Contains("newer than", error, StringComparison.Ordinal);

        string garbage = Path.Combine(db.RootDirectory, "garbage.db");
        File.WriteAllBytes(garbage, Enumerable.Repeat((byte)0x41, 8192).ToArray());
        (exit, _, error) = Run(db, "restore", garbage, "--yes");
        Assert.NotEqual(0, exit);

        Assert.Equal(1L, db.Scalar("SELECT COUNT(*) FROM player_profiles WHERE player_name = 'alice';"));
        Assert.Empty(Directory.GetFiles(db.RootDirectory, "*.pre-restore.*.bak"));
        Assert.Equal(DatabaseCli.ExitSourceOrTargetInvalid, Run(db, "restore", Path.Combine(db.RootDirectory, "missing.db"), "--yes").Exit);
    }
}
