using Xunit;
using Dream.Multiplayer.Protocol;
using Dream.Server;
using Dream.Server.Accounts;
using Dream.Server.Persistence;
using Dream.Server.Persistence.Repositories;
using Dream.Tests.Integration;
using Dream.Tests.Server.Persistence.Migrations;
using Dream.Tests.Support;
using Dream.Tests.Utilities;

namespace Dream.Tests.BugHunt;

// Moderation state the server decides in C# rather than in SQL. Real DedicatedServerService over loopback
// TCP and real SQLite, like M8EndToEndLoopbackTests, because both bugs are about what the *authoritative*
// decision reads:
//  - BH-PERSIST-4: /announce is a ChatChannel.Command line, and the command branch ran before the chat
//    path's mute check, so a muted account holding the admin role was still heard by everyone.
//  - BH-PERSIST-2: a ban this host set is kept in memory for two minutes to cover a join whose load
//    started before the write. An `--accounts unban` written by another process was overridden by that
//    memory, so the join stayed refused with the old reason and the tool looked broken.
public sealed class PersistModerationTests
{
    private const string Admin = "bhp-adm";
    private const string Bystander = "bhp-byst";
    private const string Bee = "bhp-bee";

    private static string NameOf(string session) => $"Name {session}";

    private static string TokenOf(string session) => TestAccountTokens.For(NameOf(session));

    private static async Task<WireLoopbackServer> StartAsync(MigrationTestDatabase db)
    {
        ServerPersistenceBundle bundle = ServerBootstrap.CreatePersistenceBundle(
            db.DatabasePath, MigrationTestDatabase.InitScriptPath, start: new ServerStartOptions(null));
        DedicatedServerHost host = ServerBootstrap.CreateAuthoritativeHost(bundle);
        // The cadence the standing re-read rides, shortened so a test does not wait fifteen seconds.
        host.StandingRefreshIntervalTicks = 4;
        return await WireLoopbackServer.StartAsync(host);
    }

    // Awaited, not blocked: see Dream.Tests.Support.PersistenceFlush.
    private static Task FlushAsync(WireLoopbackServer server) =>
        PersistenceFlush.AwaitAsync(server.Runtime.PersistenceWrites);

    // The operator's step: the account exists once its character has joined, then the role is set.
    private static async Task GrantAdminAsync(WireLoopbackServer server, MigrationTestDatabase db, string session)
    {
        WireLoopbackClient first = await server.ConnectAndJoinAsync(session);
        await server.DisconnectAsync(first);
        await FlushAsync(server);
        Assert.True(new SqliteAccountRepository(db.Schema).TrySetRole(NameOf(session), 1));
    }

    // A client the harness does not track, for a player the server is going to close on its own.
    private static async Task<WireLoopbackClient> JoinApartAsync(WireLoopbackServer server, string session)
    {
        Task<ServerConnection> accept = server.Service.AcceptOnThreadPoolAsync();
        WireLoopbackClient client = await WireLoopbackClient.ConnectAsync(server.Port, session);
        ServerConnection connection = await accept.WaitAsync(WireLoopbackServer.Timeout);
        await client.HandshakeAsync();
        ProgressCounter[] joinProgress = [server.ServerTicks, WireLoopbackServer.Observing(client)];
        LoopbackDeadline joining = new(joinProgress);
        while (!server.Host.IsConnectionJoined(connection.ConnectionId))
        {
            joining.AssertAlive(joinProgress, () => $"{session} join");
            await Task.Delay(5);
            await server.TickAsync();
            Assert.True(await OutboundDrain.FlushAsync(connection, WireLoopbackServer.Timeout));
        }

        await client.WaitForJoinAcceptedAsync();
        return client;
    }

    // Bounded by LoopbackDeadline, not by a flat 30 s of wall clock: see WireLoopbackHarness.
    private static Task UntilAsync(WireLoopbackServer server, Func<bool> done, string what) =>
        server.UntilAsync(done, what);

    private static async Task SettleAsync(WireLoopbackServer server, int ticks)
    {
        for (int i = 0; i < ticks; i++)
        {
            await server.TickAsync();
            await server.FlushAllAsync();
        }
    }

    private static string[] SystemLines(WireLoopbackClient client) =>
        [.. client.Frames.Select(frame => frame.Decoded.Message).OfType<ServerChatMessage>()
            .Where(chat => chat.Channel == ChatChannel.System).Select(chat => chat.Text)];

    private static JoinRejectedMessage? RejectionOf(WireLoopbackClient client) =>
        client.Frames.Select(frame => frame.Decoded.Message).OfType<JoinRejectedMessage>().FirstOrDefault();

    private static Task CommandAsync(WireLoopbackClient client, string text) =>
        client.SendAsync(new ClientChatMessage(ChatChannel.Command, text));

    private static bool SawAnnouncement(WireLoopbackClient client) =>
        SystemLines(client).Any(line => line.Contains("[Announcement]", StringComparison.Ordinal));

