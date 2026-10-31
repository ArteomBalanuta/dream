using System.Net;
using System.Net.Sockets;
using Microsoft.Data.Sqlite;
using Xunit;
using Dream.Client.Networking;
using Dream.Client.Startup;
using Dream.Server;
using Dream.Server.Hosting;
using Dream.Server.Persistence;
using Dream.Server.Persistence.Worlds;
using Dream.Server.Worlds;
using Dream.Simulation.Persistence;
using Dream.Simulation.World;
using Dream.Tests.Server.Persistence.Migrations;

namespace Dream.Tests.Server;

// Persistence wave F3: the per-world host lease. One holder per world on a database file; a held
// world is refused by a second server (exit code 7) and by the offline --worlds commands.
public sealed class WorldHostLeaseTests
{
    private static ServerPersistenceBundle Open(MigrationTestDatabase db, string? world = null) =>
        ServerBootstrap.CreatePersistenceBundle(
            db.DatabasePath,
            MigrationTestDatabase.InitScriptPath,
            start: world == null ? null : new ServerStartOptions(WorldSelector: world));

    [Fact]
    public void ALease_IsExclusivePerWorld_AndFreedOnDispose()
    {
        using MigrationTestDatabase db = new();
        string path = db.DatabasePath;

        using WorldHostLease first = WorldHostLease.Acquire(path, new WorldId(1));
        Assert.Null(WorldHostLease.TryAcquire(path, new WorldId(1)));
        Assert.True(WorldHostLease.IsHeld(path, new WorldId(1)));
        Assert.Throws<WorldHeldException>(() => WorldHostLease.Acquire(path, new WorldId(1)));

        using (WorldHostLease other = WorldHostLease.Acquire(path, new WorldId(2)))
        {
            Assert.NotEqual(first.LockPath, other.LockPath);
        }

        first.Dispose();
        first.Dispose();
        Assert.False(WorldHostLease.IsHeld(path, new WorldId(1)));
        using WorldHostLease again = WorldHostLease.Acquire(path, new WorldId(1));
        Assert.EndsWith(".world-1.lock", again.LockPath, StringComparison.Ordinal);
    }

    [Fact]
    public void ADatabaseThatIsNotAFile_TakesNoLease()
    {
        Assert.False(WorldHostLease.AppliesTo(":memory:"));
        Assert.False(WorldHostLease.AppliesTo("file:test?mode=memory&cache=shared"));
        Assert.False(WorldHostLease.AppliesTo(""));
        Assert.False(WorldHostLease.AppliesTo(null));
        Assert.True(WorldHostLease.AppliesTo(Path.Combine(Path.GetTempPath(), "x.db")));
    }

    [Fact]
    public void ABundle_HoldsItsWorld_UntilDisposed_AndASecondServerIsRefusedWithExitCode7()
    {
        using MigrationTestDatabase db = new();
        ServerPersistenceBundle first = Open(db);
        Assert.True(WorldHostLease.IsHeld(db.DatabasePath, first.WorldId));

        List<string> log = [];
        using DedicatedServerHost? second = ServerBootstrap.TryCreateServerModeHost(
            () => ServerBootstrap.CreateAuthoritativeHost(Open(db)), log.Add, out int exitCode);
        Assert.Null(second);
        Assert.Equal(ServerBootstrap.WorldHeldExitCode, exitCode);
        Assert.Equal(7, exitCode);
        string line = Assert.Single(log);
        Assert.Contains("already hosted by another process", line, StringComparison.Ordinal);
        Assert.Contains(".world-1.lock", line, StringComparison.Ordinal);

        first.Dispose();
        Assert.False(WorldHostLease.IsHeld(db.DatabasePath, first.WorldId));
        using ServerPersistenceBundle reopened = Open(db);
    }

    [Fact]
    public void TwoDifferentWorlds_OfOneDatabase_CanBeHostedAtOnce()
    {
        using MigrationTestDatabase db = new();
        using ServerPersistenceBundle one = Open(db);
        SqliteConnection.ClearAllPools();
        new SqliteWorldCatalog(db.Schema).Create(new WorldCreateOptions("second", 20));
        using ServerPersistenceBundle two = Open(db, "second");
        Assert.NotEqual(one.WorldId, two.WorldId);
    }

