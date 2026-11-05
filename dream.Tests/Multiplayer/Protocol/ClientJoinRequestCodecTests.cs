using System.Text;
using Dream.Multiplayer.Protocol;
using Dream.Simulation.Characters;
using Dream.Tests.Support;
using Xunit;

namespace Dream.Tests.Multiplayer.Protocol;

// Wave G C0 (protocol v9): the optional accountToken on the join request.
public sealed class ClientJoinRequestCodecTests
{
    private static readonly TransportJsonCodec Codec = new();

    private static string Encode(ClientJoinRequestMessage message) =>
        Encoding.UTF8.GetString(Codec.Encode(
            new TransportEnvelope(TransportEnvelope.CurrentProtocolVersion, message.MessageKind, 1),
            message));

    private static ClientJoinRequestMessage Decode(string json) =>
        Assert.IsType<ClientJoinRequestMessage>(Codec.DecodeFromClient(Encoding.UTF8.GetBytes(json)).Message);

    [Fact]
    public void JoinRequest_WithoutAToken_OmitsTheField()
    {
        string json = Encode(new ClientJoinRequestMessage("session-a", "Bob"));

        Assert.Equal(
            "{\"protocolVersion\":21,\"messageKind\":\"client.join-request\",\"sequence\":1,\"payload\":{\"requestedSessionId\":\"session-a\",\"displayName\":\"Bob\"}}",
            json);
        Assert.DoesNotContain("accountToken", json, StringComparison.Ordinal);
        Assert.DoesNotContain("resolvedAccount", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void JoinRequest_WithAToken_RoundTrips()
    {
        string token = TestAccountTokens.For("Bob");
        ClientJoinRequestMessage message = new("session-a", "Bob", token);

        string json = Encode(message);
        ClientJoinRequestMessage decoded = Decode(json);

        Assert.Contains($"\"accountToken\":\"{token}\"", json, StringComparison.Ordinal);
        Assert.Equal(token, decoded.AccountToken);
        Assert.Equal("session-a", decoded.RequestedSessionId);
        Assert.Equal("Bob", decoded.DisplayName);
        Assert.Null(decoded.ResolvedAccount);
    }

    [Fact]
    public void JoinRequest_AResolvedAccountFromTheWire_IsIgnored()
    {
        string json = "{\"protocolVersion\":21,\"messageKind\":\"client.join-request\",\"sequence\":1,\"payload\":" +
            "{\"requestedSessionId\":\"s\",\"displayName\":\"Bob\",\"resolvedAccount\":{\"hashHex\":\"" + new string('a', 64) + "\"}}}";

        ClientJoinRequestMessage decoded = Decode(json);

        Assert.Null(decoded.ResolvedAccount);
        Assert.Null(decoded.AccountToken);
    }

    [Fact]
    public void JoinRequest_ResolvedAccountIsNeverWritten()
    {
        ClientJoinRequestMessage message = new("session-a", "Bob") { ResolvedAccount = TestAccountTokens.KeyFor("Bob") };

        Assert.DoesNotContain("resolved", Encode(message), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void JoinRequest_ToString_HoldsNoToken_AndNoAccount()
    {
        string token = TestAccountTokens.For("Bob");
        ClientJoinRequestMessage message = new("session-a", "Bob", token) { ResolvedAccount = TestAccountTokens.KeyFor("Bob") };

        string text = message.ToString();

        Assert.DoesNotContain(token, text, StringComparison.Ordinal);
        Assert.DoesNotContain(TestAccountTokens.KeyFor("Bob").HashHex, text, StringComparison.Ordinal);
        Assert.DoesNotContain("AccountToken", text, StringComparison.Ordinal);
        Assert.Contains("session-a", text, StringComparison.Ordinal);
        Assert.Contains("Bob", text, StringComparison.Ordinal);
    }
}
