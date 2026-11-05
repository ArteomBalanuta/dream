using Microsoft.Data.Sqlite;
using Xunit;
using Dream.Client.Networking;
using Dream.Multiplayer.Protocol;
using Dream.Server;
using Dream.Server.Persistence;
using Dream.Server.Persistence.Repositories;
using Dream.Simulation.Characters;
using Dream.Simulation.Persistence;
using Dream.Tests.Server.Persistence.Migrations;
using Dream.Tests.Support;
using Dream.Tests.Utilities;

namespace Dream.Tests.Integration;

// M8 fix loop. Sanctions are per account: banning or muting a character reaches every connection of its
// account (an alt of an online player), the administrator guard looks at the account's role (an offline alt
// of an admin), the admin's reply follows the committed write, the roles and sanctions of connected accounts
// are read again on a slow cadence, and a full server does not lock a player out of their own seat. Real
// DedicatedServerService over loopback TCP and real SQLite, like M8EndToEndLoopbackTests.
public sealed class AdminAccountSanctionLoopbackTests
{
    private const string Admin = "m8a-adm";
    private const string Main = "m8a-main";
    private const string Alt = "m8a-alt";

    private static string NameOf(string session) => $"Name {session}";

    private static string TokenOf(string session) => TestAccountTokens.For(NameOf(session));

    private static async Task<WireLoopbackServer> StartAsync(MigrationTestDatabase db, int maxPlayers = 0, Func<IAccountRepository, IAccountRepository>? wrap = null)
    {
        ServerPersistenceBundle bundle = ServerBootstrap.CreatePersistenceBundle(
            db.DatabasePath, MigrationTestDatabase.InitScriptPath, start: new ServerStartOptions(null));
        if (wrap != null)
        {
            bundle = AccountHostHarness.WithAccounts(bundle, wrap(bundle.Accounts!));
        }

        DedicatedServerHost host = ServerBootstrap.CreateAuthoritativeHost(bundle);
        host.MaxPlayers = maxPlayers;
        return await WireLoopbackServer.StartAsync(host);
    }

    private static void Flush(WireLoopbackServer server) =>
        Assert.True(server.Runtime.PersistenceWrites!.Flush(TimeSpan.FromSeconds(30)));

    private static async Task GrantAdminAsync(WireLoopbackServer server, MigrationTestDatabase db, string session)
    {
        WireLoopbackClient first = await server.ConnectAndJoinAsync(session);
        await server.DisconnectAsync(first);
        Flush(server);
        Assert.True(new SqliteAccountRepository(db.Schema).TrySetRole(NameOf(session), 1));
    }

    // The alt is a second character of the main's account: it joins with the main's token, then leaves.
    private static async Task CreateAltAsync(WireLoopbackServer server, string alt, string ofMain)
    {
        WireLoopbackClient client = await server.ConnectAndJoinAsync(alt, asName: NameOf(alt), withTokenOf: NameOf(ofMain));
        await server.DisconnectAsync(client);
        Flush(server);
    }

    // A client the harness does not track, for a player the server is going to close.
    private static async Task<WireLoopbackClient> JoinApartAsync(WireLoopbackServer server, string session)
    {
        Task<ServerConnection> accept = server.Service.AcceptOnThreadPoolAsync();
        WireLoopbackClient client = await WireLoopbackClient.ConnectAsync(server.Port, session);
        ServerConnection connection = await accept.WaitAsync(WireLoopbackServer.Timeout);
        await client.HandshakeAsync();
        DateTime deadline = DateTime.UtcNow + WireLoopbackServer.Timeout;
        while (!server.Host.IsConnectionJoined(connection.ConnectionId))
        {
            Assert.True(DateTime.UtcNow < deadline, $"{session} never joined");
            await Task.Delay(5);
            await server.TickAsync();
            Assert.True(await OutboundDrain.FlushAsync(connection, WireLoopbackServer.Timeout));
        }

        await client.WaitForJoinAcceptedAsync();
        return client;
    }

    private static async Task UntilAsync(WireLoopbackServer server, Func<bool> done, string what)
    {
        DateTime deadline = DateTime.UtcNow + WireLoopbackServer.Timeout;
        while (!done())
        {
            Assert.True(DateTime.UtcNow < deadline, $"never saw {what}");
            await server.TickAsync();
            await server.FlushAllAsync();
            await Task.Delay(2);
        }
    }

    private static string[] SystemLines(WireLoopbackClient client) =>
        [.. client.Frames.Select(frame => frame.Decoded.Message).OfType<ServerChatMessage>()
            .Where(chat => chat.Channel == ChatChannel.System).Select(chat => chat.Text)];

