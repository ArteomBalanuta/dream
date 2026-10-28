using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using OpenTK.Mathematics;
using Xunit;
using Dream.Client.Networking;
using Dream.Client.Startup;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Server;
using Dream.Simulation.Combat;
using Dream.Simulation.Players;

namespace Dream.Tests.Integration;

// WIRE-4 moved the client's outbound encode into one reusable PooledFrameWriter while the server
// keeps the byte[] Encode and Frame path. Over real loopback TCP, a client sends hundreds of
// frames from two concurrent callers of the public Send*Async methods, with escaped and very
// large strings, including frames over PooledFrameWriter.RetainedCapacityLimit so the writer's
// shrink path runs between ordinary frames. The receiving side decodes with the server's codec and
// framer and checks every frame: order, gap-free sequences, and bytes identical to the byte[] path.
public class PooledClientSendLoopbackIntegrationTests
{
    private const int CommandCount = 500;
    private const int ActionCount = 120;
    private const int ExtraJoinRequests = 12;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    // Quotes, backslashes, JSON-escaped HTML characters, control characters, a lone-free
    // surrogate pair, non-ASCII letters, NUL and a line separator.
    private const string EscapeUnit = "\"\\<>&'\u00e9\u4e2d\U0001F600\u0001\u001f\t\n\r\0\u2028/";

    private static readonly string[] SessionIds =
    [
        "plain-session",
        "quote\"and\\backslash",
        "<script>&amp;</script>",
        "emoji-\U0001F43A-wolf",
        "ctrl-\u0001-\u001f-\t-\n",
        "\u00e9\u00e8\u00ea-\u4e2d\u6587-\u0627\u0644\u0639\u0631\u0628\u064a\u0629"
    ];