    [Fact]
    public void AHostThatFailsToStart_ReleasesTheLease()
    {
        using MigrationTestDatabase db = new();
        db.Schema.EnsureSchema();
        db.ExecuteScript(
            "INSERT INTO worlds (world_id, name, width, height, depth, map_source, generator_key, generator_version, seed, world_time_ticks, created_schema_version) " +
            "VALUES (2, 'stone', 56, 8, 56, 'stored', 'default', 1, '1', 0, 6);");
        // A stored world with no terrain chunks cannot be opened: the failure comes after the lease.
        List<string> log = [];
        using DedicatedServerHost? host = ServerBootstrap.TryCreateServerModeHost(
            () => ServerBootstrap.CreateAuthoritativeHost(Open(db, "stone")), log.Add, out int exitCode, "stone");
        Assert.Null(host);
        Assert.Equal(ServerBootstrap.WorldCannotBeOpenedExitCode, exitCode);
        Assert.False(WorldHostLease.IsHeld(db.DatabasePath, new WorldId(2)));
    }

    [Fact]
    public async Task AnIntegratedServerThatCannotBind_ReleasesTheLease()
    {
        using MigrationTestDatabase db = new();
        using TcpListener occupied = new(IPAddress.Loopback, 0);
        occupied.Start();
        int port = ((IPEndPoint)occupied.LocalEndpoint).Port;

        await using IntegratedLocalServerHandle handle = await IntegratedLocalServerHandle.StartAsync(
            preferredPort: port,
            hostFactory: () => ServerBootstrap.CreateAuthoritativeHost(Open(db)));

        Assert.False(handle.IsReady);
        Assert.NotNull(handle.ErrorMessage);
        Assert.False(WorldHostLease.IsHeld(db.DatabasePath, WorldId.Default));
    }

    [Fact]
    public async Task AnIntegratedServer_HoldsTheLeaseWhileRunning_AndReleasesItOnDispose()
    {
        using MigrationTestDatabase db = new();
        await using (IntegratedLocalServerHandle handle = await IntegratedLocalServerHandle.StartAsync(
            hostFactory: () => ServerBootstrap.CreateAuthoritativeHost(Open(db))))
        {
            Assert.True(handle.IsReady, handle.ErrorMessage);
            Assert.True(WorldHostLease.IsHeld(db.DatabasePath, WorldId.Default));
        }

        Assert.False(WorldHostLease.IsHeld(db.DatabasePath, WorldId.Default));
    }

    [Fact]
    public async Task ASecondIntegratedServer_OnAHeldWorld_ReportsNotReady()
    {
        using MigrationTestDatabase db = new();
        using ServerPersistenceBundle held = Open(db);

        await using IntegratedLocalServerHandle handle = await IntegratedLocalServerHandle.StartAsync(
            hostFactory: () => ServerBootstrap.CreateAuthoritativeHost(Open(db)));

        Assert.False(handle.IsReady);
        Assert.Contains("already in use", handle.ErrorMessage, StringComparison.Ordinal);
        Assert.True(WorldHostLease.IsHeld(db.DatabasePath, WorldId.Default));
    }

    // Two desktop clients on one database: the second one's local server stays off, the screen says
    // why, and Connect still goes out to the endpoint the player entered.
    [Theory]
    [InlineData("127.0.0.1", "5057")]
    [InlineData("play.example.com", "6000")]
    public async Task ASecondClient_WhoseWorldIsHeld_StaysUsableAndCanConnectElsewhere(string host, string port)
    {
        using MigrationTestDatabase db = new();
        using ServerPersistenceBundle held = Open(db);

        await using OnDemandLocalServerHandle local = new(() => IntegratedLocalServerHandle.StartAsync(
            hostFactory: () => ServerBootstrap.CreateAuthoritativeHost(Open(db))).AsTask().GetAwaiter().GetResult());
        RecordingConnector connector = new();
        NetworkSessionCoordinator coordinator = new(local, connector);
        ClientConnectionOptions configured = new(host, port, "second");

        ClientConnectionOptions initial = coordinator.ResolveInitialOptions(configured);

        Assert.Equal(configured, initial);
        if (host == "127.0.0.1")
        {
            Assert.False(local.IsReady);
            Assert.Contains("already in use by another Dream window", coordinator.LocalServerStatus, StringComparison.Ordinal);
            Assert.Contains("already in use by another Dream window", coordinator.StatusMessage, StringComparison.Ordinal);
        }
        else
        {
            Assert.False(local.IsStarted);
        }

        coordinator.BeginConnect(initial);
        await coordinator.WaitForPendingConnectionAsync();
        Assert.Equal((host, int.Parse(port)), connector.Target);
    }

