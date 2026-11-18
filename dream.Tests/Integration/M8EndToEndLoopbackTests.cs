using System.Text;
using Microsoft.Data.Sqlite;
using Xunit;
using Dream.Client.Networking;
using Dream.Multiplayer.Protocol;
using Dream.Server;
using Dream.Server.Accounts;
using Dream.Server.Persistence.Repositories;
using Dream.Simulation.Characters;
using Dream.Simulation.Combat;
using Dream.Simulation.Determinism;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Tests.Server.Persistence.Migrations;
using Dream.Tests.Simulation.Runtime;
using Dream.Tests.Support;
using Dream.Tests.Utilities;

namespace Dream.Tests.Integration;

// M8 end to end, nothing stubbed: real DedicatedServerService over loopback TCP, real SQLite, real accounts.
// The administrator is made the way an operator would (the account's role in the database, here through the
// repository the CLI uses), and every command is a real chat line of the Command channel. What is proved:
//  - a non-admin's command is refused and changes nothing;
//  - announce reaches everyone; mute holds across a rejoin; kick closes the connection with its reason;
//  - ban refuses the join with the reason, online or offline, across a restart, until unbanned;
//  - the message of the day follows the join reply, and the player cap refuses with "Server is full.";
//  - other players receive a level and nothing else of a player's progress, and the owner's HUD is right;
//  - a kick is not a lost connection (the client does not retry it), a dropped socket is.
public sealed class M8EndToEndLoopbackTests
{
    private const string Admin = "m8-adm";
    private const string Bee = "m8-bee";

    private static string NameOf(string session) => $"Name {session}";