    [Fact]
    public async Task Concurrent_Public_Sends_Through_The_Pooled_Writer_Arrive_In_Order_Gap_Free_And_Byte_Identical()
    {
        string hugeDisplayName = "huge-" + string.Concat(Enumerable.Repeat(EscapeUnit, 2400)) + "-end";
        string clientName = "client " + EscapeUnit + " name";
        TransportJsonCodec serverCodec = new();
        LengthPrefixedMessageFramer serverFramer = new();
        // The huge name is far past the pooled writer's retained capacity once escaped.
        int hugeJoinBytes = serverCodec.Encode(
            new TransportEnvelope(TransportEnvelope.CurrentProtocolVersion, "client.join-request", 1),
            new ClientJoinRequestMessage("s", hugeDisplayName)).Length;
        Assert.True(hugeJoinBytes > PooledFrameWriter.RetainedCapacityLimit, $"join request is only {hugeJoinBytes} bytes");

        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        int expectedFrames = 2 + CommandCount + ActionCount + ExtraJoinRequests;
        Task<List<ReceivedFrame>> serverTask = ReceiveFramesAsync(listener, serverFramer, serverCodec, expectedFrames);

        RemoteClientOptions options = new("127.0.0.1", port, SessionIds[1], hugeDisplayName, clientName);
        await using RemoteClientSession client = await RemoteClientSession.ConnectAsync(options);

        List<PlayerCommandFrame> commands = Enumerable.Range(1, CommandCount).Select(CreateCommand).ToList();
        List<PlayerActionMessage> actions = Enumerable.Range(1, ActionCount).Select(CreateAction).ToList();
        using Barrier start = new(2);
        // Both callers start together, but on a busy machine one can still finish its whole stream
        // before the other is scheduled. The command caller therefore waits, halfway through, for
        // the action caller's first send to complete; sends take the client's send lock in call
        // order, so the rest of the command stream is on the wire after that action and the two
        // streams overlap however the threads are scheduled.
        TaskCompletionSource firstActionSent = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task commandTask = Task.Run(async () =>
        {
            start.SignalAndWait();
            for (int i = 0; i < commands.Count; i++)
            {
                if (i == commands.Count / 2)
                {
                    await firstActionSent.Task.WaitAsync(Timeout);
                }

                await client.SendCommandFrameAsync(commands[i]);
            }
        });
        Task actionTask = Task.Run(async () =>
        {
            start.SignalAndWait();
            int joinsSent = 0;
            for (int i = 0; i < actions.Count; i++)
            {
                await client.SendPlayerActionAsync(actions[i]);
                firstActionSent.TrySetResult();
                if (i % 10 == 5 && joinsSent < ExtraJoinRequests)
                {
                    // A frame over the retained capacity, interleaved with the command stream.
                    await client.SendJoinRequestAsync();
                    joinsSent++;
                }
            }

            while (joinsSent < ExtraJoinRequests)
            {
                await client.SendJoinRequestAsync();
                joinsSent++;
            }
        });

        await Task.WhenAll(commandTask, actionTask).WaitAsync(Timeout);
        List<ReceivedFrame> frames = await serverTask.WaitAsync(Timeout);

        Assert.Equal(expectedFrames, frames.Count);

        // Envelope sequences are 1..N in wire order with no gap and no repeat.
        for (int i = 0; i < frames.Count; i++)
        {
            Assert.Equal((ulong)(i + 1), frames[i].Decoded.Envelope.Sequence);
            Assert.Equal(TransportEnvelope.CurrentProtocolVersion, frames[i].Decoded.Envelope.ProtocolVersion);
            Assert.Equal(frames[i].Decoded.Message.MessageKind, frames[i].Decoded.Envelope.MessageKind);
        }

        // The handshake goes first, then the two callers' streams each keep their own order.
        Assert.Equal(new ClientHelloMessage(clientName), frames[0].Decoded.Message);
        Assert.Equal(new ClientJoinRequestMessage(SessionIds[1], hugeDisplayName), frames[1].Decoded.Message);
        Assert.Equal(
            commands,
            frames.Select(frame => frame.Decoded.Message).OfType<PlayerCommandMessage>().Select(message => message.CommandFrame).ToList());
        Assert.Equal(actions, frames.Select(frame => frame.Decoded.Message).OfType<PlayerActionMessage>().ToList());
        List<ClientJoinRequestMessage> joins = frames.Select(frame => frame.Decoded.Message).OfType<ClientJoinRequestMessage>().ToList();
        Assert.Equal(1 + ExtraJoinRequests, joins.Count);
        Assert.All(joins, join => Assert.Equal(hugeDisplayName, join.DisplayName));

        // The streams really interleaved, so both callers contended for the one pooled writer.
        int firstAction = frames.FindIndex(frame => frame.Decoded.Message is PlayerActionMessage);
        int lastCommand = frames.FindLastIndex(frame => frame.Decoded.Message is PlayerCommandMessage);
        Assert.True(firstAction < lastCommand, "The action stream never overlapped the command stream.");

        // Every payload is byte-identical to what the server-side byte[] Encode produces.
        foreach (ReceivedFrame frame in frames)
        {
            byte[] expected = EncodeWithByteArrayPath(serverCodec, frame.Decoded);
            Assert.True(
                expected.AsSpan().SequenceEqual(frame.Payload),
                $"Frame {frame.Decoded.Envelope.Sequence} ({frame.Decoded.Message.MessageKind}) differs from the byte[] encode.");
        }

        // Every join crossed the retained capacity, so the writer shrank after each of them, and
        // the small frames right behind them still came out intact (checked above).
        Assert.All(
            frames.Where(frame => frame.Decoded.Message is ClientJoinRequestMessage),
            frame => Assert.True(frame.Payload.Length > PooledFrameWriter.RetainedCapacityLimit));
        Assert.All(
            frames.Where(frame => frame.Decoded.Message is not ClientJoinRequestMessage),
            frame => Assert.True(frame.Payload.Length < 4096));
    }

