using Xunit;
using Dream.Server;
using Dream.Simulation.Content;
using Dream.Tests.Server.Persistence.Migrations;

namespace Dream.Tests.Server;

// The content read at server start: a bad file stops the start with its own exit code, and a
// good one logs the digest servers are compared by.
public sealed class StartupContentTests
{
    [Fact]
    public void AnInvalidContentFile_StopsTheStartWithCode8_NamingTheProblem()
    {
        List<string> log = [];
        using DedicatedServerHost? host = ServerBootstrap.TryCreateServerModeHost(
            () =>
            {
                ServerBootstrap.ReadStartupContent(() => ContentCatalog.Load("{ not json", "{}"), log.Add);
                throw new Xunit.Sdk.XunitException("the start went on past invalid content");
            },
            log.Add,
            out int exitCode);

        Assert.Null(host);
        Assert.Equal(ServerBootstrap.ContentInvalidExitCode, exitCode);
        Assert.Equal(8, exitCode);
        Assert.Contains("the game content is invalid", Assert.Single(log), StringComparison.Ordinal);
    }

    [Fact]
    public void AProductionStart_LogsTheContentDigest_BeforeTheWorldOpens()
    {
        using MigrationTestDatabase db = new();
        List<string> log = [];
        using (ServerBootstrap.CreatePersistenceBundle(db.DatabasePath, MigrationTestDatabase.InitScriptPath, log: log.Add))
        {
        }

        Assert.StartsWith($"server.content.digest digest={ContentCatalog.Shipped.ContentDigest} ", log[0], StringComparison.Ordinal);
    }
}
