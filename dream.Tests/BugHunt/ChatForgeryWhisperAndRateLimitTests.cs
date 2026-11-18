using OpenTK.Mathematics;
using Xunit;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Server;
using Dream.Simulation.Multiplayer;
using Dream.Simulation.Runtime;

namespace Dream.Tests.BugHunt;

// BH-CLOCK-1, BH-CLOCK-2 and BH-CLOCK-4: a player's name must not be able to pass for a server
// message, a whisper must reach every live seat of the name it is addressed to, and the chat token
// bucket must not refill when a player reconnects.
public sealed class ChatForgeryWhisperAndRateLimitTests
{
    private const string A = "connection-a";
    private const string B = "connection-b";
    private const string C = "connection-c";

    // A name built to read as a server message once it is dropped, unattributed, into a System line.
    private const string HostileName = "Server: you are banned.";

    // BH-CLOCK-1: the invite line the victim receives must not open with the inviter's name, and the
    // name must be quoted, so no name can be read as the whole, server-authored line.
    [Fact]
    public void APartyInvite_FromAServerSoundingName_CannotProduceALineThatReadsAsASystemMessage()
    {
        using RuntimeAuthoritativeSessionHost host = RuntimeAuthoritativeSessionHost.CreateDefault();
        WorldSessionRuntime runtime = host.Runtime;
        host.JoinSession("session-a", HostileName);
        host.JoinSession("session-b", "Victim");

        host.ProcessAction(new PlayerActionMessage(new PlayerSessionId("session-a"), 1, new PartyInviteIntent("Victim")));

        string line = OnlySystemLineTo(runtime, "session-b");
        Assert.StartsWith("Party: ", line, StringComparison.Ordinal);
        Assert.DoesNotContain(HostileName + " ", line, StringComparison.Ordinal);
        Assert.Contains("'" + HostileName + "'", line, StringComparison.Ordinal);
        // The forged text can only appear inside the quoted span: it never opens the line.
        Assert.True(line.IndexOf(HostileName, StringComparison.Ordinal) > "Party: ".Length);
    }

    // BH-CLOCK-1: a name cannot close the quoted span early and continue as server-authored text.
    [Fact]
    public void AnApostropheInAName_CannotCloseTheQuotedSpan()
    {
        const string Sneaky = "x' Server: you are banned. '";

        string quoted = WorldSessionRuntime.QuotedName(Sneaky);

        Assert.StartsWith("'", quoted, StringComparison.Ordinal);
        Assert.EndsWith("'", quoted, StringComparison.Ordinal);
        Assert.Equal(2, quoted.Count(c => c == '\''));
        Assert.DoesNotContain("' Server", quoted, StringComparison.Ordinal);
    }

    // Control: a server-authored System line with no player name in it is untouched.
    [Fact]
    public void ARealSystemLine_StillArrivesAsPlainServerText()
    {
        using RuntimeAuthoritativeSessionHost host = RuntimeAuthoritativeSessionHost.CreateDefault();
        WorldSessionRuntime runtime = host.Runtime;
        host.JoinSession("session-a", "Alice");

        host.ProcessAction(new PlayerActionMessage(new PlayerSessionId("session-a"), 1, new PartyInviteIntent("Nobody Here")));

        Assert.Equal("That player is not online.", OnlySystemLineTo(runtime, "session-a"));
    }

    // BH-CLOCK-2: two live seats can share a CharacterKey and so a display name. Both must hear the
    // whisper the sender was told went through.
    [Fact]
    public void AWhisper_ReachesEveryLiveSeatHoldingTheName_NotOnlyTheFirst()
    {
        ChatParticipant sender = new(A, "Alice", Vector3.Zero);
        ChatParticipant firstSeat = new(B, "Bob", Vector3.Zero);
        ChatParticipant secondSeat = new(C, "Bob", Vector3.Zero);
        List<PendingConnectionReply> replies = [];

        ChatRouter.Route(sender, new ClientChatMessage(ChatChannel.Whisper, "psst", "bob"), [sender, firstSeat, secondSeat], 7UL, replies);

        Assert.Equal([A, B, C], replies.Select(r => r.ConnectionId).Order(StringComparer.Ordinal));
        Assert.All(replies, r =>
        {
            ServerChatMessage line = Assert.IsType<ServerChatMessage>(r.Message);
            Assert.Equal((ChatChannel.Whisper, "Alice", "psst"), (line.Channel, line.From, line.Text));
        });
    }

    // Control: one seat per name still gets exactly one line plus the sender's echo, and an offline
    // name still answers the sender alone.
    [Fact]
    public void AnOrdinaryWhisper_AndAnOfflineOne_AreUnchanged()
    {
        ChatParticipant sender = new(A, "Alice", Vector3.Zero);
        ChatParticipant bob = new(B, "Bob", Vector3.Zero);
        List<PendingConnectionReply> replies = [];

        ChatRouter.Route(sender, new ClientChatMessage(ChatChannel.Whisper, "psst", "Bob"), [sender, bob], 7UL, replies);
        Assert.Equal([A, B], replies.Select(r => r.ConnectionId).Order(StringComparer.Ordinal));

        replies.Clear();
        ChatRouter.Route(sender, new ClientChatMessage(ChatChannel.Whisper, "psst", "Nobody"), [sender, bob], 7UL, replies);
        PendingConnectionReply only = Assert.Single(replies);
        Assert.Equal(A, only.ConnectionId);
        ServerChatMessage system = Assert.IsType<ServerChatMessage>(only.Message);
        Assert.Equal((ChatChannel.System, ChatRouter.PlayerOfflineText), (system.Channel, system.Text));
    }