    // BH-PERSIST-4: the mute reaches the one chat path that speaks to everyone. The muted-admin state is
    // reachable exactly as the packet describes: a mute written to the row (here by another process, which
    // is also what `--accounts grant-admin` on an already muted account amounts to) while the account
    // carries the admin role. /mute in game cannot produce it, because it guards a current admin.
    [Fact]
    public async Task Announce_FromAMutedAdmin_IsSuppressed_AndOnlyTheSenderIsTold()
    {
        using MigrationTestDatabase db = new();
        await using WireLoopbackServer server = await StartAsync(db);
        await GrantAdminAsync(server, db, Admin);
        await using WireLoopbackClient admin = await server.ConnectAndJoinAsync(Admin);
        await using WireLoopbackClient bystander = await server.ConnectAndJoinAsync(Bystander);
        Assert.True(server.Host.TryGetConnectionContext(server.ConnectionFor(admin).ConnectionId, out ServerRoutingContext? adminContext));
        Assert.True(adminContext!.IsAdmin);
        await FlushAsync(server);

        // The admin's own account is muted on the row; the standing re-read brings it to the connection.
        Assert.True(new SqliteAccountRepository(db.Schema).TrySetMute(NameOf(Admin), DateTime.UtcNow.AddMinutes(30)));
        await UntilAsync(server, () => adminContext.Standing.MutedUntilUtc != null, "the mute reaching the connection");
        Assert.True(adminContext.IsAdmin, "the mute must not cost the role: this is the muted-admin case");

        await CommandAsync(admin, "/announce everyone look at me");
        await UntilAsync(server, () => SystemLines(admin).Contains(DedicatedServerHost.MutedText), "the muted notice for the admin");
        // Replies for one command are enqueued together, but the broadcast would ride other connections.
        await SettleAsync(server, 6);

        Assert.False(SawAnnouncement(bystander), "a muted account must not be heard by everyone");
        Assert.False(SawAnnouncement(admin));
    }

    // The control: the gate is the mute and nothing else. An admin who is not muted still broadcasts.
    [Fact]
    public async Task Announce_FromAnUnmutedAdmin_StillReachesEveryJoinedPlayer()
    {
        using MigrationTestDatabase db = new();
        await using WireLoopbackServer server = await StartAsync(db);
        await GrantAdminAsync(server, db, Admin);
        await using WireLoopbackClient admin = await server.ConnectAndJoinAsync(Admin);
        await using WireLoopbackClient bystander = await server.ConnectAndJoinAsync(Bystander);
        await FlushAsync(server);

        const string Expected = "[Announcement] the gate is clear";
        await CommandAsync(admin, "/announce the gate is clear");
        await UntilAsync(server, () => SystemLines(bystander).Contains(Expected), "the announcement");
        Assert.Contains(Expected, SystemLines(admin));
        Assert.DoesNotContain(DedicatedServerHost.MutedText, SystemLines(admin));
    }

    // BH-PERSIST-2: the durable row is the authority on standing. A ban set here moments ago is kept in
    // memory only to cover a read that started before the write; once the host has read the row again, the
    // row decides — so an unban another process wrote is honoured instead of being overridden for two
    // minutes while the player is refused at join with the old reason.
    [Fact]
    public async Task Join_AfterAnOutOfProcessUnban_IsAccepted_WhileTheHostsOwnBanIsStillInMemory()
    {
        using MigrationTestDatabase db = new();
        await using WireLoopbackServer server = await StartAsync(db);
        await GrantAdminAsync(server, db, Admin);
        await using WireLoopbackClient admin = await server.ConnectAndJoinAsync(Admin);
        WireLoopbackClient bee = await JoinApartAsync(server, Bee);

        // In game: a sixty-minute ban, so the memory entry stays well inside SanctionMemoryWindow.
        await CommandAsync(admin, $"/ban \"{NameOf(Bee)}\" 60 cheating");
        await UntilAsync(server, () => RejectionOf(bee) != null, "the ban closing the connection");
        await UntilAsync(server, () => SystemLines(admin).Contains($"Banned {NameOf(Bee)}."), "the ban result");
        await bee.DisposeAsync();
        await FlushAsync(server);

        // The operator's way out, from another process, while the server runs.
        StringWriter output = new();
        StringWriter error = new();
        Assert.Equal(AccountCli.ExitOk, AccountCli.Run(["unban", NameOf(Bee)], () => db.Schema, output, error));

        // One standing re-read is enough: it is queued behind every write this host made, so the row it
        // reads is newer than the recent-sanction memory, which is dropped.
        await SettleAsync(server, 8);
        await FlushAsync(server);
        await SettleAsync(server, 8);

        await using WireLoopbackClient back = await server.ConnectAndJoinAsync(Bee);
        Assert.Null(RejectionOf(back));
    }

    // The other half of the same invariant: an unban must not let a ban another process wrote slip
    // through. A remote ban still refuses the join, with its reason.
    [Fact]
    public async Task Join_AfterAnOutOfProcessBan_IsStillRefusedWithItsReason()
    {
        using MigrationTestDatabase db = new();
        await using WireLoopbackServer server = await StartAsync(db);
        WireLoopbackClient sleeper = await server.ConnectAndJoinAsync(Bee);
        await server.DisconnectAsync(sleeper);
        await FlushAsync(server);

        Assert.True(new SqliteAccountRepository(db.Schema)
            .TrySetBan(NameOf(Bee), DateTime.UtcNow.AddMinutes(30), "banned elsewhere"));

        JoinRejectedMessage refused = await server.ConnectAndGetJoinRejectionAsync(Bee, NameOf(Bee), TokenOf(Bee));
        Assert.Contains("banned elsewhere", refused.Reason, StringComparison.Ordinal);
    }
}
