using System.Net;
using System.Net.Sockets;
using Dream.Client.Networking;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Transport;
using Dream.Rendering.Ui.Panels;
using Dream.Server;
using Dream.Simulation.Runtime;
using Dream.Tests.Server.Persistence;
using Dream.Tests.Support;
using Dream.Tests.Utilities;
using Xunit;

namespace Dream.Tests.Integration;

// M1 fix loop: chat lines and character lists over real loopback TCP through ServerConnection and
// DedicatedServerService. A server message with no encode case used to fault the connection's writer and
// close the connection; these tests fail if either message kind cannot be written, or a connection drops.
public sealed class ChatAndCharacterListLoopbackTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task ACharacterListRequest_IsAnsweredOverTcp_WithTheAccountsCharacters()
    {
        using PersistenceTestDatabase database = new();
        using DedicatedServerHost host = CreateHost(database);
        await using Server server = Server.Start(host);
        string token = TestAccountTokens.For("lister");

        await using RemoteClientSession alice = await server.ConnectClientAsync("s-alice", "Alice", token);
        await using RemoteClientSession alicia = await server.ConnectClientAsync("s-alicia", "Alicia", token);
        await server.TickUntilAsync(() => alice.IsJoined && alicia.IsJoined);

        await using RawClient picker = await server.ConnectRawAsync();
        await picker.SendAsync(new ClientHelloMessage("picker"));
        await picker.SendAsync(new ClientCharacterListRequest(token));
        await server.TickUntilAsync(() => picker.Messages.OfType<ServerCharacterList>().Any());

        ServerCharacterList list = picker.Messages.OfType<ServerCharacterList>().First();
        Assert.Contains(list.Characters, c => c.DisplayName == "Alice");
        Assert.Contains(list.Characters, c => c.DisplayName == "Alicia");
        Assert.True(alice.IsConnected);
        Assert.True(alicia.IsConnected);
    }

    [Fact]
    public async Task AWorldLine_ReachesTheOtherClientAndItsChatHud_AndNobodyIsDropped()
    {
        using PersistenceTestDatabase database = new();
        using DedicatedServerHost host = CreateHost(database);
        await using Server server = Server.Start(host);
        await using RemoteClientSession alice = await server.ConnectClientAsync("s-a", "Alice", TestAccountTokens.For("a"));
        await using RemoteClientSession bob = await server.ConnectClientAsync("s-b", "Bob", TestAccountTokens.For("b"));
        await server.TickUntilAsync(() => alice.IsJoined && bob.IsJoined);
        ChatHudState bobHud = new();
        List<ServerChatMessage> bobChat = [];

        alice.QueueChat(new ClientChatMessage(ChatChannel.World, "hello world"));
        await server.TickUntilAsync(() =>
        {
            Drain(bob, bobChat, bobHud);
            return bobChat.Count > 0;
        });

        ServerChatMessage received = Assert.Single(bobChat);
        Assert.Equal(ChatChannel.World, received.Channel);
        Assert.Equal("Alice", received.From);
        Assert.Equal("hello world", received.Text);
        Assert.Equal(1, bobHud.LineCount);
        Assert.Contains("hello world", bobHud.GetLine(0).Text);

        // Several more ticks: the connections stay up.
        await server.TickAsync(20);
        Assert.True(alice.IsConnected);
        Assert.True(bob.IsConnected);
        Assert.Null(alice.DisconnectReason);
        Assert.Null(bob.DisconnectReason);
    }

    [Fact]
    public async Task AWhisperToAnOfflineName_GetsASystemReply_AndTheSenderStaysConnected()
    {
        using PersistenceTestDatabase database = new();
        using DedicatedServerHost host = CreateHost(database);
        await using Server server = Server.Start(host);
        await using RemoteClientSession alice = await server.ConnectClientAsync("s-a", "Alice", TestAccountTokens.For("a"));
        await server.TickUntilAsync(() => alice.IsJoined);
        ChatHudState hud = new();
        List<ServerChatMessage> chat = [];

        alice.QueueChat(new ClientChatMessage(ChatChannel.Whisper, "psst", "Nobody"));
        await server.TickUntilAsync(() =>
        {
            Drain(alice, chat, hud);
            return chat.Count > 0;
        });

        ServerChatMessage reply = Assert.Single(chat);
        Assert.Equal(ChatChannel.System, reply.Channel);
        await server.TickAsync(20);
        Assert.True(alice.IsConnected);
        Assert.Null(alice.DisconnectReason);
    }

    private static void Drain(RemoteClientSession session, List<ServerChatMessage> chat, ChatHudState hud)
    {
        while (session.TryDequeueInbound(out IServerToClientMessage? message))
        {
            if (message is ServerChatMessage line)
            {
                chat.Add(line);
                hud.AddServerMessage(line.Channel, line.From, line.Text);
            }
        }
    }

    private static DedicatedServerHost CreateHost(PersistenceTestDatabase database) =>
        ServerBootstrap.CreateAuthoritativeHost(ServerBootstrap.CreatePersistenceBundle(database.DatabasePath, database.SchemaPath));

    private sealed class Server : IAsyncDisposable
    {
        private readonly DedicatedServerService _service;
        private readonly DedicatedServerHost _host;

        private Server(DedicatedServerHost host, DedicatedServerService service)
        {
            _host = host;
            _service = service;
        }

        public static Server Start(DedicatedServerHost host)
        {
            DedicatedServerService service = new(
                host,
                new TcpServerTransport(IPAddress.Loopback, 0),
                new TransportJsonCodec(),
                new LengthPrefixedMessageFramer(),
                PerTickSnapshots.Options);
            service.Start();
            return new Server(host, service);
        }

        public async Task<RemoteClientSession> ConnectClientAsync(string sessionId, string name, string token)
        {
            Task<ServerConnection> accept = _service.AcceptOnThreadPoolAsync();
            RemoteClientOptions options = new("127.0.0.1", _service.Port, sessionId, name, $"loopback-{sessionId}", token);
            RemoteClientSession session = await RemoteClientSession.ConnectAsync(options);
            await accept.WaitAsync(Timeout);
            return session;
        }

        public async Task<RawClient> ConnectRawAsync()
        {
            Task<ServerConnection> accept = _service.AcceptOnThreadPoolAsync();
            RawClient client = await RawClient.ConnectAsync(_service.Port);
            await accept.WaitAsync(Timeout);
            return client;
        }

        public async Task TickAsync(int ticks)
        {
            for (int i = 0; i < ticks; i++)
            {
                await Task.Delay(10);
                await _service.AdvanceOnceAsync(SimulationClock.FixedDeltaSeconds);
                Assert.True(await _service.FlushOutboundAsync(Timeout));
            }
        }

        public async Task TickUntilAsync(Func<bool> condition)
        {
            for (int i = 0; i < 600 && !condition(); i++)
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

            Assert.True(condition(), "the expected state never arrived");
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
        private readonly Task _readLoop;
        private ulong _outboundSequence;

        private RawClient(TcpClient tcp)
        {
            _tcp = tcp;
            _stream = tcp.GetStream();
            _readLoop = Task.Factory.StartNew(ReadLoop, TaskCreationOptions.LongRunning);
        }

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

        public static async Task<RawClient> ConnectAsync(int port)
        {
            TcpClient tcp = new() { NoDelay = true };
            await tcp.ConnectAsync(IPAddress.Loopback, port);
            return new RawClient(tcp);
        }

        public async Task SendAsync<TMessage>(TMessage message)
            where TMessage : IClientToServerMessage
        {
            _outboundSequence++;
            byte[] payload = _codec.Encode(new TransportEnvelope(TransportEnvelope.CurrentProtocolVersion, message.MessageKind, _outboundSequence), message);
            await _stream.WriteAsync(_framer.Frame(payload));
        }

        public async ValueTask DisposeAsync()
        {
            _tcp.Dispose();
            try
            {
                await _readLoop.WaitAsync(Timeout);
            }
            catch (Exception)
            {
            }
        }

        private void ReadLoop()
        {
            byte[] chunk = new byte[16 * 1024];
            List<byte> pending = [];
            try
            {
                while (true)
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