    private sealed class RecordingConnector : IRemoteSessionConnector
    {
        public (string Host, int Port)? Target { get; private set; }

        public ValueTask<RemoteClientSession> ConnectAsync(RemoteClientOptions options, CancellationToken cancellationToken = default)
        {
            Target = (options.Host, options.Port);
            throw new IOException("no server in this test");
        }
    }

    // The offline --worlds commands, run with the production lease wiring against a temp database.
    private static (int Exit, string Out, string Err) Cli(MigrationTestDatabase db, params string[] args)
    {
        StringWriter output = new();
        StringWriter error = new();
        int exit = WorldCli.Run(
            args,
            () =>
            {
                db.Schema.EnsureSchema();
                return new SqliteWorldCatalog(db.Schema);
            },
            output,
            error,
            worldId => WorldHostLease.TryAcquire(db.DatabasePath, worldId));
        SqliteConnection.ClearAllPools();
        return (exit, output.ToString(), error.ToString());
    }

    [Theory]
    [InlineData("export", "1", "copy-of-default")]
    [InlineData("reset-terrain", "1", "--yes")]
    [InlineData("copy", "1", "copy-of-default")]
    [InlineData("delete", "1", "--yes")]
    public void TheOfflineWorldsCommands_RefuseAHeldWorld_AndChangeNothing(string verb, string a, string b)
    {
        using MigrationTestDatabase db = new();
        using ServerPersistenceBundle server = Open(db);
        SqliteConnection.ClearAllPools();
        long worldsBefore = (long)db.Scalar("SELECT COUNT(*) FROM worlds;")!;

        (int exit, string output, string error) = Cli(db, verb, a, b);

        Assert.Equal(WorldCli.ExitWorldHeld, exit);
        Assert.Equal(7, exit);
        Assert.Equal(string.Empty, output);
        Assert.Contains("in use", error, StringComparison.Ordinal);
        Assert.Equal(worldsBefore, (long)db.Scalar("SELECT COUNT(*) FROM worlds;")!);
        Assert.Equal(1L, (long)db.Scalar("SELECT COUNT(*) FROM worlds WHERE world_id = 1;")!);
    }

    [Fact]
    public void TheOfflineWorldsCommands_HoldTheLeaseWhileTheyRun_AndWorkOnAFreeWorld()
    {
        using MigrationTestDatabase db = new();
        using (Open(db))
        {
        }

        (int exit, string output, string error) = Cli(db, "copy", "1", "copy-of-default");
        Assert.True(exit == 0, error);
        Assert.StartsWith("2\tcopy-of-default\t", output, StringComparison.Ordinal);
        Assert.False(WorldHostLease.IsHeld(db.DatabasePath, WorldId.Default));

        (exit, _, error) = Cli(db, "delete", "2", "--yes");
        Assert.True(exit == 0, error);

        // A command on world 1 does not block a server on world 3.
        Cli(db, "create", "third");
        using ServerPersistenceBundle server = Open(db, "third");
        (exit, _, error) = Cli(db, "reset-terrain", "1", "--yes");
        Assert.True(exit == 0, error);
    }

    [Fact]
    public void AServerStart_IsRefused_WhileAnOfflineCommandHoldsTheWorld()
    {
        using MigrationTestDatabase db = new();
        using (Open(db))
        {
        }

        using WorldHostLease command = WorldHostLease.Acquire(db.DatabasePath, WorldId.Default);
        Assert.Throws<WorldHeldException>(() => Open(db));
    }

    private static string LegacyFixture(string name) => Path.Combine(
        Dream.Tests.Integration.RepoRoot.Find(), "dream.Tests", "Server", "Persistence", "Migrations", "Fixtures", name);

