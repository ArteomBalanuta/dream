using System.Net;
using System.Net.Sockets;
using Xunit;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Transport;
using Dream.Server;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;

namespace Dream.Tests.Server;

// The live server path over real loopback TCP: the host ServerBootstrap builds for the dedicated
// and integrated servers, the real DedicatedServerService and its ServerConnections, ticked by
// the test so several join requests are read before the same tick. Joins that land in one tick
// must give the same seats, actor ids and spawns whatever order the server read them in, and a
// client must hear its JoinAccepted before its first snapshot.
public class LiveServerDeferredJoinLoopbackTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private static readonly string[] Sessions = ["session-a", "session-b", "session-c"];

    [Fact]
    public async Task SameTickJoins_ReadInDifferentOrders_GiveIdenticalSeatsActorIdsAndSpawns()
    {
        string[] reversed = await RunSameTickJoinsAsync(["session-c", "session-b", "session-a"]);
        string[] forward = await RunSameTickJoinsAsync(["session-a", "session-b", "session-c"]);

        Assert.Equal("primary=session-a", reversed[0]);
        Assert.Equal(forward, reversed);
    }

    [Fact]
    public async Task DeferredJoin_EachClientGetsOneJoinAccepted_BeforeAnySnapshot()
    {
        await using ServerUnderTest server = ServerUnderTest.Start();
        List<RawClient> clients = [];
        try
        {
            foreach (string session in new[] { "session-c", "session-a", "session-b" })
            {
                RawClient client = await server.ConnectAsync(session);
                clients.Add(client);
                await client.SendJoinAndWaitUntilReadAsync();
            }

            await server.TickAsync();
            await server.TickAsync();
            foreach (RawClient client in clients)
            {
                await client.WaitForAsync(messages => messages.OfType<ServerSnapshotMessage>().Count() >= 2, "two snapshots");
                IReadOnlyList<IServerToClientMessage> messages = client.Messages;
                int joinIndex = IndexOf<JoinAcceptedMessage>(messages);
                int snapshotIndex = IndexOf<ServerSnapshotMessage>(messages);
                Assert.True(joinIndex >= 0, $"{client.SessionId} never received JoinAccepted.");
                Assert.True(joinIndex < snapshotIndex, $"{client.SessionId} received a snapshot before JoinAccepted.");
                Assert.Single(messages.OfType<JoinAcceptedMessage>());
                Assert.Empty(messages.OfType<JoinRejectedMessage>());
                Assert.Equal(client.SessionId, messages.OfType<JoinAcceptedMessage>().Single().SessionId);
            }
        }
        finally
        {
            await DisposeAllAsync(clients);
        }
    }

    [Fact]
    public async Task SameTickJoins_ForOneSession_AcceptOneConnection_AndRejectTheOther()
    {
        await using ServerUnderTest server = ServerUnderTest.Start();
        RawClient first = await server.ConnectAsync("session-x");
        RawClient second = await server.ConnectAsync("session-x");
        try
        {
            await first.SendJoinAndWaitUntilReadAsync();
            await second.SendJoinAndWaitUntilReadAsync();

            await server.TickAsync();
            RawClient[] clients = [first, second];
            foreach (RawClient client in clients)
            {
                await client.WaitForAsync(
                    messages => messages.Any(message => message is JoinAcceptedMessage or JoinRejectedMessage),
                    "a join reply");
            }

            Assert.Equal(1, clients.Count(client => client.Messages.OfType<JoinAcceptedMessage>().Count() == 1));
            RawClient rejected = Assert.Single(clients, client => client.Messages.OfType<JoinRejectedMessage>().Any());
            Assert.Empty(rejected.Messages.OfType<JoinAcceptedMessage>());
            Assert.Empty(rejected.Messages.OfType<ServerSnapshotMessage>());
        }
        finally
        {
            await first.DisposeAsync();
            await second.DisposeAsync();
        }
    }

    [Fact]
    public async Task DeferredJoin_FromAConnectionReleasedBeforeTheTick_GetsNoSeat()
    {
        await using ServerUnderTest server = ServerUnderTest.Start();
        // session-a would take the primary seat if its join were applied.
        RawClient leaver = await server.ConnectAsync("session-a");
        RawClient stayer = await server.ConnectAsync("session-b");
        try
        {
            await leaver.SendJoinAndWaitUntilReadAsync();
            await stayer.SendJoinAndWaitUntilReadAsync();
            Assert.Empty(leaver.Messages.OfType<JoinAcceptedMessage>());

            await leaver.DisposeAsync();
            await WaitUntilAsync(() => server.Service.ConnectionCount == 1, "the server never released the closed connection");

            await server.TickAsync();
            await stayer.WaitForAsync(messages => messages.OfType<ServerSnapshotMessage>().Any(), "a snapshot");

            WorldSessionRuntime runtime = server.Host.Runtime!;
            Assert.Equal("session-b", runtime.PrimaryRemoteSessionId?.Value);
            JoinAcceptedMessage accepted = stayer.Messages.OfType<JoinAcceptedMessage>().Single();
            Assert.Equal(["session-b"], accepted.EffectiveRoster.Select(entry => entry.SessionId));
            ServerSnapshotMessage snapshot = stayer.Messages.OfType<ServerSnapshotMessage>().First();
            Assert.DoesNotContain(snapshot.Snapshot.Actors, actor => actor.SessionId == "session-a");
        }
        finally
        {
            await leaver.DisposeAsync();
            await stayer.DisposeAsync();
        }
    }

    // Connects every client, has the server read the three join requests in arrivalOrder before
    // one tick, and describes the result: the primary seat, then each session's JoinAccepted and
    // its first snapshot's player actors.
    private static async Task<string[]> RunSameTickJoinsAsync(string[] arrivalOrder)
    {
        await using ServerUnderTest server = ServerUnderTest.Start();
        Dictionary<string, RawClient> clients = new(StringComparer.Ordinal);
        try
        {
            foreach (string session in Sessions)
            {
                clients[session] = await server.ConnectAsync(session);
            }

            foreach (string session in arrivalOrder)
            {
                await clients[session].SendJoinAndWaitUntilReadAsync();
            }

            await server.TickAsync();
            List<string> description = [$"primary={server.Host.Runtime!.PrimaryRemoteSessionId?.Value}"];
            foreach (string session in Sessions)
            {
                RawClient client = clients[session];
                await client.WaitForAsync(messages => messages.OfType<ServerSnapshotMessage>().Any(), "a snapshot");
                JoinAcceptedMessage accepted = client.Messages.OfType<JoinAcceptedMessage>().Single();
                ServerSnapshotMessage snapshot = client.Messages.OfType<ServerSnapshotMessage>().First();
                description.Add(
                    $"{session}: actor={accepted.ActorId} roster=[{string.Join(",", accepted.EffectiveRoster.OrderBy(entry => entry.SessionId, StringComparer.Ordinal).Select(entry => $"{entry.SessionId}:{entry.ActorId}"))}] " +
                    $"initial=[{DescribePlayers(accepted.EffectiveInitialActors)}] snapshot=[{DescribePlayers(snapshot.Snapshot.Actors)}]");
            }

            return description.ToArray();
        }
        finally
        {
            await DisposeAllAsync(clients.Values);
        }
    }

    private static string DescribePlayers(IEnumerable<ActorSnapshot> actors)
    {
        return string.Join(
            ",",
            actors
                .Where(actor => actor.SessionId != null)
                .OrderBy(actor => actor.SessionId, StringComparer.Ordinal)
                .Select(actor => $"{actor.SessionId}:{actor.ActorId}@{actor.Position.X:0.000}/{actor.Position.Y:0.000}/{actor.Position.Z:0.000}"));
    }

    private static int IndexOf<TMessage>(IReadOnlyList<IServerToClientMessage> messages)
    {
        for (int i = 0; i < messages.Count; i++)
        {
            if (messages[i] is TMessage)
            {
                return i;
            }
        }

        return -1;
    }

    private static async Task DisposeAllAsync(IEnumerable<RawClient> clients)
    {
        foreach (RawClient client in clients)
        {
            await client.DisposeAsync();
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string failure)
    {
        DateTime deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, failure);
            await Task.Delay(5);
        }
    }

    // The live host and service, ticked by the test instead of the service's tick loop.
    private sealed class ServerUnderTest : IAsyncDisposable
    {
        private ServerUnderTest(DedicatedServerHost host, DedicatedServerService service)
        {
            Host = host;
            Service = service;
        }

        public DedicatedServerHost Host { get; }

        public DedicatedServerService Service { get; }

        public static ServerUnderTest Start()
        {
            DedicatedServerHost host = ServerBootstrap.CreateAuthoritativeHost(persistence: null);
            DedicatedServerService service = new(
                host,
                new TcpServerTransport(IPAddress.Loopback, 0),
                new TransportJsonCodec(),
                new LengthPrefixedMessageFramer());
            service.Start();
            return new ServerUnderTest(host, service);
        }

        public async Task<RawClient> ConnectAsync(string sessionId)
        {
            Task<ServerConnection> accept = Service.AcceptNextClientAsync().AsTask();
            RawClient client = await RawClient.ConnectAsync(Service.Port, sessionId);
            await accept.WaitAsync(Timeout);
            return client;
        }

        // One fixed tick, then every queued message written.
        public async Task TickAsync()
        {
            ulong before = Host.Runtime!.CurrentServerTick;
            int executed = await Service.AdvanceOnceAsync(SimulationClock.FixedDeltaSeconds);
            if (executed == 0)
            {
                executed = await Service.AdvanceOnceAsync(SimulationClock.FixedDeltaSeconds * 0.01);
            }

            Assert.Equal(1, executed);
            Assert.Equal(before + 1, Host.Runtime!.CurrentServerTick);
            Assert.True(await Service.FlushOutboundAsync(Timeout), "The server did not flush its outbound queues.");
        }

        public async ValueTask DisposeAsync()
        {
            await Service.DisposeAsync();
            Host.Dispose();
        }
    }

    // A minimal protocol client that records every message the server sends, in order.
    private sealed class RawClient : IAsyncDisposable
    {
        private readonly TcpClient _tcp;
        private readonly NetworkStream _stream;
        private readonly TransportJsonCodec _codec = new();
        private readonly LengthPrefixedMessageFramer _framer = new();
        private readonly List<IServerToClientMessage> _messages = [];
        private readonly CancellationTokenSource _shutdown = new();
        private readonly Task _readLoop;
        private ulong _outboundSequence;
        private bool _disposed;

        private RawClient(TcpClient tcp, string sessionId)
        {
            _tcp = tcp;
            _stream = tcp.GetStream();
            SessionId = sessionId;
            _readLoop = Task.Factory.StartNew(ReadLoop, TaskCreationOptions.LongRunning);
        }

        public string SessionId { get; }

        public Exception? ReadFault { get; private set; }

        public IReadOnlyList<IServerToClientMessage> Messages
        {
            get
            {
                lock (_messages)
                {
                    return _messages.ToArray();
                }
            }
        }

        public static async Task<RawClient> ConnectAsync(int port, string sessionId)
        {
            TcpClient tcp = new() { NoDelay = true };
            await tcp.ConnectAsync(IPAddress.Loopback, port);
            return new RawClient(tcp, sessionId);
        }

        // Sends hello, join, hello and waits for the second ServerHello. The server handles one
        // connection's frames in order, so by then it has read this client's join request.
        public async Task SendJoinAndWaitUntilReadAsync()
        {
            await SendAsync(new ClientHelloMessage($"raw-{SessionId}"));
            await SendAsync(new ClientJoinRequestMessage(SessionId, $"Name {SessionId}"));
            await SendAsync(new ClientHelloMessage($"raw-{SessionId}"));
            await WaitForAsync(messages => messages.OfType<ServerHelloMessage>().Count() >= 2, "two server hellos");
        }

        public async Task WaitForAsync(Func<IReadOnlyList<IServerToClientMessage>, bool> condition, string what)
        {
            DateTime deadline = DateTime.UtcNow + Timeout;
            while (!condition(Messages))
            {
                Assert.True(ReadFault == null, $"{SessionId} read loop faulted: {ReadFault}");
                Assert.True(DateTime.UtcNow < deadline, $"{SessionId} never received {what}.");
                await Task.Delay(5);
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _shutdown.Cancel();
            _tcp.Dispose();
            try
            {
                await _readLoop.WaitAsync(Timeout);
            }
            catch (Exception)
            {
            }

            _shutdown.Dispose();
        }

        private async Task SendAsync<TMessage>(TMessage message)
            where TMessage : IClientToServerMessage
        {
            _outboundSequence++;
            byte[] payload = _codec.Encode(new TransportEnvelope(TransportEnvelope.CurrentProtocolVersion, message.MessageKind, _outboundSequence), message);
            await _stream.WriteAsync(_framer.Frame(payload));
        }

        private void ReadLoop()
        {
            byte[] chunk = new byte[16 * 1024];
            List<byte> pending = [];
            try
            {
                while (!_shutdown.IsCancellationRequested)
                {
                    int read = _stream.Read(chunk, 0, chunk.Length);
                    if (read <= 0)
                    {
                        return;
                    }

                    pending.AddRange(chunk.AsSpan(0, read).ToArray());
                    byte[] buffered = pending.ToArray();
                    int offset = 0;
                    while (_framer.TryDeframe(buffered.AsSpan(offset), out byte[]? payload, out int consumed))
                    {
                        offset += consumed;
                        DecodedProtocolMessage decoded = _codec.Decode(payload);
                        lock (_messages)
                        {
                            _messages.Add((IServerToClientMessage)decoded.Message);
                        }
                    }

                    pending.RemoveRange(0, offset);
                }
            }
            catch (Exception) when (_shutdown.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                ReadFault = ex;
            }
        }
    }
}