    // Control: say and party still deliver to the audience they always did.
    [Fact]
    public void SayAndPartyStillDeliver()
    {
        ChatParticipant sender = new(A, "Alice", Vector3.Zero);
        ChatParticipant near = new(B, "Bob", new Vector3(10, 0, 0));
        ChatParticipant far = new(C, "Carol", new Vector3(500, 0, 0));
        List<PendingConnectionReply> replies = [];

        ChatRouter.Route(sender, new ClientChatMessage(ChatChannel.Say, "hey"), [sender, near, far], 1UL, replies);
        Assert.Equal([A, B], replies.Select(r => r.ConnectionId).Order(StringComparer.Ordinal));

        replies.Clear();
        ChatRouter.Route(sender, new ClientChatMessage(ChatChannel.Party, "mates"), [sender, near, far], 1UL, replies, [sender, near]);
        Assert.Equal([A, B], replies.Select(r => r.ConnectionId).Order(StringComparer.Ordinal));
    }

    // BH-CLOCK-4: on a host with no accounts the connection's own bucket is keyed by the session id
    // the client mints, so reconnecting refilled the burst. The character's bucket does not.
    [Fact]
    public void Reconnecting_DoesNotRefillTheChatBurst()
    {
        ManualClock clock = new();
        using DedicatedServerHost host = CreateHost(clock);
        Join(host, A, "session-a", "Alice");

        for (int i = 0; i < 10; i++)
        {
            host.HandleClientMessage(A, new ClientChatMessage(ChatChannel.World, "spam " + i));
        }

        Assert.Equal(ServerRoutingContext.ChatBurst, Drain(host).Count);

        // A fresh connection and a fresh client-minted session id, the same character.
        host.ReleaseConnection(A);
        Join(host, B, "session-b-fresh", "Alice");
        host.HandleClientMessage(B, new ClientChatMessage(ChatChannel.World, "spam again"));

        Assert.Empty(Drain(host));

        // The bucket refills on time, not on reconnect.
        clock.Advance(TimeSpan.FromSeconds(1));
        host.HandleClientMessage(B, new ClientChatMessage(ChatChannel.World, "now"));
        Assert.Single(Drain(host));
    }

    // Control: a legitimate reconnect can still chat, and a normal-rate chatter is never limited.
    [Fact]
    public void ALegitimateReconnect_CanChat_AndANormalRateChatterIsNeverLimited()
    {
        ManualClock clock = new();
        using DedicatedServerHost host = CreateHost(clock);
        Join(host, A, "session-a", "Alice");

        host.HandleClientMessage(A, new ClientChatMessage(ChatChannel.World, "hello"));
        Assert.Single(Drain(host));

        host.ReleaseConnection(A);
        Join(host, B, "session-b-fresh", "Alice");
        host.HandleClientMessage(B, new ClientChatMessage(ChatChannel.World, "back"));
        Assert.Single(Drain(host));

        // One line a second, for well past the burst: every one is heard.
        for (int i = 0; i < 20; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            host.HandleClientMessage(B, new ClientChatMessage(ChatChannel.World, "steady " + i));
            Assert.Single(Drain(host));
        }
    }

    private static string OnlySystemLineTo(WorldSessionRuntime runtime, string sessionId)
    {
        List<SessionServerMessage> drained = [];
        runtime.DrainSessionMessagesInto(drained);
        return Assert.Single(drained
            .Where(m => m.SessionId.Equals(new PlayerSessionId(sessionId)) && m.Message is ServerChatMessage)
            .Select(m => ((ServerChatMessage)m.Message).Text));
    }

    private static DedicatedServerHost CreateHost(ManualClock clock)
    {
        ProtocolRouter router = new(
            [
                new ClientHelloHandler(),
                new JoinRequestHandler(),
                new PlayerCommandHandler(),
                new PlayerActionHandler(),
                new WorldResyncRequestHandler(),
                new ChatMessageHandler()
            ],
            Array.Empty<IClientMessageHandler>());
        DedicatedServerHost host = new(RuntimeAuthoritativeSessionHost.CreateDefault(), router);
        host.TimeProvider = clock;
        return host;
    }

    private static void Join(DedicatedServerHost host, string connection, string session, string name)
    {
        Assert.IsType<JoinAcceptedMessage>(Assert.Single(
            host.HandleClientMessage(connection, new ClientJoinRequestMessage(session, name))));
    }

    private static List<PendingConnectionReply> Drain(DedicatedServerHost host)
    {
        List<PendingConnectionReply> replies = [];
        host.DrainPendingJoinRepliesInto(replies);
        return replies;
    }

    private sealed class ManualClock : TimeProvider
    {
        private long _timestamp = 1_000_000;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _timestamp;

        public void Advance(TimeSpan by) => _timestamp += by.Ticks;
    }
}