    [Fact]
    public void ARefusedStart_DoesNotMigrateADatabase_WhileAnotherProcessHoldsAWorld()
    {
        using MigrationTestDatabase db = new();
        File.Copy(LegacyFixture("real-legacy-v1.db"), db.DatabasePath);
        byte[] before = File.ReadAllBytes(db.DatabasePath);
        using WorldHostLease holder = WorldHostLease.Acquire(db.DatabasePath, WorldId.Default);

        WorldHeldException ex = Assert.Throws<WorldHeldException>(() => Open(db));

        Assert.Equal(WorldId.Default, ex.WorldId);
        SqliteConnection.ClearAllPools();
        Assert.Equal(before, File.ReadAllBytes(db.DatabasePath));
        Assert.Empty(db.Backups());
    }

    [Fact]
    public void AnOfflineWorldsCommand_DoesNotMigrateADatabase_WhileAWorldIsHeld()
    {
        using MigrationTestDatabase db = new();
        File.Copy(LegacyFixture("real-legacy-v1.db"), db.DatabasePath);
        byte[] before = File.ReadAllBytes(db.DatabasePath);
        using WorldHostLease holder = WorldHostLease.Acquire(db.DatabasePath, WorldId.Default);

        Assert.Throws<WorldHeldException>(
            () => DatabaseMaintenanceLock.EnsureSchemaGuarded(db.Schema, db.DatabasePath, log: null));

        SqliteConnection.ClearAllPools();
        Assert.Equal(before, File.ReadAllBytes(db.DatabasePath));
        // The maintenance lock was released on the way out.
        using DatabaseMaintenanceLock free = DatabaseMaintenanceLock.Acquire(db.DatabasePath, TimeSpan.Zero);
    }

    [Fact]
    public void AStart_ThatNeedsNoMigration_ProceedsWhileAnotherWorldIsHeld()
    {
        using MigrationTestDatabase db = new();
        using ServerPersistenceBundle one = Open(db);
        SqliteConnection.ClearAllPools();
        new SqliteWorldCatalog(db.Schema).Create(new WorldCreateOptions("second", 20));
        using ServerPersistenceBundle two = Open(db, "second");
        Assert.NotEqual(one.WorldId, two.WorldId);
    }

    [Fact]
    public void AStart_ReadsTheWorldRecordAfterTakingTheLease()
    {
        using MigrationTestDatabase db = new();
        using (Open(db))
        {
        }

        SqliteConnection.ClearAllPools();
        List<WorldId> asked = [];
        bool heldDuringRead = false;
        RecordingCatalog catalog = new(new SqliteWorldCatalog(db.Schema), asked)
        {
            OnTryGet = (id, stored) =>
            {
                heldDuringRead = WorldHostLease.IsHeld(db.DatabasePath, id);
                return stored! with { WorldTimeTicks = 987_654_321L };
            }
        };
        using ServerPersistenceBundle bundle = ServerBootstrap.CreatePersistenceBundle(
            db.DatabasePath, MigrationTestDatabase.InitScriptPath, catalog: catalog);

        Assert.Equal([WorldId.Default], asked);
        Assert.True(heldDuringRead);
        Assert.Equal(987_654_321L, bundle.WorldTimeTicks);
    }

    [Fact]
    public void AStart_WhoseWorldVanishesBetweenResolveAndLease_ThrowsWorldNotFound_AndReleasesTheLease()
    {
        using MigrationTestDatabase db = new();
        using (Open(db))
        {
        }

        SqliteConnection.ClearAllPools();
        RecordingCatalog catalog = new(new SqliteWorldCatalog(db.Schema), [])
        {
            OnTryGet = (_, _) => null
        };

        WorldNotFoundException ex = Assert.Throws<WorldNotFoundException>(() => ServerBootstrap.CreatePersistenceBundle(
            db.DatabasePath, MigrationTestDatabase.InitScriptPath, catalog: catalog));

        Assert.Equal(WorldId.Default.ToString(), ex.Selector);
        Assert.False(WorldHostLease.IsHeld(db.DatabasePath, WorldId.Default));

        List<string> log = [];
        using DedicatedServerHost? host = ServerBootstrap.TryCreateServerModeHost(
            () => ServerBootstrap.CreateAuthoritativeHost(ServerBootstrap.CreatePersistenceBundle(
                db.DatabasePath, MigrationTestDatabase.InitScriptPath, catalog: catalog)),
            log.Add,
            out int exitCode);
        Assert.Null(host);
        Assert.Equal(ServerBootstrap.WorldNotFoundExitCode, exitCode);
    }

