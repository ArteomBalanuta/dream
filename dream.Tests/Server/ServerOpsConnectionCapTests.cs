using Xunit;
using Dream.Server;

namespace Dream.Tests.Server;

// M8 fix loop. A player cap at or above the connection cap would refuse sockets before it could refuse
// players (and a full server would lock its own players out of reconnecting), so the dedicated server's
// connection cap is derived from DREAM_MAX_PLAYERS unless DREAM_MAX_CONNECTIONS was set.
public sealed class ServerOpsConnectionCapTests
{
    [Fact]
    public void ThePlayerCapAtTheDefaultConnectionCap_RaisesTheConnectionCap_WithHeadroom_AndSaysSo()
    {
        List<string> log = [];

        ServerConnectionOptions options = ServerConnectionOptions.Default.WithPlayerCapHeadroom(
            ServerConnectionOptions.DefaultMaxConnections, null, log.Add);

        Assert.Equal(ServerConnectionOptions.DefaultMaxConnections + ServerConnectionOptions.PlayerCapHeadroom, options.MaxConnections);
        Assert.Equal(8, ServerConnectionOptions.PlayerCapHeadroom);
        string line = Assert.Single(log);
        Assert.Contains((ServerConnectionOptions.DefaultMaxConnections + 8).ToString(), line, StringComparison.Ordinal);
    }

    [Fact]
    public void APlayerCapAboveTheDefault_GetsPlayersPlusHeadroom()
    {
        ServerConnectionOptions options = ServerConnectionOptions.Default.WithPlayerCapHeadroom(200, "   ", null);

        Assert.Equal(208, options.MaxConnections);
    }

    [Fact]
    public void AnExplicitConnectionCap_IsKept_WithAWarningAndTheEffectiveNumber()
    {
        List<string> log = [];
        ServerConnectionOptions explicitCap = ServerConnectionOptions.Default.WithMaxConnectionsOverride("50");

        ServerConnectionOptions options = explicitCap.WithPlayerCapHeadroom(100, "50", log.Add);

        Assert.Equal(50, options.MaxConnections);
        string line = Assert.Single(log);
        Assert.Contains("Warning", line, StringComparison.Ordinal);
        Assert.Contains("50", line, StringComparison.Ordinal);
        Assert.Contains("DREAM_MAX_CONNECTIONS", line, StringComparison.Ordinal);
    }

    [Fact]
    public void APlayerCapWellBelowTheConnectionCap_ChangesNothing_AndLogsNothing()
    {
        List<string> log = [];

        ServerConnectionOptions options = ServerConnectionOptions.Default.WithPlayerCapHeadroom(10, null, log.Add);

        Assert.Equal(ServerConnectionOptions.DefaultMaxConnections, options.MaxConnections);
        Assert.Empty(log);
    }

    [Fact]
    public void NoPlayerCap_OrNoConnectionCap_ChangesNothing()
    {
        Assert.Equal(
            ServerConnectionOptions.DefaultMaxConnections,
            ServerConnectionOptions.Default.WithPlayerCapHeadroom(0, null, null).MaxConnections);
        ServerConnectionOptions off = ServerConnectionOptions.Default with { MaxConnections = 0 };
        Assert.Equal(0, off.WithPlayerCapHeadroom(500, null, null).MaxConnections);
    }

    [Fact]
    public void TheBootstrap_AppliesTheHeadroom_AndTheDocsSayWhat()
    {
        string root = Dream.Tests.Integration.RepoRoot.Find();
        string bootstrap = File.ReadAllText(Path.Combine(root, "dream", "Server", "ServerBootstrap.cs"));
        string docs = File.ReadAllText(Path.Combine(root, "docs", "configuration.md"));

        Assert.Contains(".WithPlayerCapHeadroom(", bootstrap, StringComparison.Ordinal);
        Assert.Contains("`DREAM_MAX_PLAYERS` + 8", docs, StringComparison.Ordinal);
    }
}
