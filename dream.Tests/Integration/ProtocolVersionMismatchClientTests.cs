using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Xunit;
using Dream.Client.Networking;
using Dream.Client.Startup;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Transport;
using Dream.Server;
using Dream.Tests.Multiplayer.Protocol;

namespace Dream.Tests.Integration;

// A protocol v2 client against a fake server over real loopback TCP. The fake reads the client's
// hello and join request, then answers with a ServerHello from another protocol version: either
// a whole v1 envelope (what a real v1 server sends) or a v2 envelope whose hello reports another
// version. The connect must fail with a reason that names both versions, and that reason must be
// what the connection screen shows.
[Collection(TimingSensitiveCollection.Name)]
public class ProtocolVersionMismatchClientTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task ConnectAsync_AgainstAV1Server_FailsWithAProtocolMismatch()
    {
        await using FakeServer server = FakeServer.Start(FakeServer.V1HelloFrame());

        ProtocolVersionMismatchException error = await Assert.ThrowsAsync<ProtocolVersionMismatchException>(async () =>
        {
            await using RemoteClientSession session = await RemoteClientSession.ConnectAsync(
                new RemoteClientOptions("127.0.0.1", server.Port, "session-a", "player-a", "mismatch-test"));
        }).WaitAsync(Timeout);

        Assert.Equal("Server uses protocol v1; this client uses v2.", error.Message);
        Assert.Equal(1, error.PeerVersion);
        Assert.Equal(TransportEnvelope.CurrentProtocolVersion, error.LocalVersion);
        await server.WaitForClientMessagesAsync(2);
    }

    [Fact]
    public async Task ConnectAsync_WhenTheServerHelloReportsAnotherVersion_FailsWithAProtocolMismatch()
    {
        await using FakeServer server = FakeServer.Start(FakeServer.V2EnvelopeHelloFrame(reportedVersion: 3));

        ProtocolVersionMismatchException error = await Assert.ThrowsAsync<ProtocolVersionMismatchException>(async () =>
        {
            await using RemoteClientSession session = await RemoteClientSession.ConnectAsync(
                new RemoteClientOptions("127.0.0.1", server.Port, "session-a", "player-a", "mismatch-test"));
        }).WaitAsync(Timeout);

        Assert.Equal("Server uses protocol v3; this client uses v2.", error.Message);
        Assert.Equal(3, error.PeerVersion);
    }

    [Fact]
    public async Task ConnectAsync_AgainstAV2Server_StillConnects()
    {
        await using FakeServer server = FakeServer.Start(FakeServer.V2EnvelopeHelloFrame(TransportEnvelope.CurrentProtocolVersion));

        await using RemoteClientSession session = await RemoteClientSession.ConnectAsync(
            new RemoteClientOptions("127.0.0.1", server.Port, "session-a", "player-a", "mismatch-test")).AsTask().WaitAsync(Timeout);

        Assert.True(session.IsConnected);
        Assert.Null(session.DisconnectReason);
        Assert.True(session.TryDequeueInbound(out IServerToClientMessage? hello));
        Assert.Equal(TransportEnvelope.CurrentProtocolVersion, Assert.IsType<ServerHelloMessage>(hello).ProtocolVersion);
    }

    [Fact]
    public async Task ConnectionScreen_ShowsTheProtocolMismatch_InsteadOfABareDisconnect()
    {
        await using FakeServer server = FakeServer.Start(FakeServer.V1HelloFrame());
        NetworkSessionCoordinator coordinator = new(new ReadyLocalServer(), new RemoteSessionConnector());

        coordinator.BeginConnect(new ClientConnectionOptions("127.0.0.1", server.Port.ToString(System.Globalization.CultureInfo.InvariantCulture), "player-a"));
        await coordinator.WaitForPendingConnectionAsync().WaitAsync(Timeout);

        Assert.Equal(ConnectionFlowState.Failed, coordinator.State);
        Assert.Equal("Server uses protocol v1; this client uses v2.", coordinator.StatusMessage);
        Assert.False(coordinator.TryTakeConnectedSession(out _));
    }

    [Fact]
    public async Task ReceiveLoop_StopsOnALateV1Frame_WithAReason_AndDisposesCleanly()
    {
        ScriptedConnection connection = new(FakeServer.V1HelloFrame());
        RemoteClientSession session = new(
            connection,
            new TransportJsonCodec(),
            new LengthPrefixedMessageFramer(),
            new RemoteClientOptions("127.0.0.1", 1, "session-a", "player-a", "mismatch-test"));
        session.StartBackgroundReceive();

        DateTime deadline = DateTime.UtcNow + Timeout;
        while (session.IsConnected)
        {
            Assert.True(DateTime.UtcNow < deadline, "the receive loop never stopped");
            await Task.Delay(10);
        }

        Assert.Equal("Server uses protocol v1; this client uses v2.", session.DisconnectReason);
        Assert.False(session.TryDequeueInbound(out _));
        // The receive loop ended normally, so disposing does not resurface the decode error.
        await session.DisposeAsync();
    }

    // Returns one scripted chunk, then waits until cancelled, like an idle open socket.
    private sealed class ScriptedConnection(byte[] chunk) : ITransportConnection
    {
        private byte[]? _chunk = chunk;

        public string ConnectionId => "scripted";

        public async ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            byte[]? next = Interlocked.Exchange(ref _chunk, null);
            if (next != null)
            {
                next.CopyTo(buffer);
                return next.Length;
            }

            await Task.Delay(System.Threading.Timeout.Infinite, cancellationToken);
            return 0;
        }

        public ValueTask SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ReadyLocalServer : ILocalServerHandle
    {
        public bool IsReady => true;

        public int BoundPort => 0;

        public string? ErrorMessage => null;

        public DedicatedServerPerformanceSnapshot LatestPerformance => DedicatedServerPerformanceSnapshot.Empty;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    // Accepts one client, reads its frames, and sends the given reply after the first one.
    private sealed class FakeServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _shutdown = new();
        private readonly Task _serveTask;
        private int _clientMessages;

        private FakeServer(TcpListener listener, byte[] replyFrame)
        {
            _listener = listener;
            _serveTask = Task.Run(() => ServeAsync(replyFrame, _shutdown.Token));
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public static FakeServer Start(byte[] replyFrame)
        {
            TcpListener listener = new(IPAddress.Loopback, 0);
            listener.Start();
            return new FakeServer(listener, replyFrame);
        }

        // What a protocol v1 server sends: v1 serializer options and protocolVersion 1.
        public static byte[] V1HelloFrame()
        {
            ServerHelloMessage hello = new("legacy-v1-server", ProtocolVersion: 1, CurrentServerTick: 0);
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(
                new { protocolVersion = 1, messageKind = hello.MessageKind, sequence = 1UL, payload = hello },
                ProtocolWireOptions.V1);
            return new LengthPrefixedMessageFramer().Frame(payload);
        }

        public static byte[] V2EnvelopeHelloFrame(int reportedVersion)
        {
            ServerHelloMessage hello = new("fake-server", reportedVersion, CurrentServerTick: 0);
            byte[] payload = new TransportJsonCodec().Encode(
                new TransportEnvelope(TransportEnvelope.CurrentProtocolVersion, hello.MessageKind, 1),
                hello);
            return new LengthPrefixedMessageFramer().Frame(payload);
        }

        public async Task WaitForClientMessagesAsync(int count)
        {
            DateTime deadline = DateTime.UtcNow + Timeout;
            while (Volatile.Read(ref _clientMessages) < count)
            {
                Assert.True(DateTime.UtcNow < deadline, $"the fake server read only {_clientMessages} client frames");
                await Task.Delay(10);
            }
        }

        public async ValueTask DisposeAsync()
        {
            _shutdown.Cancel();
            _listener.Stop();
            try
            {
                await _serveTask;
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or IOException or ObjectDisposedException)
            {
            }

            _shutdown.Dispose();
        }

        private async Task ServeAsync(byte[] replyFrame, CancellationToken cancellationToken)
        {
            using TcpClient client = await _listener.AcceptTcpClientAsync(cancellationToken);
            NetworkStream stream = client.GetStream();
            LengthPrefixedMessageFramer framer = new();
            TransportJsonCodec codec = new();
            List<byte> pending = [];
            byte[] chunk = new byte[4096];
            bool replied = false;
            while (!cancellationToken.IsCancellationRequested)
            {
                int read = await stream.ReadAsync(chunk, cancellationToken);
                if (read == 0)
                {
                    return;
                }

                pending.AddRange(chunk.AsSpan(0, read).ToArray());
                while (framer.TryDeframe(pending.ToArray(), out byte[]? payload, out int consumed))
                {
                    pending.RemoveRange(0, consumed);
                    Assert.IsAssignableFrom<IClientToServerMessage>(codec.Decode(payload!).Message);
                    Interlocked.Increment(ref _clientMessages);
                    if (!replied)
                    {
                        replied = true;
                        await stream.WriteAsync(replyFrame, cancellationToken);
                    }
                }
            }
        }
    }
}
