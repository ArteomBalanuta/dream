using System.Net;
using Xunit;
using Dream.Multiplayer.Transport;

namespace Dream.Tests.Multiplayer.Transport;


public class TcpServerTransportTests
{
    [Fact]
    public async Task Start_With_Port_Zero_Exposes_Resolved_Bound_Port()
    {
        await using TcpServerTransport transport = new(IPAddress.Loopback, 0);

        transport.Start();

        Assert.InRange(transport.Port, 1, 65535);
    }

    [Fact]
    public async Task AcceptedAndConnectedConnections_DisableNagle()
    {
        // Small command/snapshot writes were batched by Nagle and delayed ACK.
        await using TcpServerTransport server = new(IPAddress.Loopback, 0);
        server.Start();
        ValueTask<ITransportConnection> accept = server.AcceptClientAsync();

        await using TcpClientTransport client = await TcpClientTransport.ConnectAsync("127.0.0.1", server.Port);
        await using ITransportConnection accepted = await accept;

        Assert.True(client.NoDelay);
        Assert.True(Assert.IsType<TcpClientTransport>(accepted).NoDelay);
    }
}
