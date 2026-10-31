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
        Assert.False(File.Exists(db.DatabasePath + "-wal"));
        Assert.Equal(1L, db.Scalar("SELECT COUNT(*) FROM player_profiles WHERE player_name = 'alice';"));
        Assert.Equal(0L, db.Scalar("SELECT COUNT(*) FROM player_profiles WHERE player_name = 'bob';"));
        string safety = Assert.Single(Directory.GetFiles(db.RootDirectory, "*.pre-restore.*.bak"));
        Assert.Equal(1L, CountPlayers(safety, "bob"));
        // The restored file opens.
        using ServerPersistenceBundle bundle = ServerBootstrap.CreatePersistenceBundle(db.DatabasePath, MigrationTestDatabase.InitScriptPath);
    }

    private static long CountPlayers(string databaseFile, string name)
    {
        using SqliteConnection connection = MigrationTestDatabase.OpenFile(databaseFile);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM player_profiles WHERE player_name = '{name}';";
        long count = (long)command.ExecuteScalar()!;
        connection.Close();
        SqliteConnection.ClearAllPools();
        return count;
    }

    // Runs sql on a live connection that never checkpoints and copies the database and its -wal
    // while the rows exist only in the WAL: the copy is a hot database.
    private static void CopyWithHotWal(MigrationTestDatabase db, string sql, string destination)
    {
        using (SqliteConnection connection = db.Open())
        {
            MigrationTestDatabase.Execute(connection, "PRAGMA wal_autocheckpoint=0;");
            MigrationTestDatabase.Execute(connection, sql);
            File.Copy(db.DatabasePath, destination, overwrite: true);
            File.Copy(db.DatabasePath + "-wal", destination + "-wal", overwrite: true);
        }

        SqliteConnection.ClearAllPools();
    }

    private static string BackupOf(MigrationTestDatabase db, string fileName)
    {
        string path = Path.Combine(db.RootDirectory, fileName);
        Assert.Equal(0, Run(db, "backup", path).Exit);
        return path;
    }

    [Fact]
    public void Restore_PlantedWalOfTheLiveFile_IsRemoved_AndTheRowsAreTheSources()
    {
        using MigrationTestDatabase db = WithPlayer("alice");
        string source = BackupOf(db, "source.db");
        // The live file holds bob only in a hot -wal.
        string hot = Path.Combine(db.RootDirectory, "hot.db");
        CopyWithHotWal(db, "INSERT INTO player_profiles (player_name, display_name) VALUES ('bob', 'bob');", hot);
        File.Copy(hot, db.DatabasePath, overwrite: true);
        File.Copy(hot + "-wal", db.DatabasePath + "-wal", overwrite: true);
        File.Delete(db.DatabasePath + "-shm");
        Assert.True(new FileInfo(db.DatabasePath + "-wal").Length > 0);

        (int exit, _, string error) = Run(db, "restore", source, "--yes");

        Assert.True(exit == 0, error);
        Assert.False(File.Exists(db.DatabasePath + "-wal"));
        Assert.False(File.Exists(db.DatabasePath + "-shm"));
        Assert.Equal(1L, CountPlayers(db.DatabasePath, "alice"));
        Assert.Equal(0L, CountPlayers(db.DatabasePath, "bob"));
        // The safety copy was taken from the live file with its WAL applied.
        Assert.Equal(1L, CountPlayers(Assert.Single(Directory.GetFiles(db.RootDirectory, "*.pre-restore.*.bak")), "bob"));
    }

    [Fact]
    public void Restore_FromASourceWithAHotWal_RestoresTheWalRows()
    {
        using MigrationTestDatabase db = WithPlayer("alice");
        string source = Path.Combine(db.RootDirectory, "hot-source.db");
        CopyWithHotWal(db, "INSERT INTO player_profiles (player_name, display_name) VALUES ('bob', 'bob');", source);
        Assert.True(new FileInfo(source + "-wal").Length > 0);
        File.Delete(source + "-shm");

        (int exit, _, string error) = Run(db, "restore", source, "--yes");

        Assert.True(exit == 0, error);
        Assert.Equal(1L, CountPlayers(db.DatabasePath, "alice"));
        Assert.Equal(1L, CountPlayers(db.DatabasePath, "bob"));
    }

    [Fact]
    public void Restore_OverAHeaderDamagedLiveFile_KeepsARawSafetyCopy_AndRepairsIt()
    {
        using MigrationTestDatabase db = WithPlayer("alice");
        string source = BackupOf(db, "source.db");
        byte[] damaged = File.ReadAllBytes(db.DatabasePath);
        Array.Fill(damaged, (byte)0x41, 0, 100);
        File.WriteAllBytes(db.DatabasePath, damaged);

        (int exit, string output, string error) = Run(db, "restore", source, "--yes");

        Assert.True(exit == 0, error);
        Assert.Contains("previous database kept as", output, StringComparison.Ordinal);
        string safety = Assert.Single(Directory.GetFiles(db.RootDirectory, "*.pre-restore.*.bak"));
        Assert.Equal(damaged, File.ReadAllBytes(safety));
        Assert.Equal(1L, CountPlayers(db.DatabasePath, "alice"));
        using ServerPersistenceBundle bundle = ServerBootstrap.CreatePersistenceBundle(db.DatabasePath, MigrationTestDatabase.InitScriptPath);
    }

    [Fact]
    public void Restore_OverATruncatedLiveFile_KeepsARawSafetyCopy_AndRepairsIt()
    {
        using MigrationTestDatabase db = WithPlayer("alice");
        string source = BackupOf(db, "source.db");
        using (FileStream stream = new(db.DatabasePath, FileMode.Open, FileAccess.Write))
        {
            stream.SetLength(5000);
        }

        (int exit, _, string error) = Run(db, "restore", source, "--yes");

        Assert.True(exit == 0, error);
        string safety = Assert.Single(Directory.GetFiles(db.RootDirectory, "*.pre-restore.*.bak"));
        Assert.Equal(5000L, new FileInfo(safety).Length);
        Assert.Equal(1L, CountPlayers(db.DatabasePath, "alice"));
        using ServerPersistenceBundle bundle = ServerBootstrap.CreatePersistenceBundle(db.DatabasePath, MigrationTestDatabase.InitScriptPath);
    }

    [Fact]
    public void Restore_WhenTheLiveFileIsLockedForSwapping_LeavesItIntact_RemovesTheStaging_AndKeepsAValidBak()
    {
        using MigrationTestDatabase db = WithPlayer("alice");
        string source = BackupOf(db, "source.db");
        db.ExecuteScript("INSERT INTO player_profiles (player_name, display_name) VALUES ('bob', 'bob');");
        SqliteConnection.ClearAllPools();
        byte[] before = File.ReadAllBytes(db.DatabasePath);

        int exit;
        string error;
        // Readable by others, but not replaceable.
        using (new FileStream(db.DatabasePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            (exit, _, error) = Run(db, "restore", source, "--yes");
        }

        Assert.Equal(DatabaseCli.ExitSourceOrTargetInvalid, exit);
        Assert.Contains("left as it was", error, StringComparison.Ordinal);
        Assert.False(File.Exists(db.DatabasePath + ".restore.tmp"));
        Assert.Equal(before, File.ReadAllBytes(db.DatabasePath));
        string safety = Assert.Single(Directory.GetFiles(db.RootDirectory, "*.pre-restore.*.bak"));
        Assert.Equal(1L, CountPlayers(safety, "bob"));
        Assert.Equal(1L, CountPlayers(db.DatabasePath, "bob"));
    }

    // SQLite itself removes a stray -wal when it opens the file, so the sidecar handling is tested on the
    // two internal steps that see them: the swap and the safety copy.
    [Fact]
    public void SwapIn_ReplacesTheFile_AndRemovesTheOldWalAndShm()
    {
        using MigrationTestDatabase db = new();
        File.WriteAllText(db.DatabasePath, "old");
        File.WriteAllText(db.DatabasePath + "-wal", "old-wal");
        File.WriteAllText(db.DatabasePath + "-shm", "old-shm");
        string staged = db.DatabasePath + ".restore.tmp";
        File.WriteAllText(staged, "new");

        DatabaseCli.SwapIn(staged, db.DatabasePath);

        Assert.Equal("new", File.ReadAllText(db.DatabasePath));
        Assert.False(File.Exists(db.DatabasePath + "-wal"));
        Assert.False(File.Exists(db.DatabasePath + "-shm"));
        Assert.Empty(Directory.GetFiles(db.RootDirectory, "*.restore-aside"));
    }

    [Fact]
    public void SwapIn_WhenItFailsAfterTheWalMovedAside_PutsTheWalAndShmBack()
    {
        using MigrationTestDatabase db = new();
        File.WriteAllText(db.DatabasePath, "old");
        File.WriteAllText(db.DatabasePath + "-wal", "old-wal");
        File.WriteAllText(db.DatabasePath + "-shm", "old-shm");
        string staged = db.DatabasePath + ".restore.tmp";
        File.WriteAllText(staged, "new");

        // The final move fails: the live file is held open without sharing.
        using (new FileStream(db.DatabasePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.ThrowsAny<Exception>(() => DatabaseCli.SwapIn(staged, db.DatabasePath));
        }

        Assert.Equal("old", File.ReadAllText(db.DatabasePath));
        Assert.Equal("old-wal", File.ReadAllText(db.DatabasePath + "-wal"));
        Assert.Equal("old-shm", File.ReadAllText(db.DatabasePath + "-shm"));
        Assert.Empty(Directory.GetFiles(db.RootDirectory, "*.restore-aside"));
    }

    [Fact]
    public void SafetyCopy_OfADamagedLiveFile_KeepsItsWalAndShmBesideTheRawCopy()
    {
        using MigrationTestDatabase db = new();
        byte[] damaged = Enumerable.Repeat((byte)0x41, 6000).ToArray();
        File.WriteAllBytes(db.DatabasePath, damaged);
        File.WriteAllText(db.DatabasePath + "-wal", "wal-bytes");
        File.WriteAllText(db.DatabasePath + "-shm", "shm-bytes");
        string safety = db.DatabasePath + ".pre-restore.test.bak";

        // SQLite deletes a stray -wal it can; held open (readable, not deletable) they survive its attempt.
        using (new FileStream(db.DatabasePath + "-wal", FileMode.Open, FileAccess.Read, FileShare.Read))
        using (new FileStream(db.DatabasePath + "-shm", FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            DatabaseCli.SafetyCopy(db.DatabasePath, safety);
        }

        Assert.Equal(damaged, File.ReadAllBytes(safety));
        Assert.Equal("wal-bytes", File.ReadAllText(safety + "-wal"));
        Assert.Equal("shm-bytes", File.ReadAllText(safety + "-shm"));
    }

    [Fact]
    public void Restore_WhenEvenTheRawSafetyCopyFails_AbortsAndLeavesTheLiveFileAlone()
    {
        using MigrationTestDatabase db = WithPlayer("alice");
        string source = BackupOf(db, "source.db");
        db.ExecuteScript("INSERT INTO player_profiles (player_name, display_name) VALUES ('bob', 'bob');");
        SqliteConnection.ClearAllPools();
        byte[] before = File.ReadAllBytes(db.DatabasePath);

        int exit;
        using (new FileStream(db.DatabasePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            (exit, _, _) = Run(db, "restore", source, "--yes");
        }

        Assert.Equal(DatabaseCli.ExitSourceOrTargetInvalid, exit);
        Assert.Empty(Directory.GetFiles(db.RootDirectory, "*.pre-restore.*"));
        Assert.False(File.Exists(db.DatabasePath + ".restore.tmp"));
        Assert.Equal(before, File.ReadAllBytes(db.DatabasePath));
    }

    [Fact]
    public void Restore_RefusesAZeroByteSource_AndAForeignSqliteFile()
    {
        using MigrationTestDatabase db = WithPlayer("alice");
        string empty = Path.Combine(db.RootDirectory, "empty.db");
        File.WriteAllBytes(empty, []);
        (int exit, _, string error) = Run(db, "restore", empty, "--yes");
        Assert.Equal(DatabaseCli.ExitSourceOrTargetInvalid, exit);
        Assert.Contains("not a Dream database", error, StringComparison.Ordinal);

        string foreign = Path.Combine(db.RootDirectory, "foreign.db");
        using (SqliteConnection connection = MigrationTestDatabase.OpenFile(foreign))
        {
            MigrationTestDatabase.Execute(connection, "CREATE TABLE notes (id INTEGER PRIMARY KEY, body TEXT); INSERT INTO notes (body) VALUES ('x');");
        }

        SqliteConnection.ClearAllPools();
        (exit, _, error) = Run(db, "restore", foreign, "--yes");
        Assert.Equal(DatabaseCli.ExitSourceOrTargetInvalid, exit);
        Assert.Contains("not a Dream database", error, StringComparison.Ordinal);

        Assert.Equal(1L, CountPlayers(db.DatabasePath, "alice"));
        Assert.Empty(Directory.GetFiles(db.RootDirectory, "*.pre-restore.*.bak"));
    }

    [Theory]
    [InlineData("real-legacy-v0.db")]
    [InlineData("real-legacy-v1.db")]
    public void Restore_AcceptsARealLegacyDatabase(string fixture)
    {
        using MigrationTestDatabase db = WithPlayer("alice");
        string source = Path.Combine(db.RootDirectory, fixture);
        File.Copy(
            Path.Combine(
                Dream.Tests.Integration.RepoRoot.Find(), "dream.Tests", "Server", "Persistence", "Migrations", "Fixtures", fixture),
            source);

        (int exit, _, string error) = Run(db, "restore", source, "--yes");

        Assert.True(exit == 0, error);
        Assert.Equal(0L, CountPlayers(db.DatabasePath, "alice"));
        // The next start migrates it as usual.
        using ServerPersistenceBundle bundle = ServerBootstrap.CreatePersistenceBundle(db.DatabasePath, MigrationTestDatabase.InitScriptPath);
    }

    // Header intact, pages overwritten: not "not a database" (2) but a damaged one (6).
    private static void DamagePages(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        Assert.True(bytes.Length > 8192);
        Array.Fill(bytes, (byte)0xFF, 4096, bytes.Length - 4096);
        File.WriteAllBytes(path, bytes);
    }

    [Fact]
    public void Restore_OfASourceWithGoodHeaderAndDamagedPages_ExitsWithTheIntegrityCode()
    {
        using MigrationTestDatabase db = WithPlayer("alice");
        string source = BackupOf(db, "source.db");
        DamagePages(source);

        (int exit, _, string error) = Run(db, "restore", source, "--yes");

        Assert.Equal(DatabaseCli.ExitIntegrity, exit);
        Assert.Equal(6, exit);
        Assert.Contains("integrity", error, StringComparison.Ordinal);
        Assert.Equal(1L, CountPlayers(db.DatabasePath, "alice"));
        Assert.Empty(Directory.GetFiles(db.RootDirectory, "*.pre-restore.*.bak"));
    }

    [Fact]
    public void Backup_OfADatabaseWithGoodHeaderAndDamagedPages_ExitsWithTheIntegrityCode()
    {
        using MigrationTestDatabase db = WithPlayer("alice");
        DamagePages(db.DatabasePath);
        string target = Backup(db);

        (int exit, _, _) = Run(db, "backup", target);

        Assert.Equal(DatabaseCli.ExitIntegrity, exit);
        Assert.False(File.Exists(target));
        Assert.False(File.Exists(target + ".partial"));
    }

    [Fact]
    public void Restore_IsRefused_WhileAStartHoldsTheMaintenanceLock()
    {
        using MigrationTestDatabase db = WithPlayer("alice");
        string source = BackupOf(db, "source.db");
        using DatabaseMaintenanceLock held = DatabaseMaintenanceLock.Acquire(db.DatabasePath, TimeSpan.Zero);

        (int exit, _, string error) = Run(db, "restore", source, "--yes");

        Assert.Equal(DatabaseCli.ExitWorldHeld, exit);
        Assert.Contains("maintenance.lock", error, StringComparison.Ordinal);
    }

    [Fact]
    public void ARestoreInProgress_BlocksAStart_UntilItIsDone()
    {
        using MigrationTestDatabase db = WithPlayer("alice");
        DatabaseMaintenanceLock restoring = DatabaseMaintenanceLock.Acquire(db.DatabasePath, TimeSpan.Zero);
        Task<ServerPersistenceBundle> start = Task.Run(
            () => ServerBootstrap.CreatePersistenceBundle(db.DatabasePath, MigrationTestDatabase.InitScriptPath));

        Assert.False(start.Wait(300));
        restoring.Dispose();
        using ServerPersistenceBundle bundle = start.GetAwaiter().GetResult();
        Assert.NotNull(bundle);
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
        Assert.Equal(DatabaseCli.ExitSourceOrTargetInvalid, exit);

        Assert.Equal(1L, db.Scalar("SELECT COUNT(*) FROM player_profiles WHERE player_name = 'alice';"));
        Assert.Empty(Directory.GetFiles(db.RootDirectory, "*.pre-restore.*.bak"));
        Assert.Equal(DatabaseCli.ExitSourceOrTargetInvalid, Run(db, "restore", Path.Combine(db.RootDirectory, "missing.db"), "--yes").Exit);
    }
}
