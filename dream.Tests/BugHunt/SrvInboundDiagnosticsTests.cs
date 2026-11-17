using System.Text;
using Xunit;
using Dream.Multiplayer.Diagnostics;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Transport;
using Dream.Server;
using static Dream.Tests.Server.ServerConnectionTestFixtures;

namespace Dream.Tests.BugHunt;

// BH-NET-2: a player.action whose payload the client left out decodes to a null Payload. The
// action handler is written for that (it rejects "A null action payload" by name), but the
// server's inbound diagnostic ran first and dereferenced the payload, so with network diagnostics
// on a client could fault the server's inbound path with one message.
public sealed class SrvInboundDiagnosticsTests
{
    [Fact]
    public async Task APlayerActionWithNoPayload_IsLoggedAndRejected_NotThrown()
    {
        List<(string Category, string Message)> lines = [];
        using DedicatedServerHost host = DedicatedServerHost.CreateRuntimeAttached(persistence: null);
        GatedTransport transport = new("bughunt-net2");
        await using ServerConnection connection = new(
            transport,
            host,
            new TransportJsonCodec(),
            new LengthPrefixedMessageFramer(),
            WithoutLiveness);

        transport.Feed(new LengthPrefixedMessageFramer().Frame(Encoding.UTF8.GetBytes(
            $"{{\"protocolVersion\":{TransportEnvelope.CurrentProtocolVersion},\"messageKind\":\"player.action\"," +
            "\"sequence\":1,\"payload\":{\"sessionId\":{\"value\":\"s-nobody\"},\"actionSequence\":1}}")));

        // The capture sink is thread-static and the fed frame is already buffered, so the receive
        // and its logging run inline on this thread.
        using (NetworkDiagnosticsLog.CaptureOnCurrentThread((category, message) => lines.Add((category, message))))
        {
            Assert.True(NetworkDiagnosticsLog.IsEnabled);
            Assert.True(await connection.ProcessIncomingAsync());
        }

        Assert.Contains(lines, line => line.Category == "server.rx" && line.Message.Contains("kind=player.action", StringComparison.Ordinal));
        Assert.False(connection.IsFaulted);
    }
}
