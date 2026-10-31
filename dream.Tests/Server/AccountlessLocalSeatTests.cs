using Dream.Client.Startup;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Server;
using Dream.Simulation.Runtime;
using Xunit;

namespace Dream.Tests.Server;

public sealed class AccountlessLocalSeatTests
{
    // Only a host with accounts keeps the server's own seat (PlayerSessionId.LocalDefault) off the network;
    // tests and tools run hosts without accounts and join under that session id.
    [Fact]
    public void AnAccountlessHost_StillAcceptsAJoinForTheLocalSessionId()
    {
        ProtocolRouter router = new(
            [new ClientHelloHandler(), new JoinRequestHandler(), new PlayerCommandHandler(), new PlayerActionHandler(), new WorldResyncRequestHandler()],
            Array.Empty<IClientMessageHandler>());
        using DedicatedServerHost host = new(RuntimeAuthoritativeSessionHost.CreateDefault(), router);
        Assert.False(host.Runtime!.HasAccounts);

        IReadOnlyList<IServerToClientMessage> replies = host.HandleClientMessage(
            "10.0.0.2:4000",
            new ClientJoinRequestMessage(PlayerSessionId.LocalDefault.Value, "Someone"));

        Assert.IsType<JoinAcceptedMessage>(Assert.Single(replies));
    }

    [Theory]
    [InlineData("localhost", true)]
    [InlineData("LOCALHOST.", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("127.0.0.5", true)]
    [InlineData("[::1]", true)]
    [InlineData("::1", true)]
    [InlineData("example.com", false)]
    [InlineData("10.0.0.2", false)]
    [InlineData("", false)]
    public void LoopbackHost_RecognizesEveryWayToSpellThisMachine(string host, bool expected)
    {
        Assert.Equal(expected, LoopbackHost.IsLoopback(host));
    }
}
