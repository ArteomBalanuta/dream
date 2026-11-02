using System.Text;
using Xunit;
using Dream.Multiplayer.Protocol;

namespace Dream.Tests.Multiplayer.Protocol;

// M1 contract (protocol v14): the chat and character list messages round trip, travel in the right
// direction only, and the list request never prints its account token.
public sealed class ChatContractCodecTests
{
    private static readonly TransportJsonCodec Codec = new();

    private static byte[] Encode<TMessage>(TMessage message, ulong sequence = 1) where TMessage : IProtocolMessage =>
        Codec.Encode(new TransportEnvelope(TransportEnvelope.CurrentProtocolVersion, message.MessageKind, sequence), message);

    [Fact]
    public void ClientChat_RoundTrips_WithAndWithoutATarget()
    {
        ClientChatMessage whisper = new(ChatChannel.Whisper, "hello there", "Bob");
        ClientChatMessage world = new(ChatChannel.World, "hi all");

        DecodedProtocolMessage decoded = Codec.DecodeFromClient(Encode(whisper, 4));

        Assert.Equal("client.chat", decoded.Envelope.MessageKind);
        Assert.Equal(4UL, decoded.Envelope.Sequence);
        Assert.Equal(whisper, Assert.IsType<ClientChatMessage>(decoded.Message));
        byte[] worldBytes = Encode(world);
        Assert.DoesNotContain("\"target\"", Encoding.UTF8.GetString(worldBytes), StringComparison.Ordinal);
        Assert.Equal(world, Assert.IsType<ClientChatMessage>(Codec.DecodeFromClient(worldBytes).Message));
    }

    [Fact]
    public void ServerChat_RoundTrips_AndIsRefusedFromAClient()
    {
        ServerChatMessage original = new(ChatChannel.Say, "Alice", "over here", 1234UL);

        byte[] bytes = Encode(original);

        Assert.Equal(original, Assert.IsType<ServerChatMessage>(Codec.Decode(bytes).Message));
        Assert.True(TransportJsonCodec.IsServerOnlyKind(ServerChatMessage.Kind));
        Assert.Throws<InvalidOperationException>(() => Codec.DecodeFromClient(bytes));
    }

    [Fact]
    public void ClientCharacterListRequest_RoundTrips_AndNeverPrintsTheToken()
    {
        const string Token = "SENTINEL-token-0123456789abcdefghijklmnopqrstuv";
        ClientCharacterListRequest original = new(Token);

        DecodedProtocolMessage decoded = Codec.DecodeFromClient(Encode(original));

        ClientCharacterListRequest request = Assert.IsType<ClientCharacterListRequest>(decoded.Message);
        Assert.Equal(Token, request.AccountToken);
        Assert.DoesNotContain(Token, request.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Token, original.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ServerCharacterList_RoundTrips_AndIsRefusedFromAClient()
    {
        ServerCharacterList original = new([new CharacterListEntry("Alice", 7), new CharacterListEntry("Bob", 1)]);

        byte[] bytes = Encode(original);

        ServerCharacterList list = Assert.IsType<ServerCharacterList>(Codec.Decode(bytes).Message);
        Assert.Equal(original.Characters, list.Characters);
        Assert.True(TransportJsonCodec.IsServerOnlyKind(ServerCharacterList.Kind));
        Assert.Throws<InvalidOperationException>(() => Codec.DecodeFromClient(bytes));
    }
}
