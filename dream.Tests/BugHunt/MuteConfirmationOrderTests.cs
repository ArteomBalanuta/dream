using Xunit;
using Dream.Multiplayer.Protocol;
using Dream.Server;
using Dream.Server.Persistence.Repositories;
using Dream.Simulation.Characters;
using Dream.Simulation.Runtime;
using Dream.Tests.Server.Persistence;
using Dream.Tests.Support;

namespace Dream.Tests.BugHunt;

// BH-MUTE: the moderation confirmation must not run ahead of the enforcement it reports.
//
// A sanction's write and the barrier read behind it run on the persistence worker, asynchronous to the
// tick. The barrier used to put the account update on one queue and the administrator's "Muted X." on
// another, and the two drain at different points of a tick: account updates in StepAccountStandings, the
// first statement of RunTick, and connection replies after AdvanceTime. A barrier landing between those
// two points therefore sent the confirmation on tick T while the standing only reached the target's
// connection on tick T+1 -- and chat is decided on the receive path, against that connection's standing,
// so a line the muted player sent inside that window was still published after the administrator had been
// told the mute was in place. For a moderation feature the confirmation is the product, and the case
// moderation exists for is exactly a griefer getting one more line out.
//
// These tests drive the production host directly (AccountHostHarness: real SQLite accounts, manual ticks,
// nothing ticking in the background), because the window is one tick wide and only a test that owns the
// tick can stand inside it. Each one sends the command, awaits the persistence queue -- which returns
// exactly when the barrier read has run, with no tick in between -- and then asks what is deliverable.
public sealed class MuteConfirmationOrderTests
{
    private const string AdminConnection = "198.51.100.10:41001";
    private const string BeeConnection = "198.51.100.11:41002";
    private const string CeeConnection = "198.51.100.12:41003";
    private const string AdminSession = "bhm-adm";
    private const string BeeSession = "bhm-bee";
    private const string CeeSession = "bhm-cee";

    private static string NameOf(string session) => $"Name {session}";

    private static string MutedLine(string session) => $"Muted {NameOf(session)}.";

    private static string UnmutedLine(string session) => $"Unmuted {NameOf(session)}.";

    // One tick of the host, drained the way DedicatedServerService drains it: RunTick (which applies the
    // queued account changes first of all), then the pending connection replies.
    private static List<PendingConnectionReply> Tick(AccountHostHarness harness, List<PendingConnectionReply> log)
    {
        int executed = harness.Host.AdvanceTime(SimulationClock.FixedDeltaSeconds);
        if (executed == 0)
        {
            executed = harness.Host.AdvanceTime(SimulationClock.FixedDeltaSeconds * 0.01);
        }

        Assert.Equal(1, executed);
        harness.Host.DrainPendingActionResults();
        return Deliver(harness, log);
    }

    // The service's delivery point, on its own: what the host has ready for the connections right now.
    private static List<PendingConnectionReply> Deliver(AccountHostHarness harness, List<PendingConnectionReply> log)
    {
        List<PendingConnectionReply> drained = [];
        harness.Host.DrainPendingJoinRepliesInto(drained);
        log.AddRange(drained);
        return drained;
    }

    private static async Task SettleAsync(AccountHostHarness harness, List<PendingConnectionReply> log, int ticks)
    {
        for (int i = 0; i < ticks; i++)
        {
            await PersistenceFlush.AwaitAsync(harness.Runtime.PersistenceWrites);
            Tick(harness, log);
        }
    }

    private static async Task JoinAsync(AccountHostHarness harness, List<PendingConnectionReply> log, string connectionId, string session)
    {
        string name = NameOf(session);
        harness.SendJoin(connectionId, session, name, TestAccountTokens.For(name));
        for (int i = 0; i < 24 && !harness.Host.IsConnectionJoined(connectionId); i++)
        {
            await PersistenceFlush.AwaitAsync(harness.Runtime.PersistenceWrites);
            Tick(harness, log);
        }

        Assert.True(harness.Host.IsConnectionJoined(connectionId), $"{session} never joined");
        await SettleAsync(harness, log, 2);
    }