    private static async Task<WireLoopbackServer> StartAsync(MigrationTestDatabase db, string? motd = null, int maxPlayers = 0)
    {
        DedicatedServerHost host = ServerBootstrap.CreateAuthoritativeHost(
            ServerBootstrap.CreatePersistenceBundle(db.DatabasePath, MigrationTestDatabase.InitScriptPath, start: new ServerStartOptions(null)));
        host.Motd = motd;
        host.MaxPlayers = maxPlayers;
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

    // A client the harness does not track, for a player who is about to be kicked or banned: the harness would
    // wait for a connection the server closed on its own. The caller disposes it.
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
    //
    // Every wait in this class is the same shape: an admin command's reply, a chat notice, a kick or
    // ban notice, the message of the day. All of them are a persistence round trip followed by the
    // one tick that applies the result to the live connections, so a tick budget is the honest bound
    // and it keeps a never-true condition from spending the whole 180 s cap. Nothing here is on a
    // cadence - this class never sets StandingRefreshIntervalTicks and never waits on it - and a
    // /mute or /ban with no minutes runs to PermanentUntilUtc, so no wait here is on the clock
    // either.
    // `observed` is the client the condition is READ FROM, and passing it is the whole point: every
    // wait in this class is satisfied by a frame reaching that client, so the budget has to be spent
    // on its received-frame count as well as on the server's ticks. Budgeting ticks alone measured
    // the side the test loop drives itself, which cannot starve, and failed this class under a full
    // suite while its server side was provably healthy.
    //
    // budget defaults to the write round trip; a wait whose write has already landed (the test has
    // seen the reply the barrier read sent) passes LoopbackDeadline.Applied instead.
    private static Task UntilAsync(WireLoopbackServer server, WireLoopbackClient observed, Func<bool> done, string what, long? budget = null) =>
        server.UntilAsync(done, () => what, budget ?? LoopbackDeadline.WriteRoundTrip, WireLoopbackServer.Observing(observed));

    private static Task UntilAsync(WireLoopbackServer server, WireLoopbackClient observed, Func<bool> done, Func<string> what, long? budget = null) =>
        server.UntilAsync(done, what, budget ?? LoopbackDeadline.WriteRoundTrip, WireLoopbackServer.Observing(observed));

    // A condition every one of several clients has to see: all of their frame counts are counters.
    private static Task UntilAllAsync(WireLoopbackServer server, WireLoopbackClient[] observed, Func<bool> done, string what) =>
        server.UntilAsync(
            done,
            () => what,
            LoopbackDeadline.WriteRoundTrip,
            [.. observed.Select(WireLoopbackServer.Observing)]);

    // A condition read from the DATABASE: it is settled by the persistence worker, so that is the
    // side it is budgeted on alongside the server's ticks. Budgeting ticks alone would again be
    // measuring a side that cannot starve.
    private static Task UntilRowAsync(WireLoopbackServer server, Func<bool> done, string what) =>
        server.UntilAsync(done, () => what, LoopbackDeadline.WriteRoundTrip, server.PersistenceWrites);

    // What a stalled mute notice has to tell apart: whether the mute reached the player's own
    // connection standing - StepAccountStandings' job on the tick after the write's barrier read,
    // and the thing the notice is produced from - or whether it did and the notice still never came.
    private static string MuteStateOf(WireLoopbackServer server, WireLoopbackClient client)
    {
        string standing =
            server.Host.TryGetConnectionContext(server.ConnectionFor(client).ConnectionId, out ServerRoutingContext? context) && context != null
                ? $"muted_until_utc={context.Standing.MutedUntilUtc?.ToString("o") ?? "null"}"
                : "no connection context";
        Dream.Simulation.Persistence.PersistenceWriteQueue? writes = server.Runtime.PersistenceWrites;
        return $"{standing}, writes pending={writes?.PendingCount} completed={writes?.CompletedWrites} failed={writes?.FailedWrites}, tick={server.Runtime.CurrentServerTick}";
    }

    private static async Task TicksAsync(WireLoopbackServer server, int ticks)
    {
        for (int i = 0; i < ticks; i++)
        {
            await server.TickAsync();
            await server.FlushAllAsync();
            await Task.Delay(1);
        }
    }

    private static ServerChatMessage[] Chats(WireLoopbackClient client) =>
        [.. client.Frames.Select(frame => frame.Decoded.Message).OfType<ServerChatMessage>()];

    private static string[] SystemLines(WireLoopbackClient client) =>
        [.. Chats(client).Where(chat => chat.Channel == ChatChannel.System).Select(chat => chat.Text)];

    private static Task<bool> HasSystemLine(WireLoopbackServer server, WireLoopbackClient client, string text) =>
        Task.FromResult(SystemLines(client).Contains(text));

    private static Task SayAsync(WireLoopbackClient client, ChatChannel channel, string text) =>
        client.SendAsync(new ClientChatMessage(channel, text));

    private static Task CommandAsync(WireLoopbackClient client, string text) => SayAsync(client, ChatChannel.Command, text);

    private static JoinRejectedMessage? RejectionOf(WireLoopbackClient client) =>
        client.Frames.Select(frame => frame.Decoded.Message).OfType<JoinRejectedMessage>().FirstOrDefault();

    private static string TokenOf(string session) => TestAccountTokens.For(NameOf(session));

    // ---- a non-admin ----

    [Fact]
    public async Task Admin_ANonAdminsCommands_AreRefused_AndChangeNothing()
    {
        using MigrationTestDatabase db = new();
        await using WireLoopbackServer server = await StartAsync(db);
        await GrantAdminAsync(server, db, Admin);
        await using WireLoopbackClient admin = await server.ConnectAndJoinAsync(Admin);
        await using WireLoopbackClient player = await server.ConnectAndJoinAsync(Bee);

        // The line is a normal Command chat line; only the role the connection joined with decides.
        await CommandAsync(player, $"/kick \"{NameOf(Admin)}\" get out");
        await UntilAsync(server, player, () => SystemLines(player).Count(line => line == DedicatedServerHost.PermissionDeniedText) >= 1, "the refusal of kick");
        await CommandAsync(player, "/announce I am in charge now");
        await CommandAsync(player, $"/ban \"{NameOf(Admin)}\" 999 nope");
        await CommandAsync(player, "/definitely-not-a-command");
        await UntilAsync(server, player, () => SystemLines(player).Count(line => line == DedicatedServerHost.PermissionDeniedText) >= 4, "four refusals");

        await TicksAsync(server, 12);
        Assert.True(server.Host.IsConnectionJoined(server.ConnectionFor(admin).ConnectionId), "the admin was kicked by a player");
        Assert.DoesNotContain(SystemLines(admin), line => line.Contains("Announcement", StringComparison.Ordinal));
        Assert.Equal(0L, Convert.ToInt64(db.Scalar("SELECT COUNT(*) FROM accounts WHERE banned_until_utc IS NOT NULL OR muted_until_utc IS NOT NULL;")));
        Assert.Equal(1L, Convert.ToInt64(db.Scalar("SELECT COUNT(*) FROM accounts WHERE role = 1;")));
    }

    [Fact]
    public async Task Admin_TheRoleComesFromTheAccount_NotFromTheClient()
    {
        using MigrationTestDatabase db = new();
        await using WireLoopbackServer server = await StartAsync(db);
        await using WireLoopbackClient player = await server.ConnectAndJoinAsync(Bee);

        // Whatever the client claims, it is a player: the join carries no role and nothing else can set one.
        Assert.True(server.Host.TryGetConnectionContext(server.ConnectionFor(player).ConnectionId, out ServerRoutingContext? context));
        Assert.False(context!.IsAdmin);
        Assert.Equal(0, context.Standing.Role);

        // The account row is written by the persistence queue after the join; wait for it as GrantAdminAsync does.
        await FlushAsync(server);
        Assert.True(new SqliteAccountRepository(db.Schema).TrySetRole(NameOf(Bee), 1));
        await CommandAsync(player, "/announce still a player until I rejoin");
        await UntilAsync(server, player, () => SystemLines(player).Contains(DedicatedServerHost.PermissionDeniedText), "the refusal");

        await server.DisconnectAsync(player);
        await using WireLoopbackClient rejoined = await server.ConnectAndJoinAsync(Bee);
        Assert.True(server.Host.TryGetConnectionContext(server.ConnectionFor(rejoined).ConnectionId, out context));
        Assert.True(context!.IsAdmin);
    }

    // ---- announce ----

    [Fact]
    public async Task Admin_Announce_ReachesEveryJoinedPlayer()
    {
        using MigrationTestDatabase db = new();
        await using WireLoopbackServer server = await StartAsync(db);
        await GrantAdminAsync(server, db, Admin);
        await using WireLoopbackClient admin = await server.ConnectAndJoinAsync(Admin);
        await using WireLoopbackClient one = await server.ConnectAndJoinAsync(Bee);
        await using WireLoopbackClient two = await server.ConnectAndJoinAsync("m8-cee");

        await CommandAsync(admin, "/announce The server restarts in five minutes");

        const string Expected = "[Announcement] The server restarts in five minutes";
        await UntilAllAsync(
            server,
            [admin, one, two],
            () => new[] { admin, one, two }.All(client => SystemLines(client).Contains(Expected)),
            "the announcement at all three");
        Assert.All(new[] { admin, one, two }, client => Assert.Single(SystemLines(client), line => line == Expected));
    }

    // ---- mute ----

    // What the name claims is that the mute lives on the ACCOUNT, i.e. in accounts.muted_until_utc.
    // A rejoin against the same host does not prove that: /mute also records an in-process
    // SanctionMemory entry, and DedicatedServerHost.OverlaySanctions overrides whatever the join
    // prefetch read with that entry for the whole SanctionMemoryWindow (two minutes). A rejoin a
    // second later is always inside that window, so TrySetMute could write nothing, write to the
    // wrong account, or be deleted outright and this test still passed.
    //
    // So the row is now asserted directly, and the rejoin that has to be silenced BY the row
    // happens on a restarted host. A fresh DedicatedServerHost starts with an empty
    // _sanctionMemory, so there is nothing left to override the prefetch with: the only thing that
    // can still silence the player is accounts.muted_until_utc. That is deterministic - it needs no
    // clock to advance past the window and reaches into no private state.
    [Fact]
    public async Task Admin_Mute_SilencesThePlayer_AcrossARejoin_UntilUnmuted()
    {
        using MigrationTestDatabase db = new();
        await using (WireLoopbackServer server = await StartAsync(db))
        {
            await GrantAdminAsync(server, db, Admin);
            await using WireLoopbackClient admin = await server.ConnectAndJoinAsync(Admin);
            WireLoopbackClient bee = await server.ConnectAndJoinAsync(Bee);

            await CommandAsync(admin, $"/mute \"{NameOf(Bee)}\"");
            await UntilAsync(server, admin, () => SystemLines(admin).Contains($"Muted {NameOf(Bee)}."), "the mute result");

            // Muted at once, on the live connection. The line is sent BEFORE this waits on anything,
            // which is the point: it proves a line sent the moment the admin's reply lands is already
            // refused. The reply is sent by the write's own barrier read, which is also what queues
            // the AccountUpdate, so by now the only thing left is the tick that applies it - a couple
            // of ticks, not a cadence and not a clock.
            await SayAsync(bee, ChatChannel.World, "hello anyone");
            await UntilAsync(
                server, bee,
                () => SystemLines(bee).Contains(DedicatedServerHost.MutedText),
                () => $"the muted notice ({MuteStateOf(server, bee)})",
                LoopbackDeadline.Applied);
            await TicksAsync(server, 8);
            Assert.DoesNotContain(Chats(admin), chat => chat.Text == "hello anyone");

            // The mute reached the account row, and it is Bee's account that carries it - not the
            // admin's, and not some second row. The persistence queue writes that row
            // asynchronously, so wait for the write, as the sibling admin tests do.
            await FlushAsync(server);
            Assert.Equal(1L, Convert.ToInt64(db.Scalar("SELECT COUNT(*) FROM accounts WHERE muted_until_utc IS NOT NULL;")));
            Assert.Equal(
                1L,
                Convert.ToInt64(db.Scalar(
                    "SELECT COUNT(*) FROM accounts a JOIN player_profiles p ON p.account_id = a.account_id " +
                    $"WHERE p.player_name = '{NameOf(Bee)}' AND a.muted_until_utc IS NOT NULL;")));

            // A rejoin against the SAME host is still worth keeping - it is the live path a player
            // takes - but on its own it only exercises the in-process overlay.
            await server.DisconnectAsync(bee);
            bee = await server.ConnectAndJoinAsync(Bee);
            await SayAsync(bee, ChatChannel.World, "hello again");
            await UntilAsync(
                server, bee,
                () => SystemLines(bee).Contains(DedicatedServerHost.MutedText),
                () => $"the muted notice after a rejoin ({MuteStateOf(server, bee)})",
                LoopbackDeadline.Applied);
            await TicksAsync(server, 8);
            Assert.DoesNotContain(Chats(admin), chat => chat.Text == "hello again");

            // Commands of the admin are never muted, and an unmute frees the player at once - and
            // clears the row.
            await CommandAsync(admin, $"/unmute \"{NameOf(Bee)}\"");
            await UntilAsync(server, admin, () => SystemLines(admin).Contains($"Unmuted {NameOf(Bee)}."), "the unmute result");
            await SayAsync(bee, ChatChannel.World, "i can talk");
            await UntilAsync(
                server, admin,
                () => Chats(admin).Any(chat => chat.Channel == ChatChannel.World && chat.Text == "i can talk" && chat.From == NameOf(Bee)),
                "the player's line reaching the admin");
            await UntilRowAsync(
                server,
                () => Convert.ToInt64(db.Scalar("SELECT COUNT(*) FROM accounts WHERE muted_until_utc IS NOT NULL;")) == 0L,
                "the unmute reaching the account row");

            // Muted again, so the restart below has a mute to carry. Flushed, so the row is on disk
            // before this host releases the world.
            await CommandAsync(admin, $"/mute \"{NameOf(Bee)}\"");
            await UntilAsync(server, admin, () => SystemLines(admin).Contains($"Muted {NameOf(Bee)}."), "the second mute result");
            await UntilRowAsync(
                server,
                () => Convert.ToInt64(db.Scalar("SELECT COUNT(*) FROM accounts WHERE muted_until_utc IS NOT NULL;")) == 1L,
                "the second mute reaching the account row before the restart");
            await bee.DisposeAsync();
        }

        // The restart: a new host, so _sanctionMemory is empty and there is nothing left to override
        // the join prefetch with. The only thing that can still silence Bee here is the row. This
        // block deliberately asks nothing of the periodic standing refresh - it judges the join,
        // which reads the row, and then the row itself.
        await using (WireLoopbackServer restarted = await StartAsync(db))
        {
            await using WireLoopbackClient admin = await restarted.ConnectAndJoinAsync(Admin);
            await using WireLoopbackClient bee = await restarted.ConnectAndJoinAsync(Bee);

            await SayAsync(bee, ChatChannel.World, "hello after the restart");
            await UntilAsync(
                restarted, bee,
                () => SystemLines(bee).Contains(DedicatedServerHost.MutedText),
                () => $"the muted notice after a restart, which only accounts.muted_until_utc can produce ({MuteStateOf(restarted, bee)})",
                LoopbackDeadline.Applied);
            await TicksAsync(restarted, 8);
            Assert.DoesNotContain(Chats(admin), chat => chat.Text == "hello after the restart");

            // And the unmute reaches the row on this host too, so the mute does not come back on the
            // next restart.
            await CommandAsync(admin, $"/unmute \"{NameOf(Bee)}\"");
            await UntilAsync(restarted, admin, () => SystemLines(admin).Contains($"Unmuted {NameOf(Bee)}."), "the unmute result after the restart");
            await UntilRowAsync(
                restarted,
                () => Convert.ToInt64(db.Scalar("SELECT COUNT(*) FROM accounts WHERE muted_until_utc IS NOT NULL;")) == 0L,
                "the unmute reaching the account row after the restart");
        }
    }

    // ---- kick ----

    [Fact]
    public async Task Admin_Kick_ClosesTheConnectionWithItsReason_AndThePlayerMayReturn()
    {
        using MigrationTestDatabase db = new();
        await using WireLoopbackServer server = await StartAsync(db);
        await GrantAdminAsync(server, db, Admin);
        await using WireLoopbackClient admin = await server.ConnectAndJoinAsync(Admin);
        await using WireLoopbackClient bee = await JoinApartAsync(server, Bee);

        await CommandAsync(admin, "/kick \"No Such Player\"");
        await UntilAsync(server, admin, () => SystemLines(admin).Contains(DedicatedServerHost.PlayerNotOnlineText), "the not-online answer");
        await CommandAsync(admin, $"/kick \"{NameOf(Admin)}\"");
        await UntilAsync(server, admin, () => SystemLines(admin).Contains("You cannot do that to yourself."), "the self-kick refusal");

        await CommandAsync(admin, $"/kick \"{NameOf(Bee)}\" take a break");
        await UntilAsync(server, bee, () => RejectionOf(bee) != null, "the kick notice");

        JoinRejectedMessage notice = RejectionOf(bee)!;
        Assert.Contains("kicked", notice.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("take a break", notice.Reason, StringComparison.Ordinal);
        Assert.False(RemoteClientSession.IsRetryableEnd(notice.Reason), "a kick must not be retried by the client");
        await UntilAsync(server, admin, () => SystemLines(admin).Contains($"Kicked {NameOf(Bee)}."), "the kick result");

        // A kick is not a ban.
        await using WireLoopbackClient back = await server.ConnectAndJoinAsync(Bee);
        Assert.Null(RejectionOf(back));
    }

    // ---- ban ----

    [Fact]
    public async Task Admin_Ban_KicksAnOnlinePlayer_RefusesTheJoinWithTheReason_AcrossARestart_UntilUnbanned()
    {
        using MigrationTestDatabase db = new();
        await using (WireLoopbackServer server = await StartAsync(db))
        {
            await GrantAdminAsync(server, db, Admin);
            await using WireLoopbackClient admin = await server.ConnectAndJoinAsync(Admin);
            await using WireLoopbackClient bee = await JoinApartAsync(server, Bee);

            await CommandAsync(admin, $"/ban \"{NameOf(Bee)}\" 60 spamming links");
            await UntilAsync(server, bee, () => RejectionOf(bee) != null, "the ban notice");
            Assert.Contains("banned", RejectionOf(bee)!.Reason, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("spamming links", RejectionOf(bee)!.Reason, StringComparison.Ordinal);
            await UntilAsync(server, admin, () => SystemLines(admin).Contains($"Banned {NameOf(Bee)}."), "the ban result");
            await FlushAsync(server);

            JoinRejectedMessage refused = await server.ConnectAndGetJoinRejectionAsync(Bee, NameOf(Bee), TokenOf(Bee));
            Assert.StartsWith(JoinRejectionReasons.Banned, refused.Reason, StringComparison.Ordinal);
            Assert.Contains("spamming links", refused.Reason, StringComparison.Ordinal);
            Assert.Contains("The ban ends", refused.Reason, StringComparison.Ordinal);
            Assert.False(RemoteClientSession.IsRetryableEnd(refused.Reason));

            // Other players are not affected.
            await using WireLoopbackClient other = await server.ConnectAndJoinAsync("m8-other");
            Assert.Null(RejectionOf(other));
        }

        SqlitePools.Release(db.Schema);
        await using (WireLoopbackServer restarted = await StartAsync(db))
        {
            JoinRejectedMessage stillBanned = await restarted.ConnectAndGetJoinRejectionAsync(Bee, NameOf(Bee), TokenOf(Bee));
            Assert.Contains("spamming links", stillBanned.Reason, StringComparison.Ordinal);

            // The operator's way out, while the server runs.
            StringWriter output = new();
            StringWriter error = new();
            Assert.Equal(AccountCli.ExitOk, AccountCli.Run(["unban", NameOf(Bee)], () => db.Schema, output, error));

            await using WireLoopbackClient back = await restarted.ConnectAndJoinAsync(Bee);
            Assert.Null(RejectionOf(back));
        }
    }

    [Fact]
    public async Task Admin_BanOfflineCharacter_IsPermanentWithoutMinutes_AndAnUnknownNameIsSaid()
    {
        using MigrationTestDatabase db = new();
        await using WireLoopbackServer server = await StartAsync(db);
        await GrantAdminAsync(server, db, Admin);
        await using WireLoopbackClient admin = await server.ConnectAndJoinAsync(Admin);
        WireLoopbackClient sleeper = await server.ConnectAndJoinAsync("m8-sleeper");
        await server.DisconnectAsync(sleeper);
        await FlushAsync(server);

        await CommandAsync(admin, $"/ban \"{NameOf("m8-sleeper")}\" griefing the castle");
        await UntilAsync(server, admin, () => SystemLines(admin).Contains($"Banned {NameOf("m8-sleeper")}."), "the ban result");
        await CommandAsync(admin, "/ban \"Nobody At All\" 5");
        await UntilAsync(server, admin, () => SystemLines(admin).Contains("No account owns a character named 'Nobody At All'."), "the unknown-name answer");
        await FlushAsync(server);

        JoinRejectedMessage refused = await server.ConnectAndGetJoinRejectionAsync("m8-sleeper", NameOf("m8-sleeper"), TokenOf("m8-sleeper"));
        Assert.Contains("griefing the castle", refused.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("The ban ends", refused.Reason, StringComparison.Ordinal);

        // /unban from chat lifts it for an offline character too.
        await CommandAsync(admin, $"/unban \"{NameOf("m8-sleeper")}\"");
        await UntilAsync(server, admin, () => SystemLines(admin).Contains($"Unbanned {NameOf("m8-sleeper")}."), "the unban result");
        await FlushAsync(server);
        await using WireLoopbackClient back = await server.ConnectAndJoinAsync("m8-sleeper");
        Assert.Null(RejectionOf(back));
    }

    [Fact]
    public async Task Admin_ABanRunsOut()
    {
        using MigrationTestDatabase db = new();
        await using WireLoopbackServer server = await StartAsync(db);
        await GrantAdminAsync(server, db, Admin);
        WireLoopbackClient first = await server.ConnectAndJoinAsync(Bee);
        await server.DisconnectAsync(first);
        await FlushAsync(server);

        SqliteAccountRepository accounts = new(db.Schema);
        Assert.True(accounts.TrySetBan(NameOf(Bee), DateTime.UtcNow.AddMinutes(-1), "yesterday's news"));

        await using WireLoopbackClient back = await server.ConnectAndJoinAsync(Bee);
        Assert.Null(RejectionOf(back));
    }

    // ---- the message of the day and the cap ----

    [Fact]
    public async Task Motd_FollowsTheJoinReply_OncePerJoin_AndThePlayerCapRefusesTheNextPlayer()
    {
        using MigrationTestDatabase db = new();
        await using WireLoopbackServer server = await StartAsync(db, motd: "Welcome to the Gate, traveller.", maxPlayers: 1);

        WireLoopbackClient first = await server.ConnectAndJoinAsync("m8-one");
        await UntilAsync(server, first, () => SystemLines(first).Contains("Welcome to the Gate, traveller."), "the message of the day");
        IReadOnlyList<WireFrame> frames = first.Frames;
        int joinIndex = IndexOf(frames, frame => frame.Decoded.Message is JoinAcceptedMessage);
        int motdIndex = IndexOf(frames, frame => frame.Decoded.Message is ServerChatMessage { Channel: ChatChannel.System } chat && chat.Text == "Welcome to the Gate, traveller.");
        Assert.InRange(joinIndex, 0, motdIndex - 1);
        await TicksAsync(server, 10);
        Assert.Single(SystemLines(first), line => line == "Welcome to the Gate, traveller.");

        // Every seat is taken.
        JoinRejectedMessage full = await server.ConnectAndGetJoinRejectionAsync("m8-two", NameOf("m8-two"), TokenOf("m8-two"));
        Assert.Equal(ServerConnectionOptions.ServerFullRejectionReason, full.Reason);
        Assert.True(server.Host.IsConnectionJoined(server.ConnectionFor(first).ConnectionId), "the seated player lost their seat");

        // A seat frees: the next player gets in and is greeted too.
        await server.DisconnectAsync(first);
        await using WireLoopbackClient second = await server.ConnectAndJoinAsync("m8-two");
        await UntilAsync(server, second, () => SystemLines(second).Contains("Welcome to the Gate, traveller."), "the message of the day for the second player");
        Assert.Null(RejectionOf(second));
    }

    [Fact]
    public async Task Motd_ARejectedJoin_IsNotGreeted()
    {
        using MigrationTestDatabase db = new();
        await using WireLoopbackServer server = await StartAsync(db, motd: "Welcome.", maxPlayers: 1);
        await using WireLoopbackClient first = await server.ConnectAndJoinAsync("m8-one");

        Task<JoinRejectedMessage> refused = server.ConnectAndGetJoinRejectionAsync("m8-two", NameOf("m8-two"), TokenOf("m8-two"));

        Assert.Equal(ServerConnectionOptions.ServerFullRejectionReason, (await refused).Reason);
    }

    private static int IndexOf(IReadOnlyList<WireFrame> frames, Func<WireFrame, bool> match)
    {
        for (int i = 0; i < frames.Count; i++)
        {
            if (match(frames[i]))
            {
                return i;
            }
        }

        return -1;
    }

    // ---- owner-only progress ----

    [Fact]
    public async Task OwnerProgress_OthersSeeLevelOnly_AndTheOwnersHudStaysRight()
    {
        using MigrationTestDatabase db = new();
        await using WireLoopbackServer server = await StartAsync(db);
        await using HeadlessPlayScene owner = await HeadlessPlayScene.JoinAsync(server, "m8-owner");

        // The owner's server-side values: a level, a lot of experience, a base strength nobody else may read.
        const int Experience = 912347;
        IAuthoritativePlayerTarget target = WaveIRuntimeKit.Target(server.Runtime, "m8-owner");
        target.Combatant.ApplyProgression(4, Experience);
        target.Combatant.ApplyBaseStats(new PlayerCombatStats { Strength = 41, AttackSpeed = target.Combatant.BaseStats.AttackSpeed, Vitality = target.Combatant.BaseStats.Vitality });

        // The stranger joins after, so the JoinAccepted actor list is tested too.
        await using WireLoopbackClient stranger = await server.ConnectAndJoinAsync("m8-stranger");

        ProgressCounter[] panelProgress = [server.ServerTicks, new($"messages applied by {owner.SessionId}", () => owner.Applied.Count)];
        LoopbackDeadline deadline = new(panelProgress);
        while (true)
        {
            await server.TickAsync();
            await server.FlushAllAsync();
            Assert.True(await owner.FlushAsync());
            owner.Pump();
            Dream.Rendering.Hud.HudStatusSnapshot panel = owner.World.CapturePresentationState(64f).HudStatus;
            if (panel.Level == 4 && panel.Experience == Experience && panel.BaseStrength == 41)
            {
                Assert.True(panel.ExperienceToNextLevel > 0);
                break;
            }

            deadline.AssertAlive(panelProgress, () => $"the owner's panel at level 4 (it shows level {panel.Level}, experience {panel.Experience}, strength {panel.BaseStrength})");
            await Task.Delay(2);
        }

        await TicksAsync(server, 20);
        owner.Pump();

        // The stranger: every actor of the owner it was ever sent has the level and nothing else.
        int ownerActorId = target.ActorId;
        List<ActorSnapshot> seen = [];
        foreach (WireFrame frame in stranger.Frames)
        {
            switch (frame.Decoded.Message)
            {
                case JoinAcceptedMessage joined:
                    seen.AddRange(joined.EffectiveInitialActors.Where(actor => actor.ActorId == ownerActorId));
                    break;
                case ServerSnapshotMessage snapshot:
                    seen.AddRange(snapshot.Snapshot.Actors.Where(actor => actor.ActorId == ownerActorId));
                    break;
            }
        }

        Assert.NotEmpty(seen);
        Assert.All(seen, actor =>
        {
            Assert.Null(actor.CombatStats);
            Assert.Equal(WorldSessionRuntime.HiddenExperience, actor.Progression!.Value.Experience);
            Assert.Equal(WorldSessionRuntime.HiddenExperience, actor.Progression.Value.ExperienceToNextLevel);
        });
        // Level stays public: the latest sighting shows it.
        Assert.Equal(4, seen[^1].Progression!.Value.Level);

        // The bytes on the stranger's wire hold neither the experience nor a stats record of anyone.
        string raw = Encoding.UTF8.GetString(stranger.RawStream);
        Assert.DoesNotContain(Experience.ToString(), raw, StringComparison.Ordinal);
        Assert.DoesNotContain("combatStats", raw, StringComparison.OrdinalIgnoreCase);

        // The owner's own snapshots hide it as well; the HUD got it from the owner message.
        Assert.Contains(owner.Applied, message => message is ServerPlayerProgress { Level: 4, Experience: Experience });
        Assert.All(
            owner.Applied.OfType<ServerSnapshotMessage>().SelectMany(snapshot => snapshot.Snapshot.Actors).Where(actor => actor.SessionId == "m8-owner"),
            actor => Assert.Null(actor.CombatStats));

        // The stranger got progress messages of its own (its own level 1), never the owner's.
        ServerPlayerProgress[] strangerOwn = [.. stranger.Frames.Select(frame => frame.Decoded.Message).OfType<ServerPlayerProgress>()];
        Assert.NotEmpty(strangerOwn);
        Assert.All(strangerOwn, progress =>
        {
            Assert.NotEqual(Experience, progress.Experience);
            Assert.NotEqual(41, progress.BaseStats.Strength);
            Assert.Equal(1, progress.Level);
        });

        // The server's own state, and so its checksum, still holds the truth.
        ulong withProjection = SimulationStateChecksum.Compute(server.Runtime);
        server.Runtime.OwnerOnlyProgress = false;
        Assert.Equal(withProjection, SimulationStateChecksum.Compute(server.Runtime));
        Assert.Equal(Experience, server.Runtime.CaptureActorSnapshots().Single(actor => actor.SessionId == "m8-owner").Progression!.Value.Experience);
    }

    // ---- reconnect ----

    [Fact]
    public async Task Reconnect_ADroppedConnectionIsRetryable_AKickIsNot()
    {
        using MigrationTestDatabase db = new();
        WireLoopbackServer server = await StartAsync(db);
        await GrantAdminAsync(server, db, Admin);
        await using WireLoopbackClient admin = await server.ConnectAndJoinAsync(Admin);
        await using HeadlessPlayScene kicked = await HeadlessPlayScene.JoinAsync(server, "m8-kicked");
        await using HeadlessPlayScene dropped = await HeadlessPlayScene.JoinAsync(server, "m8-dropped");

        await CommandAsync(admin, $"/kick \"{NameOf("m8-kicked")}\" cooling off");
        // The server is ticked in here, so the bound is LoopbackDeadline on its tick number: a
        // loaded machine makes this slower, not wrong.
        ProgressCounter[] removalProgress = [server.ServerTicks, new($"messages applied by {kicked.SessionId}", () => kicked.Applied.Count)];
        LoopbackDeadline removal = new(removalProgress);
        while (kicked.Session.DisconnectReason == null)
        {
            removal.AssertAlive(removalProgress, () => "the kicked client learning it was removed");
            await server.TickAsync();
            await server.FlushAllAsync();
            await dropped.FlushAsync();
            kicked.Pump();
            await Task.Delay(2);
        }

        string kickReason = kicked.Session.DisconnectReason!;
        Assert.Contains("cooling off", kickReason, StringComparison.Ordinal);
        Assert.False(RemoteClientSession.IsRetryableEnd(kickReason), $"'{kickReason}' would be retried");

        // The server vanishes under the other client: no notice, the socket just closes.
        await server.DisposeAsync();
        // Nothing advances while this waits - the server is gone and the client is waiting for its
        // socket read to come back closed - so there is no progress counter to bound it by, and the
        // flat 30 s stays. A closed socket is reported by the read itself, with no clock involved.
        DateTime noticed = DateTime.UtcNow + WireLoopbackServer.Timeout;
        while (dropped.Session.DisconnectReason == null)
        {
            Assert.True(DateTime.UtcNow < noticed, "the dropped client never noticed");
            dropped.Pump();
            await Task.Delay(5);
        }

        Assert.True(
            RemoteClientSession.IsRetryableEnd(dropped.Session.DisconnectReason),
            $"'{dropped.Session.DisconnectReason}' would not be retried");
    }
}
