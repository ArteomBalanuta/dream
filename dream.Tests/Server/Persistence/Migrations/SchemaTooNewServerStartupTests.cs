using Xunit;
using Dream.Server;
using Dream.Server.Persistence.Migrations;

namespace Dream.Tests.Server.Persistence.Migrations;

// The dedicated server on a database a newer build migrated: it logs why and exits with
// SchemaTooNewExitCode instead of crashing on an unhandled SchemaTooNewException.
public sealed class SchemaTooNewServerStartupTests
{
    [Fact]
    public void ServerMode_OnADatabaseFromANewerBuild_LogsAndReturnsTheExitCode()
    {
        using MigrationTestDatabase database = new();
        database.Schema.EnsureSchema();
        database.ExecuteScript("INSERT INTO schema_version (version, name, applied_utc) VALUES (99, 'from_the_future', '2030-01-01T00:00:00.0000000Z');");
        List<string> log = [];

        DedicatedServerHost? host = ServerBootstrap.TryCreateServerModeHost(
            () => ServerBootstrap.CreateAuthoritativeHost(
                ServerBootstrap.CreatePersistenceBundle(database.DatabasePath, MigrationTestDatabase.InitScriptPath)),
            log.Add,
            out int exitCode);

        Assert.Null(host);
        Assert.Equal(ServerBootstrap.SchemaTooNewExitCode, exitCode);
        Assert.NotEqual(0, exitCode);
        string line = Assert.Single(log);
        Assert.Contains("version 99", line, StringComparison.Ordinal);
        Assert.Contains($"version {MigrationTestDatabase.ShippedLatestVersion} ", line, StringComparison.Ordinal);
    }

    [Fact]
    public void ServerMode_OtherStartupFailures_StillThrow()
    {
        Assert.Throws<InvalidOperationException>(() => ServerBootstrap.TryCreateServerModeHost(
            () => throw new InvalidOperationException("not a schema problem"),
            _ => { },
            out _));
    }
}