    // The operator's step: the account exists once its character has joined, then the role is set and the
    // administrator comes back with it.
    private static async Task JoinAsAdminAsync(AccountHostHarness harness, List<PendingConnectionReply> log)
    {
        await JoinAsync(harness, log, AdminConnection, AdminSession);
        harness.Host.ReleaseConnection(AdminConnection);
        await SettleAsync(harness, log, 2);
        Assert.True(harness.Sqlite!.TrySetRole(NameOf(AdminSession), 1));
        await JoinAsync(harness, log, AdminConnection, AdminSession);
        Assert.True(harness.Host.TryGetConnectionContext(AdminConnection, out ServerRoutingContext? admin));
        Assert.True(admin!.IsAdmin, "the administrator's role did not reach the connection");
    }

    // A chat line straight into the host's receive path, which is where chat is decided. The clock moves a
    // second first so the rate limiter never decides a test instead of the mute.
    private static void Send(AccountHostHarness harness, List<PendingConnectionReply> log, string connectionId, ChatChannel channel, string text)
    {
        harness.Clock.Advance(TimeSpan.FromSeconds(1));
        foreach (IServerToClientMessage message in harness.Host.HandleClientMessage(connectionId, new ClientChatMessage(channel, text)))
        {
            log.Add(new PendingConnectionReply(connectionId, message));
        }
    }

    private static string[] SystemLines(List<PendingConnectionReply> log, string connectionId) =>
        [.. log.Where(reply => string.Equals(reply.ConnectionId, connectionId, StringComparison.Ordinal))
            .Select(reply => reply.Message).OfType<ServerChatMessage>()
            .Where(chat => chat.Channel == ChatChannel.System).Select(chat => chat.Text)];

    private static string[] HeardLines(List<PendingConnectionReply> log, string connectionId) =>
        [.. log.Where(reply => string.Equals(reply.ConnectionId, connectionId, StringComparison.Ordinal))
            .Select(reply => reply.Message).OfType<ServerChatMessage>()
            .Where(chat => chat.Channel != ChatChannel.System).Select(chat => chat.Text)];

    private static async Task MuteAsync(AccountHostHarness harness, List<PendingConnectionReply> log, string session)
    {
        Send(harness, log, AdminConnection, ChatChannel.Command, $"/mute \"{NameOf(session)}\"");
        await PersistenceFlush.AwaitAsync(harness.Runtime.PersistenceWrites);
        for (int i = 0; i < 8 && !SystemLines(log, AdminConnection).Contains(MutedLine(session)); i++)
        {
            Tick(harness, log);
        }

        Assert.Contains(MutedLine(session), SystemLines(log, AdminConnection));
    }

    // THE BUG, as the administrator meets it. The moment the administrator can be told "Muted X." -- the
    // service's own delivery point, nothing skipped and nothing hurried -- X's very next line must not be
    // published. Before the fix the confirmation was deliverable a tick early and this line went out.
    [Fact]
    public async Task ALineSentOnceTheAdministratorHasBeenToldMuted_IsRefused()
    {
        using PersistenceTestDatabase db = new();
        using AccountHostHarness harness = AccountHostHarness.OverSqlite(db);
        List<PendingConnectionReply> log = [];
        await JoinAsAdminAsync(harness, log);
        await JoinAsync(harness, log, BeeConnection, BeeSession);

        Send(harness, log, AdminConnection, ChatChannel.Command, $"/mute \"{NameOf(BeeSession)}\"");

        // Returns exactly when the barrier read behind the write has run, and no tick has run since: the
        // test is now standing in the window the bug lived in.
        await PersistenceFlush.AwaitAsync(harness.Runtime.PersistenceWrites);

        // Deliver first, tick only if there was nothing to deliver, so the administrator is told at the
        // earliest moment the host allows.
        bool told = false;
        for (int i = 0; i < 8 && !told; i++)
        {
            Deliver(harness, log);
            told = SystemLines(log, AdminConnection).Contains(MutedLine(BeeSession));
            if (!told)
            {
                Tick(harness, log);
                told = SystemLines(log, AdminConnection).Contains(MutedLine(BeeSession));
            }
        }

        Assert.True(told, "the administrator was never told the mute landed");

        const string Line = "one more word out of me";
        Send(harness, log, BeeConnection, ChatChannel.Say, Line);
        Deliver(harness, log);

        Assert.DoesNotContain(Line, HeardLines(log, AdminConnection));
        Assert.DoesNotContain(Line, HeardLines(log, BeeConnection));
        Assert.Contains(DedicatedServerHost.MutedText, SystemLines(log, BeeConnection));
    }

