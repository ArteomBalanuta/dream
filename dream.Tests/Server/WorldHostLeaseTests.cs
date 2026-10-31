using System.Net;
using System.Net.Sockets;
using Microsoft.Data.Sqlite;
using Xunit;
using Dream.Client.Startup;
using Dream.Server;
using Dream.Server.Hosting;
using Dream.Server.Persistence;
using Dream.Server.Persistence.Worlds;
using Dream.Server.Worlds;
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
}
