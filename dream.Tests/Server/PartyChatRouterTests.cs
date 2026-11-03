using OpenTK.Mathematics;
using Xunit;
using Dream.Multiplayer.Protocol;
using Dream.Server;

namespace Dream.Tests.Server;

// M5: the Party channel in ChatRouter. A line reaches the sender's party members and nobody else; a sender
// in no party is told so and nothing is delivered.
public sealed class PartyChatRouterTests
{
    private static ChatParticipant Who(string connection, string name) => new(connection, name, Vector3.Zero);

    private static List<PendingConnectionReply> Route(
        ChatParticipant sender,
        string text,
        IReadOnlyList<ChatParticipant> everyone,
        IReadOnlyList<ChatParticipant>? party)
    {
        List<PendingConnectionReply> replies = [];
        ChatRouter.Route(sender, new ClientChatMessage(ChatChannel.Party, text), everyone, 7, replies, party);
        return replies;
    }

    [Fact]
    public void APartyLine_ReachesTheMembersOnly_EachOnce()
    {
        ChatParticipant alice = Who("a", "Alice");
        ChatParticipant bob = Who("b", "Bob");
        ChatParticipant carol = Who("c", "Carol");

        List<PendingConnectionReply> replies = Route(alice, "pull left", [alice, bob, carol], [alice, bob]);

        Assert.Equal(["a", "b"], replies.Select(r => r.ConnectionId).Order(StringComparer.Ordinal));
        ServerChatMessage line = Assert.IsType<ServerChatMessage>(replies[0].Message);
        Assert.Equal((ChatChannel.Party, "Alice", "pull left", 7UL), (line.Channel, line.From, line.Text, line.ServerTick));
        Assert.DoesNotContain(replies, r => r.ConnectionId == "c");
    }

    [Fact]
    public void ASenderInNoParty_GetsASystemLine_AndNobodyHearsIt()
    {
        ChatParticipant alice = Who("a", "Alice");
        ChatParticipant bob = Who("b", "Bob");

        List<PendingConnectionReply> replies = Route(alice, "hello?", [alice, bob], party: null);

        PendingConnectionReply only = Assert.Single(replies);
        Assert.Equal("a", only.ConnectionId);
        ServerChatMessage system = Assert.IsType<ServerChatMessage>(only.Message);
        Assert.Equal(ChatChannel.System, system.Channel);
        Assert.Equal(ChatRouter.NotInPartyText, system.Text);
    }

    [Fact]
    public void APartyListThatDoesNotHoldTheSender_IsTreatedAsNoParty()
    {
        ChatParticipant alice = Who("a", "Alice");
        ChatParticipant bob = Who("b", "Bob");
        ChatParticipant carol = Who("c", "Carol");

        List<PendingConnectionReply> replies = Route(alice, "sneaky", [alice, bob, carol], [bob, carol]);

        PendingConnectionReply only = Assert.Single(replies);
        Assert.Equal("a", only.ConnectionId);
        Assert.Equal(ChatChannel.System, Assert.IsType<ServerChatMessage>(only.Message).Channel);
    }

    [Fact]
    public void ADuplicatedConnectionInTheList_HearsTheLineOnce()
    {
        ChatParticipant alice = Who("a", "Alice");
        ChatParticipant bob = Who("b", "Bob");

        List<PendingConnectionReply> replies = Route(alice, "x", [alice, bob], [alice, bob, bob]);

        Assert.Equal(2, replies.Count);
    }
}
