using System.Net;
using System.Net.Sockets;
using Xunit;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Transport;
using Dream.Server;
using Dream.Simulation.Characters;
using Dream.Simulation.Runtime;
using Dream.Tests.Server.Persistence;
using Dream.Tests.Support;
using Dream.Tests.Utilities;

namespace Dream.Tests.Integration;

// Wave G (G2): the account checks over real loopback TCP against the host the dedicated and integrated
// servers run (ServerBootstrap.CreateAuthoritativeHost over a bundle with accounts), ticked by the test.
public sealed class CharacterLoopbackTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task NullAndMalformedTokens_AreRejectedOverTcp_WithNoPrefetchAndNoSeat()
    {
        using PersistenceTestDatabase database = new();
        CountingAccountRepository accounts = new();
        using DedicatedServerHost host = AccountHostHarness.CreateProductionHost(database, accounts);
        await using ServerUnderTest server = ServerUnderTest.Start(host);
        await using RawClient missing = await server.ConnectAsync("session-null");
        await using RawClient malformed = await server.ConnectAsync("session-bad");

        await missing.SendJoinAsync("Nobody", token: null);
        await malformed.SendJoinAsync("Nobody", token: "not-a-token");

        // A refusal before the queue answers on the receive path; no tick is needed.
        JoinRejectedMessage first = await missing.WaitForRejectionAsync();
        JoinRejectedMessage second = await malformed.WaitForRejectionAsync();
        Assert.Equal(JoinRejectionReasons.TokenRequired, first.Reason);
        Assert.Equal(JoinRejectionReasons.TokenInvalid, second.Reason);
        Assert.Equal(0, accounts.LookupCalls);
        Assert.Equal(0, accounts.AccountCount);
        Assert.DoesNotContain(host.Runtime!.Sessions.Players, player => !player.IsLocal);
        Assert.Equal(0, database.Count("SELECT COUNT(*) FROM player_profiles;"));
    }

    [Fact]
    public async Task AValidToken_JoinsOverTcp_AndASecondTokenOnTheSameNameIsRejected()
    {
        using PersistenceTestDatabase database = new();
        CountingAccountRepository accounts = new();
        using DedicatedServerHost host = AccountHostHarness.CreateProductionHost(database, accounts);
        await using ServerUnderTest server = ServerUnderTest.Start(host);
        await using RawClient owner = await server.ConnectAsync("session-owner");
        await using RawClient stranger = await server.ConnectAsync("session-stranger");

        await owner.SendJoinAsync("Shared", TestAccountTokens.For("owner"));
        await server.TickUntilAsync(() => owner.Messages.OfType<JoinAcceptedMessage>().Any());
        await stranger.SendJoinAsync("Shared", TestAccountTokens.For("stranger"));
        await server.TickUntilAsync(() => stranger.Messages.OfType<JoinRejectedMessage>().Any());

        Assert.Equal(JoinRejectionReasons.CharacterOwned, (await stranger.WaitForRejectionAsync()).Reason);
        Assert.Empty(stranger.Messages.OfType<JoinAcceptedMessage>());
        Assert.Equal(TestAccountTokens.KeyFor("owner"), accounts.OwnerOf("Shared"));
        Assert.Single(host.Runtime!.Sessions.Players, player => !player.IsLocal && player.IsSpawned);
    }

    private sealed class ServerUnderTest : IAsyncDisposable
    {
        private readonly DedicatedServerService _service;
        private readonly DedicatedServerHost _host;

        private ServerUnderTest(DedicatedServerHost host, DedicatedServerService service)
        {
            _host = host;
            _service = service;
        }

        public static ServerUnderTest Start(DedicatedServerHost host)
        {
            DedicatedServerService service = new(
                host,
                new TcpServerTransport(IPAddress.Loopback, 0),
                new TransportJsonCodec(),
                new LengthPrefixedMessageFramer(),
                PerTickSnapshots.Options);
            service.Start();
            return new ServerUnderTest(host, service);
        }

        public async Task<RawClient> ConnectAsync(string sessionId)
        {
            Task<ServerConnection> accept = _service.AcceptOnThreadPoolAsync();
            RawClient client = await RawClient.ConnectAsync(_service.Port, sessionId);
            await accept.WaitAsync(Timeout);
            return client;
        }

        public async Task TickUntilAsync(Func<bool> condition)
        {
            for (int i = 0; i < 400 && !condition(); i++)
            {
                await Task.Delay(10);
                Assert.True(_host.Runtime!.PersistenceWrites?.Flush(Timeout) ?? true);
                int executed = await _service.AdvanceOnceAsync(SimulationClock.FixedDeltaSeconds);
                if (executed == 0)
                {
                    await _service.AdvanceOnceAsync(SimulationClock.FixedDeltaSeconds * 0.01);
                }

                Assert.True(await _service.FlushOutboundAsync(Timeout));
            }

            Assert.True(condition(), "the expected reply never arrived");
        }

        public async ValueTask DisposeAsync()
        {
            await _service.DisposeAsync();
        }
    }

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

        private RawClient(TcpClient tcp, string sessionId)
        {
            _tcp = tcp;
            _stream = tcp.GetStream();
            SessionId = sessionId;
            _readLoop = Task.Factory.StartNew(ReadLoop, TaskCreationOptions.LongRunning);
        }

        public string SessionId { get; }

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

        public async Task SendJoinAsync(string name, string? token)
        {
            await SendAsync(new ClientHelloMessage($"loopback-{SessionId}"));
            await SendAsync(new ClientJoinRequestMessage(SessionId, name, token));
        }

        public async Task<JoinRejectedMessage> WaitForRejectionAsync()
        {
            DateTime deadline = DateTime.UtcNow + Timeout;
            while (DateTime.UtcNow < deadline)
            {
                if (Messages.OfType<JoinRejectedMessage>().FirstOrDefault() is { } rejected)
                {
                    return rejected;
                }

                await Task.Delay(5);
            }

            throw new TimeoutException($"{SessionId} never received a JoinRejected.");
        }

        public async ValueTask DisposeAsync()
        {
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
            catch (Exception)
            {
            }
        }
    }
}
