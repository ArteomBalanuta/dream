using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Xunit;
using Dream.Client.Networking;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Transport;
using Dream.Server;
using Dream.Tests.Utilities;

namespace Dream.Tests.Server;

// NETR-7 over real loopback TCP: a peer that declares a frame above the limit for its direction
// is disconnected with a logged reason, on the server and on the client, and the other side goes on.
public class DirectionalFrameLimitLoopbackTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task Server_ClosesAClientThatDeclaresAFrameAboveTheClientCap_WithAReason()
    {
        DedicatedServerHost host = DedicatedServerHost.CreateRuntimeAttached(persistence: null);
        try
        {
            await using DedicatedServerService service = new(
                host,
                new TcpServerTransport(IPAddress.Loopback, 0),
                new TransportJsonCodec(),
                FrameLimitOptions.Default.CreateServerFramer(),
                ServerConnectionTestFixtures.WithoutLiveness);
            TaskCompletionSource<ServerConnectionError> error = new(TaskCreationOptions.RunContinuationsAsynchronously);
            service.ConnectionError += reported => error.TrySetResult(reported);
            service.Start();

            Task<ServerConnection> accept = service.AcceptOnThreadPoolAsync();
            using TcpClient hostile = new() { NoDelay = true };
            await hostile.ConnectAsync(IPAddress.Loopback, service.Port);
            await accept.WaitAsync(Timeout);
            NetworkStream stream = hostile.GetStream();

            // A 64 KB + 1 frame header followed by a little of its "payload".
            byte[] chunk = new byte[256];
            BinaryPrimitives.WriteInt32LittleEndian(chunk, FrameLimitOptions.DefaultClientToServerMaxPayloadBytes + 1);
            await stream.WriteAsync(chunk);

            ServerConnectionError reported = await error.Task.WaitAsync(Timeout);
            Assert.Equal("error", reported.Kind);
            Assert.Contains(
                $"exceeds configured maximum {FrameLimitOptions.DefaultClientToServerMaxPayloadBytes} bytes",
                reported.Reason,
                StringComparison.Ordinal);

            // The connection is released and its socket closed: the peer reads to the end.
            await WaitUntilAsync(() => service.ConnectionCount == 0, "the connection was never removed");
            byte[] sink = new byte[4096];
            using CancellationTokenSource readTimeout = new(Timeout);
            while (await stream.ReadAsync(sink, readTimeout.Token) > 0)
            {
            }

            // The server still takes the next client.
            Task<ServerConnection> next = service.AcceptOnThreadPoolAsync();
            using TcpClient polite = new();
            await polite.ConnectAsync(IPAddress.Loopback, service.Port);
            await next.WaitAsync(Timeout);
            Assert.Equal(1, service.ConnectionCount);
        }
        finally
        {
            host.Dispose();
        }
    }

    [Fact]
    public async Task Client_DisconnectsFromAServerThatDeclaresAFrameAboveTheServerCap_WithAReason()
    {
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Task<TcpClient> accept = listener.AcceptTcpClientAsync();

        // The production connector builds the client framer from the limits.
        RemoteSessionConnector connector = new(FrameLimitOptions.Default);
        Task<RemoteClientSession> connect = connector.ConnectAsync(
            new RemoteClientOptions("127.0.0.1", port, "limit-client", "limit-client", "frame-limit-test")).AsTask();
        using TcpClient server = await accept.WaitAsync(Timeout);
        byte[] header = new byte[64];
        BinaryPrimitives.WriteInt32LittleEndian(header, FrameLimitOptions.DefaultServerToClientMaxPayloadBytes + 1);
        await server.GetStream().WriteAsync(header);

        await using RemoteClientSession session = await connect.WaitAsync(Timeout);
        await WaitUntilAsync(() => !session.IsConnected, "the client never disconnected");
        Assert.NotNull(session.DisconnectReason);
        Assert.Contains(
            $"exceeds configured maximum {FrameLimitOptions.DefaultServerToClientMaxPayloadBytes} bytes",
            session.DisconnectReason,
            StringComparison.Ordinal);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string failure)
    {
        DateTime deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, failure);
            await Task.Delay(10);
        }
    }
}