    // The same thing one layer down, where it is a plain ordering fact: with no tick run since the barrier,
    // the standing cannot be in force yet, so nothing that says the mute is in place may be deliverable.
    [Fact]
    public async Task TheMuteConfirmation_IsNotDeliverableBeforeATickHasAppliedTheStanding()
    {
        using PersistenceTestDatabase db = new();
        using AccountHostHarness harness = AccountHostHarness.OverSqlite(db);
        List<PendingConnectionReply> log = [];
        await JoinAsAdminAsync(harness, log);
        await JoinAsync(harness, log, BeeConnection, BeeSession);

        Send(harness, log, AdminConnection, ChatChannel.Command, $"/mute \"{NameOf(BeeSession)}\"");
        await PersistenceFlush.AwaitAsync(harness.Runtime.PersistenceWrites);

        Assert.True(harness.Host.TryGetConnectionContext(BeeConnection, out ServerRoutingContext? bee));
        Assert.Null(bee!.Standing.MutedUntilUtc);

        Deliver(harness, log);
        Assert.DoesNotContain(MutedLine(BeeSession), SystemLines(log, AdminConnection));

        // One tick: the standing is applied first of all, and the confirmation comes out of that same tick.
        Tick(harness, log);
        Assert.NotNull(bee.Standing.MutedUntilUtc);
        Assert.Contains(MutedLine(BeeSession), SystemLines(log, AdminConnection));
    }

    // Control: the ordinary mute is unchanged. The administrator is told, the row is written, and the
    // player is silenced on the live connection.
    [Fact]
    public async Task AMute_StillConfirmsToTheAdministrator_AndStillSilencesThePlayer()
    {
        using PersistenceTestDatabase db = new();
        using AccountHostHarness harness = AccountHostHarness.OverSqlite(db);
        List<PendingConnectionReply> log = [];
        await JoinAsAdminAsync(harness, log);
        await JoinAsync(harness, log, BeeConnection, BeeSession);

        await MuteAsync(harness, log, BeeSession);
        // The durable half: the sanction is on accounts.muted_until_utc, not only in memory.
        AccountKey? owner = harness.Sqlite!.Lookup(NameOf(BeeSession), null).Existing?.Owner;
        Assert.NotNull(owner);
        Assert.NotNull(harness.Sqlite.ReadStanding(owner!.Value)?.MutedUntilUtc);

        const string Line = "hello everyone";
        Send(harness, log, BeeConnection, ChatChannel.Say, Line);
        Deliver(harness, log);
        Assert.Contains(DedicatedServerHost.MutedText, SystemLines(log, BeeConnection));
        Assert.DoesNotContain(Line, HeardLines(log, AdminConnection));
    }