    private static JoinRejectedMessage? RejectionOf(WireLoopbackClient client) =>
        client.Frames.Select(frame => frame.Decoded.Message).OfType<JoinRejectedMessage>().FirstOrDefault();

    private static Task CommandAsync(WireLoopbackClient client, string text) =>
        client.SendAsync(new ClientChatMessage(ChatChannel.Command, text));

    [Fact]
    public async Task Admin_BanOfAnAlt_ClosesTheMainThatIsOnline_AndTheReplyFollowsTheCommit()
    {
        using MigrationTestDatabase db = new();
        await using WireLoopbackServer server = await StartAsync(db);
        await GrantAdminAsync(server, db, Admin);
        await CreateAltAsync(server, Alt, Main);
        await using WireLoopbackClient admin = await server.ConnectAndJoinAsync(Admin);
        await using WireLoopbackClient main = await JoinApartAsync(server, Main);

        await CommandAsync(admin, $"/ban \"{NameOf(Alt)}\" 60 alting");
        await UntilAsync(server, () => SystemLines(admin).Contains($"Banned {NameOf(Alt)}."), "the ban result");

        // The reply was sent behind the write, so the ban is already in the database.
        Assert.Equal(1L, Convert.ToInt64(db.Scalar("SELECT COUNT(*) FROM accounts WHERE banned_until_utc IS NOT NULL;")));
        await UntilAsync(server, () => RejectionOf(main) != null, "the ban notice at the main");
        Assert.Contains("banned", RejectionOf(main)!.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("alting", RejectionOf(main)!.Reason, StringComparison.Ordinal);

        // Both characters of the account are refused now.
        JoinRejectedMessage mainRefused = await server.ConnectAndGetJoinRejectionAsync(Main, NameOf(Main), TokenOf(Main));
        Assert.StartsWith(JoinRejectionReasons.Banned, mainRefused.Reason, StringComparison.Ordinal);
        JoinRejectedMessage altRefused = await server.ConnectAndGetJoinRejectionAsync(Alt, NameOf(Alt), TokenOf(Main));
        Assert.StartsWith(JoinRejectionReasons.Banned, altRefused.Reason, StringComparison.Ordinal);

        // Lifting it through the alt frees the account.
        await CommandAsync(admin, $"/unban \"{NameOf(Alt)}\"");
        await UntilAsync(server, () => SystemLines(admin).Contains($"Unbanned {NameOf(Alt)}."), "the unban result");
        await using WireLoopbackClient back = await server.ConnectAndJoinAsync(Main);
        Assert.Null(RejectionOf(back));
    }

    [Fact]
    public async Task Admin_MuteOfAnAlt_SilencesTheMainThatIsOnline_UntilUnmuted()
    {
        using MigrationTestDatabase db = new();
        await using WireLoopbackServer server = await StartAsync(db);
        await GrantAdminAsync(server, db, Admin);
        await CreateAltAsync(server, Alt, Main);
        await using WireLoopbackClient admin = await server.ConnectAndJoinAsync(Admin);
        await using WireLoopbackClient main = await server.ConnectAndJoinAsync(Main);

        await CommandAsync(admin, $"/mute \"{NameOf(Alt)}\"");
        await UntilAsync(server, () => SystemLines(admin).Contains($"Muted {NameOf(Alt)}."), "the mute result");
        await UntilAsync(
            server,
            () => server.Host.TryGetConnectionContext(server.ConnectionFor(main).ConnectionId, out ServerRoutingContext? context) && context!.Standing.MutedUntilUtc != null,
            "the mute on the main's connection");

        await main.SendAsync(new ClientChatMessage(ChatChannel.World, "hello from the main"));
        await UntilAsync(server, () => SystemLines(main).Contains(DedicatedServerHost.MutedText), "the muted notice");

        await CommandAsync(admin, $"/unmute \"{NameOf(Alt)}\"");
        await UntilAsync(server, () => SystemLines(admin).Contains($"Unmuted {NameOf(Alt)}."), "the unmute result");
        await UntilAsync(
            server,
            () => server.Host.TryGetConnectionContext(server.ConnectionFor(main).ConnectionId, out ServerRoutingContext? context) && context!.Standing.MutedUntilUtc == null,
            "the unmute on the main's connection");
    }

    [Fact]
    public async Task Admin_TheGuard_LooksAtTheAccountsRole_ForAnOfflineAlt_AndTheAdminsOwnAlt()
    {
        using MigrationTestDatabase db = new();
        await using WireLoopbackServer server = await StartAsync(db);
        await GrantAdminAsync(server, db, Admin);
        await GrantAdminAsync(server, db, "m8a-adm2");
        await CreateAltAsync(server, "m8a-adm-alt", Admin);
        await using WireLoopbackClient other = await server.ConnectAndJoinAsync("m8a-adm2");

        await CommandAsync(other, $"/ban \"{NameOf("m8a-adm-alt")}\" 5");
        await CommandAsync(other, $"/mute \"{NameOf("m8a-adm-alt")}\" 5");
        await UntilAsync(
            server,
            () => SystemLines(other).Count(line => line == "That player is an administrator.") >= 2,
            "two refusals for the admin's offline alt");

        // Their own alt is themselves.
        await CommandAsync(other, $"/ban \"{NameOf("m8a-adm2")}\" 5");
        await UntilAsync(server, () => SystemLines(other).Contains("You cannot do that to yourself."), "the self refusal");

        Flush(server);
        Assert.Equal(0L, Convert.ToInt64(db.Scalar("SELECT COUNT(*) FROM accounts WHERE banned_until_utc IS NOT NULL OR muted_until_utc IS NOT NULL;")));
    }

    [Fact]
    public async Task Standing_IsReadAgainForConnectedAccounts_RoleRevoked_RemoteBanClosed_RemoteMuteApplied()
    {
        using MigrationTestDatabase db = new();
        await using WireLoopbackServer server = await StartAsync(db);
        server.Host.StandingRefreshIntervalTicks = 4;
        await GrantAdminAsync(server, db, Admin);
        await using WireLoopbackClient admin = await server.ConnectAndJoinAsync(Admin);
        await using WireLoopbackClient bee = await JoinApartAsync(server, "m8a-bee");
        await using WireLoopbackClient cee = await server.ConnectAndJoinAsync("m8a-cee");
        SqliteAccountRepository accounts = new(db.Schema);
        Assert.True(server.Host.TryGetConnectionContext(server.ConnectionFor(admin).ConnectionId, out ServerRoutingContext? adminContext));
        Assert.True(adminContext!.IsAdmin);

        // The joined accounts' rows are written by the persistence queue; wait for them to exist.
        Flush(server);
        // Another process changes the database: the admin is demoted, one player banned, another muted.
        Assert.True(accounts.TrySetRole(NameOf(Admin), 0));
        Assert.True(accounts.TrySetBan(NameOf("m8a-bee"), DateTime.UtcNow.AddMinutes(30), "banned elsewhere"));
        Assert.True(accounts.TrySetMute(NameOf("m8a-cee"), DateTime.UtcNow.AddMinutes(30)));

        await UntilAsync(server, () => !adminContext.IsAdmin, "the demotion");
        await UntilAsync(server, () => RejectionOf(bee) != null, "the remote ban closing the connection");
        Assert.Contains("banned elsewhere", RejectionOf(bee)!.Reason, StringComparison.Ordinal);
        await cee.SendAsync(new ClientChatMessage(ChatChannel.World, "can anyone hear me"));
        await UntilAsync(server, () => SystemLines(cee).Contains(DedicatedServerHost.MutedText), "the remote mute");

        await CommandAsync(admin, "/announce still an admin?");
        await UntilAsync(server, () => SystemLines(admin).Contains(DedicatedServerHost.PermissionDeniedText), "the refusal for the demoted admin");
    }

    [Fact]
    public async Task Cap_AFullServer_StillLetsAPlayerTakeBackTheirOwnSeat()
    {
        using MigrationTestDatabase db = new();
        await using WireLoopbackServer server = await StartAsync(db, maxPlayers: 1);
        await using WireLoopbackClient first = await JoinApartAsync(server, "m8a-one");

        // The same account and character on a new connection (a crash rejoin) replaces the old one.
        await using WireLoopbackClient second = await server.ConnectAndJoinAsync("m8a-one");
        Assert.Null(RejectionOf(second));

        // A stranger is still refused.
        JoinRejectedMessage full = await server.ConnectAndGetJoinRejectionAsync("m8a-two", NameOf("m8a-two"), TokenOf("m8a-two"));
        Assert.Equal(ServerConnectionOptions.ServerFullRejectionReason, full.Reason);
    }

    [Fact]
    public async Task Sanction_AWriteThatFails_PropagatesToTheQueue_AndTheAdminIsTold_ThenItWorksAgain()
    {
        using MigrationTestDatabase db = new();
        FailingBans? failing = null;
        await using WireLoopbackServer server = await StartAsync(db, wrap: inner => failing = new FailingBans(inner));
        await GrantAdminAsync(server, db, Admin);
        await CreateAltAsync(server, Alt, Main);
        await using WireLoopbackClient admin = await server.ConnectAndJoinAsync(Admin);
        long failedBefore = server.Runtime.PersistenceWrites!.FailedWrites;

        failing!.FailNextBan = true;
        await CommandAsync(admin, $"/ban \"{NameOf(Alt)}\" 5");
        await UntilAsync(server, () => SystemLines(admin).Contains("The change could not be saved."), "the failure notice");
        Assert.True(server.Runtime.PersistenceWrites.FailedWrites > failedBefore, "the failure never reached the queue");
        Assert.Equal(0L, Convert.ToInt64(db.Scalar("SELECT COUNT(*) FROM accounts WHERE banned_until_utc IS NOT NULL;")));

        await CommandAsync(admin, $"/ban \"{NameOf(Alt)}\" 5");
        await UntilAsync(server, () => SystemLines(admin).Contains($"Banned {NameOf(Alt)}."), "the ban after the failure");
        Assert.Equal(1L, Convert.ToInt64(db.Scalar("SELECT COUNT(*) FROM accounts WHERE banned_until_utc IS NOT NULL;")));
    }

    [Fact]
    public async Task Sanction_BehindALockedDatabase_IsRetried_AndTheReplyWaitsForIt()
    {
        using MigrationTestDatabase db = new();
        await using WireLoopbackServer server = await StartAsync(db);
        await GrantAdminAsync(server, db, Admin);
        await CreateAltAsync(server, Alt, Main);
        await using WireLoopbackClient admin = await server.ConnectAndJoinAsync(Admin);

        using (SqliteConnection holder = db.Schema.CreateOpenConnection())
        using (SqliteTransaction writeLock = holder.BeginTransaction(deferred: false))
        {
            await CommandAsync(admin, $"/ban \"{NameOf(Alt)}\" 5");
            // Past the 5 s busy timeout the group cannot start. The admin hears nothing yet.
            await Task.Delay(TimeSpan.FromSeconds(7));
            Assert.DoesNotContain($"Banned {NameOf(Alt)}.", SystemLines(admin));
            Assert.True(server.Runtime.PersistenceWrites!.LockRetries >= 1);
            writeLock.Rollback();
        }

        await UntilAsync(server, () => SystemLines(admin).Contains($"Banned {NameOf(Alt)}."), "the ban once the lock ended");
        Assert.DoesNotContain("The change could not be saved.", SystemLines(admin));
        Assert.Equal(1L, Convert.ToInt64(db.Scalar("SELECT COUNT(*) FROM accounts WHERE banned_until_utc IS NOT NULL;")));
        SqlitePools.Release(db.Schema);
    }

    // Fails the next ban write; everything else is the real repository.
    private sealed class FailingBans(IAccountRepository inner) : IAccountRepository
    {
        public volatile bool FailNextBan;

        public CharacterLookup Lookup(string trimmedName, AccountKey? account) => inner.Lookup(trimmedName, account);

        public bool RecordJoin(AccountKey account, CharacterKey character, bool claim, DateTime utcNow) =>
            inner.RecordJoin(account, character, claim, utcNow);

        public IReadOnlyList<AccountCharacterSummary> ListCharacters(AccountKey account, int max) => inner.ListCharacters(account, max);

        public AccountStanding? ReadStanding(AccountKey account) => inner.ReadStanding(account);

        public bool TrySetBan(string displayName, DateTime? untilUtc, string? reason)
        {
            if (FailNextBan)
            {
                FailNextBan = false;
                throw new InvalidOperationException("disk I/O error");
            }

            return inner.TrySetBan(displayName, untilUtc, reason);
        }

        public bool TrySetMute(string displayName, DateTime? untilUtc) => inner.TrySetMute(displayName, untilUtc);

        public bool TrySetRole(string displayName, int role) => inner.TrySetRole(displayName, role);

        public bool TryClaimPresence(string characterKey, Dream.Simulation.World.WorldId world, DateTime nowUtc, DateTime expiresUtc) =>
            inner.TryClaimPresence(characterKey, world, nowUtc, expiresUtc);

        public void RenewPresence(IReadOnlyList<string> characterKeys, Dream.Simulation.World.WorldId world, DateTime expiresUtc) =>
            inner.RenewPresence(characterKeys, world, expiresUtc);

        public void ReleasePresence(string characterKey, Dream.Simulation.World.WorldId world) =>
            inner.ReleasePresence(characterKey, world);
    }
}
