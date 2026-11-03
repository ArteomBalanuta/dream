using System.Text;
using OpenTK.Mathematics;
using Xunit;
using Dream.Multiplayer.Protocol;
using Dream.Server;
using Dream.Simulation.Runtime;

namespace Dream.Tests.Server;

// M1-A: chat on the server. The handler validates and rate limits, ChatRouter picks the audience,
// and the host delivers through its pending connection replies.
public sealed class ChatServerTests
{
    private const string A = "connection-a";
    private const string B = "connection-b";

    [Fact]
    public void WireMessage_HasNoSenderField_AndTheNameComesFromTheServer()
    {
        TransportJsonCodec codec = new();
        ClientChatMessage message = new(ChatChannel.World, "hi");
        string json = Encoding.UTF8.GetString(codec.Encode(
            new TransportEnvelope(TransportEnvelope.CurrentProtocolVersion, message.MessageKind, 1), message));
        Assert.DoesNotContain("from", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sender", json, StringComparison.OrdinalIgnoreCase);

        using DedicatedServerHost host = CreateHost(new ManualClock());
        Join(host, A, "session-a", "Alice");
        Join(host, B, "session-b", "Bob");

        host.HandleClientMessage(A, message);

        List<PendingConnectionReply> replies = Drain(host);
        Assert.Equal(2, replies.Count);
        Assert.All(replies, r => Assert.Equal("Alice", Assert.IsType<ServerChatMessage>(r.Message).From));
        Assert.Equal([A, B], replies.Select(r => r.ConnectionId).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void ChatBeforeJoin_IsIgnored()
    {
        using DedicatedServerHost host = CreateHost(new ManualClock());

        host.HandleClientMessage(A, new ClientChatMessage(ChatChannel.World, "hello"));

        Assert.Empty(Drain(host));
    }

    [Fact]
    public void Flood_IsLimitedToTheBurst_ThenRefillsOnePerSecond()
    {
        ManualClock clock = new();
        using DedicatedServerHost host = CreateHost(clock);
        Join(host, A, "session-a", "Alice");

        for (int i = 0; i < 20; i++)
        {
            host.HandleClientMessage(A, new ClientChatMessage(ChatChannel.World, "spam " + i));
        }

        Assert.Equal(5, Drain(host).Count);

        clock.Advance(TimeSpan.FromMilliseconds(900));
        host.HandleClientMessage(A, new ClientChatMessage(ChatChannel.World, "too soon"));
        Assert.Empty(Drain(host));

        clock.Advance(TimeSpan.FromMilliseconds(100));
        host.HandleClientMessage(A, new ClientChatMessage(ChatChannel.World, "now"));
        Assert.Single(Drain(host));
    }

    [Fact]
    public void Oversize_TextAndTarget_AreDropped_AndTheLimitItselfIsAccepted()
    {
        using DedicatedServerHost host = CreateHost(new ManualClock());
        Join(host, A, "session-a", "Alice");

        host.HandleClientMessage(A, new ClientChatMessage(ChatChannel.World, new string('x', ClientChatMessage.MaxTextLength + 1)));
        host.HandleClientMessage(A, new ClientChatMessage(ChatChannel.Whisper, "hi", new string('n', ClientChatMessage.MaxTargetLength + 1)));
        Assert.Empty(Drain(host));

        host.HandleClientMessage(A, new ClientChatMessage(ChatChannel.World, new string('x', ClientChatMessage.MaxTextLength)));
        Assert.Single(Drain(host));
    }

    [Fact]
    public void ControlAndBidiCharacters_AreStripped_AndEmptyResultsDropped()
    {
        using DedicatedServerHost host = CreateHost(new ManualClock());
        Join(host, A, "session-a", "Alice");

        host.HandleClientMessage(A, new ClientChatMessage(ChatChannel.World, "he\u0000l\u001Blo‮ ⁦there‏\r\n"));
        ServerChatMessage line = Assert.IsType<ServerChatMessage>(Assert.Single(Drain(host)).Message);
        Assert.Equal("hello there", line.Text);

        host.HandleClientMessage(A, new ClientChatMessage(ChatChannel.World, "‮\u0007​\n  "));
        Assert.Empty(Drain(host));
    }

    [Theory]
    [InlineData((byte)3)]
    [InlineData((byte)6)]
    [InlineData((byte)255)]
    public void InvalidChannel_AndSystem_AreDropped(byte channel)
    {
        using DedicatedServerHost host = CreateHost(new ManualClock());
        Join(host, A, "session-a", "Alice");

        host.HandleClientMessage(A, new ClientChatMessage((ChatChannel)channel, "hello"));

        Assert.Empty(Drain(host));
    }

    // M5: channel 4 is Party now. A sender in no party is told so, and nobody else hears anything.
    [Fact]
    public void PartyChat_FromASenderInNoParty_IsAnsweredWithASystemLine_ToTheSenderOnly()
    {
        using DedicatedServerHost host = CreateHost(new ManualClock());
        Join(host, A, "session-a", "Alice");
        Join(host, B, "session-b", "Bob");

        host.HandleClientMessage(A, new ClientChatMessage((ChatChannel)4, "hello party"));

        PendingConnectionReply reply = Assert.Single(Drain(host));
        Assert.Equal(A, reply.ConnectionId);
        ServerChatMessage line = Assert.IsType<ServerChatMessage>(reply.Message);
        Assert.Equal((ChatChannel.System, ChatRouter.NotInPartyText), (line.Channel, line.Text));
    }

    [Fact]
    public void Whisper_ReachesTheTarget_CaseInsensitively_AndEchoesToTheSender()
    {
        using DedicatedServerHost host = CreateHost(new ManualClock());
        Join(host, A, "session-a", "Alice");
        Join(host, B, "session-b", "Bob");
        Join(host, "connection-c", "session-c", "Carol");

        host.HandleClientMessage(A, new ClientChatMessage(ChatChannel.Whisper, "psst", " bOB "));

        List<PendingConnectionReply> replies = Drain(host);
        Assert.Equal([A, B], replies.Select(r => r.ConnectionId).Order(StringComparer.Ordinal));
        Assert.All(replies, r =>
        {
            ServerChatMessage line = Assert.IsType<ServerChatMessage>(r.Message);
            Assert.Equal(ChatChannel.Whisper, line.Channel);
            Assert.Equal("Alice", line.From);
        });
    }

    [Fact]
    public void Whisper_ToAnOfflineName_GetsASystemReplyToTheSenderOnly()
    {
        using DedicatedServerHost host = CreateHost(new ManualClock());
        Join(host, A, "session-a", "Alice");
        Join(host, B, "session-b", "Bob");

        host.HandleClientMessage(A, new ClientChatMessage(ChatChannel.Whisper, "psst", "Nobody"));

        PendingConnectionReply reply = Assert.Single(Drain(host));
        Assert.Equal(A, reply.ConnectionId);
        ServerChatMessage line = Assert.IsType<ServerChatMessage>(reply.Message);
        Assert.Equal(ChatChannel.System, line.Channel);
        Assert.Equal("", line.From);
    }

    [Fact]
    public void Say_ReachesOnlyThoseWithin30Meters_AndTheSender()
    {
        ChatParticipant sender = new(A, "Alice", new Vector3(0, 0, 0));
        ChatParticipant near = new(B, "Bob", new Vector3(30, 0, 0));
        ChatParticipant far = new("connection-c", "Carol", new Vector3(30.1f, 0, 0));
        List<PendingConnectionReply> replies = [];

        ChatRouter.Route(sender, new ClientChatMessage(ChatChannel.Say, "hey"), [sender, near, far], 9UL, replies);

        Assert.Equal([A, B], replies.Select(r => r.ConnectionId).Order(StringComparer.Ordinal));

        replies.Clear();
        ChatRouter.Route(sender, new ClientChatMessage(ChatChannel.World, "all"), [sender, near, far], 9UL, replies);
        Assert.Equal(3, replies.Count);
    }

    [Fact]
    public void WhisperTarget_FoldsAsciiCaseOnly()
    {
        ChatParticipant sender = new(A, "Alice", Vector3.Zero);
        ChatParticipant other = new(B, "Émile", Vector3.Zero);
        List<PendingConnectionReply> replies = [];

        ChatRouter.Route(sender, new ClientChatMessage(ChatChannel.Whisper, "x", "émile"), [sender, other], 1UL, replies);

        Assert.Equal(ChatChannel.System, Assert.IsType<ServerChatMessage>(Assert.Single(replies).Message).Channel);
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