    // Control: an unmute restores speech, and it confirms too.
    [Fact]
    public async Task AnUnmute_ConfirmsAndRestoresSpeech()
    {
        using PersistenceTestDatabase db = new();
        using AccountHostHarness harness = AccountHostHarness.OverSqlite(db);
        List<PendingConnectionReply> log = [];
        await JoinAsAdminAsync(harness, log);
        await JoinAsync(harness, log, BeeConnection, BeeSession);
        await MuteAsync(harness, log, BeeSession);

        Send(harness, log, AdminConnection, ChatChannel.Command, $"/unmute \"{NameOf(BeeSession)}\"");
        await PersistenceFlush.AwaitAsync(harness.Runtime.PersistenceWrites);
        for (int i = 0; i < 8 && !SystemLines(log, AdminConnection).Contains(UnmutedLine(BeeSession)); i++)
        {
            Tick(harness, log);
        }

        Assert.Contains(UnmutedLine(BeeSession), SystemLines(log, AdminConnection));
        Assert.True(harness.Host.TryGetConnectionContext(BeeConnection, out ServerRoutingContext? bee));
        Assert.Null(bee!.Standing.MutedUntilUtc);

        const string Line = "can you hear me now";
        Send(harness, log, BeeConnection, ChatChannel.Say, Line);
        Deliver(harness, log);
        Assert.Contains(Line, HeardLines(log, AdminConnection));
    }

    // Control: the mute is durable. It survives the player leaving and coming back...
    [Fact]
    public async Task AMute_SurvivesARejoin()
    {
        using PersistenceTestDatabase db = new();
        using AccountHostHarness harness = AccountHostHarness.OverSqlite(db);
        List<PendingConnectionReply> log = [];
        await JoinAsAdminAsync(harness, log);
        await JoinAsync(harness, log, BeeConnection, BeeSession);
        await MuteAsync(harness, log, BeeSession);

        harness.Host.ReleaseConnection(BeeConnection);
        await SettleAsync(harness, log, 2);
        await JoinAsync(harness, log, BeeConnection, BeeSession);

        List<PendingConnectionReply> after = [];
        const string Line = "back again";
        Send(harness, log, BeeConnection, ChatChannel.Say, Line);
        Deliver(harness, after);
        log.AddRange(after);
        Assert.Contains(DedicatedServerHost.MutedText, SystemLines(after, BeeConnection));
        Assert.DoesNotContain(Line, HeardLines(log, AdminConnection));
    }

    // ...and a restart of the server, which only accounts.muted_until_utc can carry.
    [Fact]
    public async Task AMute_SurvivesARestart()
    {
        using PersistenceTestDatabase db = new();
        List<PendingConnectionReply> log = [];
        using (AccountHostHarness first = AccountHostHarness.OverSqlite(db))
        {
            await JoinAsAdminAsync(first, log);
            await JoinAsync(first, log, BeeConnection, BeeSession);
            await MuteAsync(first, log, BeeSession);
            await SettleAsync(first, log, 2);
        }

        using AccountHostHarness restarted = AccountHostHarness.OverSqlite(db);
        List<PendingConnectionReply> after = [];
        await JoinAsync(restarted, after, BeeConnection, BeeSession);

        const string Line = "still here";
        Send(restarted, after, BeeConnection, ChatChannel.Say, Line);
        Deliver(restarted, after);
        Assert.Contains(DedicatedServerHost.MutedText, SystemLines(after, BeeConnection));
    }

    // Control: the gate is the mute and nothing else. A player nobody muted is heard, and a mute of
    // somebody else does not touch them.
    [Fact]
    public async Task AnUnmutedPlayer_IsUnaffectedByAMuteOfSomebodyElse()
    {
        using PersistenceTestDatabase db = new();
        using AccountHostHarness harness = AccountHostHarness.OverSqlite(db);
        List<PendingConnectionReply> log = [];
        await JoinAsAdminAsync(harness, log);
        await JoinAsync(harness, log, BeeConnection, BeeSession);
        await JoinAsync(harness, log, CeeConnection, CeeSession);
        await MuteAsync(harness, log, BeeSession);

        const string Line = "nobody muted me";
        Send(harness, log, CeeConnection, ChatChannel.Say, Line);
        Deliver(harness, log);
        Assert.Contains(Line, HeardLines(log, AdminConnection));
        Assert.DoesNotContain(DedicatedServerHost.MutedText, SystemLines(log, CeeConnection));
    }
}
