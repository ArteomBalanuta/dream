using System.Net;
using System.Net.Sockets;
using System.Text;
using Xunit;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Transport;
using Dream.Server;

namespace Dream.Tests.Server;

// A connection that sends something the server cannot decode is logged once and closed, without
// a reply and without faulting the connection's fire-and-forget receive task.
public class DedicatedServerServiceConnectionErrorTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task MalformedFrame_IsLoggedAsAnError_AndClosesOnlyThatConnection_WithoutAReply()
    {
        DedicatedServerHost host = DedicatedServerHost.CreateRuntimeAttached(persistence: null);
        await using DedicatedServerService service = new(
            host,
            new TcpServerTransport(IPAddress.Loopback, 0),
            new TransportJsonCodec(),
            new LengthPrefixedMessageFramer());
        service.Start();
        List<ServerConnectionError> errors = [];
        service.ConnectionError += error =>
        {
            lock (errors)
            {
                errors.Add(error);
            }
        };

        using TcpClient client = new();
        Task<ServerConnection> accept = service.AcceptNextClientAsync().AsTask();
        await client.ConnectAsync(IPAddress.Loopback, service.Port);
        ServerConnection connection = await accept.WaitAsync(Timeout);
        NetworkStream stream = client.GetStream();
        await stream.WriteAsync(new LengthPrefixedMessageFramer().Frame(Encoding.UTF8.GetBytes("{\"not\":\"an envelope\"")));

        byte[] buffer = new byte[256];
        int received = 0;
        using (CancellationTokenSource readTimeout = new(Timeout))
        {
            try
            {
                int read;
                while ((read = await stream.ReadAsync(buffer, readTimeout.Token)) > 0)
                {
                    received += read;
                }
            }
            catch (IOException)
            {
                // A reset is also a close.
            }
        }

        Assert.Equal(0, received);
        DateTime deadline = DateTime.UtcNow + Timeout;
        while (service.ConnectionCount != 0 || errors.Count == 0)
        {
            Assert.True(DateTime.UtcNow < deadline, "the malformed connection was never dropped");
            await Task.Delay(10);
        }

        lock (errors)
        {
            ServerConnectionError error = Assert.Single(errors);
            Assert.Equal(connection.ConnectionId, error.ConnectionId);
            Assert.Equal("error", error.Kind);
            Assert.IsNotType<ProtocolVersionMismatchException>(error.Exception);
        }
    }

    [Theory]
    [InlineData("{\"protocolVersion\":1,\"messageKind\":\"client.hello\"}", 1)]
    [InlineData("{\"messageKind\":\"client.hello\",\"payload\":{\"protocolVersion\":9},\"protocolVersion\":3}", 3)]
    public void TryReadProtocolVersion_ReadsTheTopLevelVersionOnly(string json, int expected)
    {
        Assert.True(ProtocolVersionMismatchException.TryReadProtocolVersion(Encoding.UTF8.GetBytes(json), out int version));
        Assert.Equal(expected, version);
    }

    [Theory]
    [InlineData("{\"messageKind\":\"client.hello\",\"payload\":{\"protocolVersion\":9}}")]
    [InlineData("{\"protocolVersion\":\"1\"}")]
    [InlineData("[1]")]
    [InlineData("{\"protocolVersion\":")]
    public void TryReadProtocolVersion_RejectsPayloadsWithoutAnIntegerVersion(string json)
    {
        Assert.False(ProtocolVersionMismatchException.TryReadProtocolVersion(Encoding.UTF8.GetBytes(json), out _));
    }

    [Fact]
    public void ReadForeignProtocolVersion_IgnoresTheCurrentVersion()
    {
        byte[] current = Encoding.UTF8.GetBytes($"{{\"protocolVersion\":{TransportEnvelope.CurrentProtocolVersion}}}");
        Assert.Null(ProtocolVersionMismatchException.ReadForeignProtocolVersion(current));
        Assert.Equal(1, ProtocolVersionMismatchException.ReadForeignProtocolVersion("{\"protocolVersion\":1}"u8));
    }
}