    [Fact]
    public void AnIntegratedStart_GivesUpQuicklyOnABusyDatabase_WhileTheDedicatedWaitIsLong()
    {
        Assert.True(DatabaseMaintenanceLock.IntegratedStartWait <= TimeSpan.FromSeconds(1));
        Assert.True(DatabaseMaintenanceLock.StartWait >= TimeSpan.FromSeconds(10));

        using MigrationTestDatabase db = new();
        using (Open(db))
        {
        }

        SqliteConnection.ClearAllPools();
        using DatabaseMaintenanceLock restoring = DatabaseMaintenanceLock.Acquire(db.DatabasePath, TimeSpan.Zero);
        long started = System.Diagnostics.Stopwatch.GetTimestamp();

        Assert.Throws<DatabaseBusyException>(() => ServerBootstrap.CreatePersistenceBundle(
            db.DatabasePath,
            MigrationTestDatabase.InitScriptPath,
            start: new ServerStartOptions(MaintenanceWait: DatabaseMaintenanceLock.IntegratedStartWait)));

        Assert.True(System.Diagnostics.Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void AServerStart_OnABusyDatabase_ExitsWithTheWorldHeldCode()
    {
        using MigrationTestDatabase db = new();
        using (Open(db))
        {
        }

        SqliteConnection.ClearAllPools();
        using DatabaseMaintenanceLock restoring = DatabaseMaintenanceLock.Acquire(db.DatabasePath, TimeSpan.Zero);
        List<string> log = [];

        using DedicatedServerHost? host = ServerBootstrap.TryCreateServerModeHost(
            () => ServerBootstrap.CreateAuthoritativeHost(ServerBootstrap.CreatePersistenceBundle(
                db.DatabasePath,
                MigrationTestDatabase.InitScriptPath,
                start: new ServerStartOptions(MaintenanceWait: TimeSpan.Zero))),
            log.Add,
            out int exitCode);

        Assert.Null(host);
        Assert.Equal(ServerBootstrap.WorldHeldExitCode, exitCode);
    }

    [Fact]
    public void AWorldsCommand_OnABusyDatabase_ExitsWithTheWorldHeldCode()
    {
        StringWriter error = new();
        int exit = WorldCli.Run(
            ["list"],
            () => throw new DatabaseBusyException("db.maintenance.lock"),
            new StringWriter(),
            error);

        Assert.Equal(WorldCli.ExitWorldHeld, exit);
        Assert.Contains("Try again", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheMaintenanceLock_WhoseDirectoryCannotBeCreated_IsALockFileError()
    {
        using MigrationTestDatabase db = new();
        string blocker = Path.Combine(db.RootDirectory, "not-a-directory");
        File.WriteAllText(blocker, "x");

        Assert.Throws<LockFileUnavailableException>(
            () => DatabaseMaintenanceLock.TryAcquire(Path.Combine(blocker, "world.db"), TimeSpan.Zero));
    }

    private sealed class RecordingCatalog(IWorldCatalog inner, List<WorldId> asked) : IWorldCatalog
    {
        // Sees the id and the stored record, and returns what TryGet reports.
        public Func<WorldId, WorldRecord?, WorldRecord?>? OnTryGet { get; init; }

        public IReadOnlyList<WorldRecord> List() => inner.List();

        public WorldRecord? TryGet(WorldId id)
        {
            asked.Add(id);
            WorldRecord? stored = inner.TryGet(id);
            return OnTryGet == null ? stored : OnTryGet(id, stored);
        }

        public WorldRecord? TryGetByName(string name) => inner.TryGetByName(name);

        public WorldRecord Create(WorldCreateOptions options) => inner.Create(options);

        public WorldRecord Copy(WorldId source, string newName) => inner.Copy(source, newName);

        public bool Delete(WorldId id) => inner.Delete(id);

        public WorldTerrainSource ReadTerrainSource(WorldId id) => inner.ReadTerrainSource(id);

        public WorldRecord CreateStored(WorldId source, string newName, IReadOnlyList<PersistedTerrainChunk> chunks, bool terrainOnly) =>
            inner.CreateStored(source, newName, chunks, terrainOnly);

        public TerrainResetResult ResetTerrain(WorldId id) => inner.ResetTerrain(id);

        public WorldRecord Resolve(WorldSelector selector, int newWorldSize, out bool created) =>
            inner.Resolve(selector, newWorldSize, out created);
    }

    // A lock file that cannot be opened for a reason other than a live holder is a clear error, not
    // a "held" world and not a stack trace.
    private static void MakeReadOnlyLockFile(MigrationTestDatabase db, WorldId world)
    {
        string path = WorldHostLease.GetLockPath(db.DatabasePath, world);
        File.WriteAllText(path, string.Empty);
        File.SetAttributes(path, FileAttributes.ReadOnly);
    }

    [Fact]
    public void AReadOnlyLockFile_IsNotReportedAsHeld()
    {
        using MigrationTestDatabase db = new();
        MakeReadOnlyLockFile(db, WorldId.Default);
        try
        {
            LockFileUnavailableException ex = Assert.Throws<LockFileUnavailableException>(
                () => WorldHostLease.TryAcquire(db.DatabasePath, WorldId.Default));
            Assert.Contains(".world-1.lock", ex.Message, StringComparison.Ordinal);
            Assert.Throws<LockFileUnavailableException>(() => WorldHostLease.IsHeld(db.DatabasePath, WorldId.Default));
        }
        finally
        {
            File.SetAttributes(WorldHostLease.GetLockPath(db.DatabasePath, WorldId.Default), FileAttributes.Normal);
        }
    }

    [Fact]
    public void AReadOnlyLockFile_StopsAServerStartWithExitCode5_AndAWorldsCommandWithExitCode2()
    {
        using MigrationTestDatabase db = new();
        using (Open(db))
        {
        }

        SqliteConnection.ClearAllPools();
        MakeReadOnlyLockFile(db, WorldId.Default);
        try
        {
            List<string> log = [];
            using DedicatedServerHost? host = ServerBootstrap.TryCreateServerModeHost(
                () => ServerBootstrap.CreateAuthoritativeHost(Open(db)), log.Add, out int exitCode);
            Assert.Null(host);
            Assert.Equal(5, exitCode);
            Assert.Contains("cannot create the lock file", Assert.Single(log), StringComparison.Ordinal);

            (int exit, _, string error) = Cli(db, "delete", "1", "--yes");
            Assert.Equal(WorldCli.ExitNotFoundOrConflict, exit);
            Assert.Contains("lock file", error, StringComparison.Ordinal);
            Assert.DoesNotContain("   at ", error, StringComparison.Ordinal);
        }
        finally
        {
            File.SetAttributes(WorldHostLease.GetLockPath(db.DatabasePath, WorldId.Default), FileAttributes.Normal);
        }
    }

    [Fact]
    public void AWorldsCommand_OnADamagedDatabase_ExitsCleanlyInsteadOfThrowing()
    {
        using MigrationTestDatabase db = new();
        File.WriteAllBytes(db.DatabasePath, Enumerable.Repeat((byte)0x41, 8192).ToArray());
        StringWriter error = new();

        int exit = WorldCli.Run(
            ["list"], () => new SqliteWorldCatalog(db.Schema), new StringWriter(), error);
        SqliteConnection.ClearAllPools();

        Assert.Equal(WorldCli.ExitNotFoundOrConflict, exit);
        Assert.Contains("--db restore", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheOfflineWorldsCommands_ReallyHoldTheLease_DuringTheCommand()
    {
        using MigrationTestDatabase db = new();
        using (Open(db))
        {
        }

        List<bool> heldAtRelease = [];
        StringWriter output = new();
        int exit = WorldCli.Run(
            ["copy", "1", "copy-of-default"],
            () =>
            {
                db.Schema.EnsureSchema();
                return new SqliteWorldCatalog(db.Schema);
            },
            output,
            new StringWriter(),
            worldId => new ReleaseProbe(
                WorldHostLease.TryAcquire(db.DatabasePath, worldId)!,
                () => heldAtRelease.Add(WorldHostLease.IsHeld(db.DatabasePath, worldId))));
        SqliteConnection.ClearAllPools();

        Assert.Equal(0, exit);
        Assert.StartsWith("2\tcopy-of-default\t", output.ToString(), StringComparison.Ordinal);
        Assert.Equal([true], heldAtRelease);
        Assert.False(WorldHostLease.IsHeld(db.DatabasePath, WorldId.Default));
    }

    private sealed class ReleaseProbe(WorldHostLease lease, Action beforeRelease) : IDisposable
    {
        public void Dispose()
        {
            beforeRelease();
            lease.Dispose();
        }
    }
}