    [Fact]
    public async Task Real_Server_Round_Trips_An_Escaped_Display_Name_From_The_Pooled_Client_Encode()
    {
        string displayName = "Wolf\"Tamer\\ <b>&</b> \u00e9\u4e2d\U0001F43A \u0001\t" + new string('x', 3000) + "|end";
        DedicatedServerHost? host = null;
        await using IntegratedLocalServerHandle server = await IntegratedLocalServerHandle.StartAsync(
            0,
            () => host = DedicatedServerHost.CreateRuntimeAttached(persistence: null));
        Assert.True(server.IsReady, server.ErrorMessage);

        await using RemoteClientSession client = await RemoteClientSession.ConnectAsync(
            new RemoteClientOptions("127.0.0.1", server.BoundPort, "escaped-session", displayName, "pooled-test"));

        Stopwatch clock = Stopwatch.StartNew();
        bool joined = false;
        string? echoedName = null;
        ulong sequence = 0;
        while (clock.Elapsed < Timeout && (!joined || echoedName == null))
        {
            sequence++;
            await client.SendCommandFrameAsync(PlayerCommandFrame.CreateIdle(new PlayerSessionId("escaped-session"), sequence));
            while (client.TryDequeueInbound(out IServerToClientMessage? message) && message != null)
            {
                joined |= message is JoinAcceptedMessage;
                if (message is ServerSnapshotMessage snapshot)
                {
                    echoedName ??= snapshot.Snapshot.Actors
                        .FirstOrDefault(actor => actor.Kind == CreatureKind.Player && actor.SessionId == "escaped-session")
                        .DisplayName;
                }
            }

            await Task.Delay(15);
        }

        Assert.True(joined, "The server never accepted the join.");
        Assert.True(client.IsConnected);
        Assert.Equal(displayName, echoedName);
    }

    private static PlayerCommandFrame CreateCommand(int index)
    {
        // Values that are exact in binary, so the JSON round trip cannot blur them.
        return new PlayerCommandFrame(
            new PlayerSessionId(SessionIds[index % SessionIds.Length]),
            (ulong)index,
            index % 7 == 0 ? new PlayerModeSwitchIntent(PlayerInteractionMode.Combat) : PlayerModeSwitchIntent.None,
            new PlayerMoveIntent(
                ForwardAxis: (index % 5 - 2) * 0.5f,
                StrafeAxis: (index % 3 - 1) * 0.25f,
                VerticalAxis: index % 11 == 0 ? 1f : 0f,
                JumpPressed: index % 13 == 0),
            new PlayerLookIntent(index * 0.125f, -index * 0.0625f, index % 2 == 0 ? new Vector3(0f, 0f, -1f) : null),
            PrimaryUsePressed: index % 4 == 0);
    }

    private static PlayerActionMessage CreateAction(int index)
    {
        IPlayerActionPayload payload = index % 2 == 0
            ? new BuildUseIntent(new Vector3(index * 0.5f, 2f, -index * 0.25f))
            : new InventoryEquipIntent(index % 9);
        return new PlayerActionMessage(new PlayerSessionId(SessionIds[index % SessionIds.Length]), (ulong)index, payload);
    }

    private static byte[] EncodeWithByteArrayPath(TransportJsonCodec codec, DecodedProtocolMessage decoded)
    {
        return decoded.Message switch
        {
            ClientHelloMessage hello => codec.Encode(decoded.Envelope, hello),
            ClientJoinRequestMessage join => codec.Encode(decoded.Envelope, join),
            PlayerCommandMessage command => codec.Encode(decoded.Envelope, command),
            PlayerActionMessage action => codec.Encode(decoded.Envelope, action),
            _ => throw new InvalidOperationException($"Unexpected message {decoded.Message.MessageKind}.")
        };
    }

    private static async Task<List<ReceivedFrame>> ReceiveFramesAsync(
        TcpListener listener,
        LengthPrefixedMessageFramer framer,
        TransportJsonCodec codec,
        int expectedFrames)
    {
        using CancellationTokenSource timeout = new(Timeout);
        using TcpClient accepted = await listener.AcceptTcpClientAsync(timeout.Token);
        NetworkStream stream = accepted.GetStream();
        List<ReceivedFrame> frames = [];
        List<byte> pending = [];
        byte[] chunk = new byte[8 * 1024];
        while (frames.Count < expectedFrames)
        {
            int read = await stream.ReadAsync(chunk, timeout.Token);
            if (read <= 0)
            {
                break;
            }

            pending.AddRange(chunk.AsSpan(0, read).ToArray());
            byte[] buffered = pending.ToArray();
            int offset = 0;
            while (framer.TryDeframe(buffered.AsSpan(offset), out byte[]? payload, out int consumed))
            {
                offset += consumed;
                frames.Add(new ReceivedFrame(payload!, codec.Decode(payload)));
            }

            pending.RemoveRange(0, offset);
        }

        return frames;
    }

    private sealed record ReceivedFrame(byte[] Payload, DecodedProtocolMessage Decoded);
}
