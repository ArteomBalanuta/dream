using Xunit;
using Dream.Server.Accounts;
using Dream.Server.Persistence.Repositories;
using Dream.Simulation.Characters;
using Dream.Tests.Support;

namespace Dream.Tests.Server;

// M8. `--accounts grant-admin | revoke-admin | unban <name>` over a real SQLite database with a real
// account created by a real join. The CLI takes no world lease, so it runs beside a running host, as it
// does against a running server.
public sealed class AdminAccountCliTests
{
    private static int Run(AccountHostHarness harness, out string output, out string error, params string[] args)
    {
        StringWriter stdout = new();
        StringWriter stderr = new();
        int code = AccountCli.Run(args, () => harness.Database.Schema, stdout, stderr);
        output = stdout.ToString();
        error = stderr.ToString();
        return code;
    }

    private static AccountStanding StandingOf(AccountHostHarness harness, string name) =>
        harness.Sqlite!.Lookup(name, TestAccountTokens.KeyFor(name)).Standing;

    [Fact]
    public void GrantAdmin_ThenRevoke_ChangesTheRoleOfTheOwningAccount()
    {
        using AccountHostHarness harness = AccountHostHarness.OverSqlite();
        harness.JoinAccepted("c1", "s1", "Warden");
        Assert.Equal(0, StandingOf(harness, "Warden").Role);

        Assert.Equal(AccountCli.ExitOk, Run(harness, out string output, out _, "grant-admin", "warden"));
        Assert.Equal(1, StandingOf(harness, "Warden").Role);
        Assert.StartsWith("grant-admin\t", output, StringComparison.Ordinal);

        Assert.Equal(AccountCli.ExitOk, Run(harness, out _, out _, "revoke-admin", "WARDEN"));
        Assert.Equal(0, StandingOf(harness, "Warden").Role);
    }

    [Fact]
    public void Unban_ClearsTheBanAndTheReason()
    {
        using AccountHostHarness harness = AccountHostHarness.OverSqlite();
        harness.JoinAccepted("c1", "s1", "Rowdy");
        Assert.True(harness.Sqlite!.TrySetBan("Rowdy", DateTime.UtcNow.AddDays(3), "loud"));
        AccountStanding banned = StandingOf(harness, "Rowdy");
        Assert.True(BanMessage.IsActive(banned, DateTime.UtcNow));
        Assert.Equal("loud", banned.BanReason);

        Assert.Equal(AccountCli.ExitOk, Run(harness, out _, out _, "unban", "Rowdy"));

        AccountStanding cleared = StandingOf(harness, "Rowdy");
        Assert.Null(cleared.BannedUntilUtc);
        Assert.Null(cleared.BanReason);
    }

    [Fact]
    public void AnUnknownName_IsNotFound_AndChangesNothing()
    {
        using AccountHostHarness harness = AccountHostHarness.OverSqlite();
        harness.JoinAccepted("c1", "s1", "Someone");

        Assert.Equal(AccountCli.ExitNotFound, Run(harness, out string output, out string error, "grant-admin", "Nobody Here"));

        Assert.Equal(string.Empty, output);
        Assert.Contains("Nobody Here", error, StringComparison.Ordinal);
        Assert.Equal(0, StandingOf(harness, "Someone").Role);
    }

    [Theory]
    [InlineData]
    [InlineData("grant-admin")]
    [InlineData("grant-admin", "a", "b")]
    [InlineData("promote", "a")]
    [InlineData("unban", " ")]
    public void BadUsage_IsRefused_WithTheUsageLine(params string[] args)
    {
        using AccountHostHarness harness = AccountHostHarness.OverSqlite();

        Assert.Equal(AccountCli.ExitUsage, Run(harness, out _, out string error, args));

        Assert.Contains("Usage: --accounts", error, StringComparison.Ordinal);
    }

    [Fact]
    public void NoCommandPrintsATokenOrItsHash()
    {
        using AccountHostHarness harness = AccountHostHarness.OverSqlite();
        harness.JoinAccepted("c1", "s1", "Quiet");

        Run(harness, out string output, out string error, "grant-admin", "Quiet");

        string token = TestAccountTokens.For("Quiet");
        string hash = TestAccountTokens.KeyFor("Quiet").HashHex;
        Assert.DoesNotContain(token, output + error, StringComparison.Ordinal);
        Assert.DoesNotContain(hash, output + error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheRole_IsReadAtJoin_NotFromAnythingTheClientSends()
    {
        using AccountHostHarness harness = AccountHostHarness.OverSqlite();
        harness.JoinAccepted("c1", "s1", "Pretender");
        harness.Host.TryGetConnectionContext("c1", out Dream.Multiplayer.Protocol.ServerRoutingContext? context);
        Assert.False(context!.IsAdmin);

        Run(harness, out _, out _, "grant-admin", "Pretender");

        // Granted in the database; the connection that joined before keeps what it joined with.
        Assert.False(context.IsAdmin);
        harness.Leave("c1");
        harness.JoinAccepted("c2", "s2", "Pretender");
        harness.Host.TryGetConnectionContext("c2", out context);
        Assert.True(context!.IsAdmin);
    }
}
