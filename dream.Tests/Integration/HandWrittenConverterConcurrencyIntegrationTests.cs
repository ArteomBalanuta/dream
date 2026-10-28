using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using OpenTK.Mathematics;
using Xunit;
using Xunit.Abstractions;
using Dream.Client.Replication;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Multiplayer.Transport;
using Dream.Server;
using Dream.Simulation.Combat;
using Dream.Simulation.Enemies;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Tests.Multiplayer.Protocol;
using RawClient = Dream.Tests.Integration.ProtocolV2SharedSnapshotLoopbackIntegrationTests.RawClient;
using Frame = Dream.Tests.Integration.ProtocolV2SharedSnapshotLoopbackIntegrationTests.Frame;
using ClientWorldReplay = Dream.Tests.Integration.ProtocolV2SharedSnapshotLoopbackIntegrationTests.ClientWorldReplay;
using Dream.Tests.Utilities;

namespace Dream.Tests.Integration;

// WIRE-10's hand-written converters (ActorSnapshot, the actor list, the command frame) are
// registered once on the codec's static options and are shared by every connection writer, the
// WIRE-5 shared snapshot encode (SharedEncodedPayload) and every reader thread. Any shared
// scratch state in them (a static buffer, the pooled actor array of the list reader) would
// corrupt output only under concurrency. Here 16 threads encode and decode the same large
// snapshots and command frames at once, through one shared codec, private codecs and shared
// payloads, and every result is compared with the reflection reference (ProtocolWireOptions.V2).
// A real loopback run with three TCP clients then checks the wire end to end; it lives in
// HandWrittenConverterLoopbackIntegrationTests so the two run in parallel, using the helpers here.
public class HandWrittenConverterConcurrencyIntegrationTests(ITestOutputHelper output)
{
    internal const int Threads = 16;
    internal const int Snapshots = 48;
    internal const int Commands = 256;
    internal static readonly TimeSpan Bound = TimeSpan.FromSeconds(60);
    internal static readonly TimeSpan NetTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public void SixteenThreads_EncodeAndDecodeLargeSnapshotsAndCommands_ThroughCodecAndSharedPayload_MatchTheReference()
    {
        Random random = new(10_2026);
        TransportJsonCodec sharedCodec = new();
        ServerSnapshotMessage[] snapshots = new ServerSnapshotMessage[Snapshots];
        byte[][] expectedSnapshotPayloads = new byte[Snapshots][];
        byte[][] expectedSnapshotFrames = new byte[Snapshots][];
        TransportEnvelope[] snapshotEnvelopes = new TransportEnvelope[Snapshots];
        SharedEncodedPayload[] sharedPayloads = new SharedEncodedPayload[Snapshots];
        for (int i = 0; i < Snapshots; i++)
        {
            // From empty to far past the list reader's 32-actor pooled scratch (it grows twice or more).
            int actorCount = i switch { 0 => 0, 1 => 1, 2 => 32, 3 => 33, _ => random.Next(40, 600) };
            snapshots[i] = new ServerSnapshotMessage(SnapshotWith((ulong)(1000 + i), RandomActors(random, actorCount)));
            snapshotEnvelopes[i] = new TransportEnvelope(TransportEnvelope.CurrentProtocolVersion, snapshots[i].MessageKind, (ulong)(i + 1));
            expectedSnapshotPayloads[i] = JsonSerializer.SerializeToUtf8Bytes(snapshots[i], ProtocolWireOptions.V2);
            ArrayBufferWriter<byte> frame = new();
            sharedCodec.WriteEnvelope(snapshotEnvelopes[i], expectedSnapshotPayloads[i], frame);
            expectedSnapshotFrames[i] = frame.WrittenSpan.ToArray();
            sharedPayloads[i] = new SharedEncodedPayload(sharedCodec, snapshots[i]);
        }

        PlayerCommandMessage[] commands = new PlayerCommandMessage[Commands];
        byte[][] expectedCommandFrames = new byte[Commands][];
        for (int i = 0; i < Commands; i++)
        {
            commands[i] = new PlayerCommandMessage(RandomFrame(random));
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(commands[i], ProtocolWireOptions.V2);
            ArrayBufferWriter<byte> frame = new();
            sharedCodec.WriteEnvelope(new TransportEnvelope(TransportEnvelope.CurrentProtocolVersion, commands[i].MessageKind, (ulong)i), payload, frame);
            expectedCommandFrames[i] = frame.WrittenSpan.ToArray();
        }

        string?[] failures = new string?[Threads];
        int encodes = 0;
        int decodes = 0;
        using Barrier start = new(Threads);
        Stopwatch elapsed = Stopwatch.StartNew();
        Thread[] threads = new Thread[Threads];
        for (int t = 0; t < Threads; t++)
        {
            int index = t;
            threads[t] = new Thread(() =>
            {
                try
                {
                    // Half the threads share the server's codec instance, half have their own;
                    // the converters are static on the options either way.
                    TransportJsonCodec codec = index % 2 == 0 ? sharedCodec : new TransportJsonCodec();
                    Random order = new(index);
                    ArrayBufferWriter<byte> scratch = new();
                    start.SignalAndWait();
                    for (int pass = 0; pass < 3; pass++)
                    {
                        foreach (int i in Enumerable.Range(0, Snapshots).OrderBy(_ => order.Next()))
                        {
                            byte[] frame = codec.Encode(snapshotEnvelopes[i], snapshots[i]);
                            Expect(frame.AsSpan().SequenceEqual(expectedSnapshotFrames[i]), $"snapshot {i}: framed encode differs from the reference");

                            scratch.Clear();
                            codec.EncodePayload(snapshots[i], scratch);
                            Expect(scratch.WrittenSpan.SequenceEqual(expectedSnapshotPayloads[i]), $"snapshot {i}: payload encode differs from the reference");

                            ReadOnlySpan<byte> shared = sharedPayloads[i].Bytes;
                            Expect(shared.SequenceEqual(expectedSnapshotPayloads[i]), $"snapshot {i}: shared payload differs from the reference");
                            Interlocked.Add(ref encodes, 3);

                            DecodedProtocolMessage decoded = codec.Decode(expectedSnapshotFrames[i]);
                            ServerSnapshotMessage message = Assert.IsType<ServerSnapshotMessage>(decoded.Message);
                            Expect(message.Snapshot.Actors.SequenceEqual(snapshots[i].Snapshot.Actors), $"snapshot {i}: decoded actors differ");
                            Expect(
                                JsonSerializer.SerializeToUtf8Bytes(message, ProtocolWireOptions.V2).AsSpan().SequenceEqual(expectedSnapshotPayloads[i]),
                                $"snapshot {i}: decoded snapshot re-encodes differently through the reference");
                            Interlocked.Increment(ref decodes);
                        }

                        foreach (int i in Enumerable.Range(0, Commands).OrderBy(_ => order.Next()))
                        {
                            TransportEnvelope envelope = new(TransportEnvelope.CurrentProtocolVersion, commands[i].MessageKind, (ulong)i);
                            Expect(codec.Encode(envelope, commands[i]).AsSpan().SequenceEqual(expectedCommandFrames[i]), $"command {i}: encode differs from the reference");
                            PlayerCommandMessage decoded = Assert.IsType<PlayerCommandMessage>(codec.Decode(expectedCommandFrames[i]).Message);
                            Expect(decoded.CommandFrame == commands[i].CommandFrame, $"command {i}: decoded frame differs");
                            Interlocked.Increment(ref encodes);
                            Interlocked.Increment(ref decodes);
                        }
                    }
                }
                catch (Exception ex)
                {
                    failures[index] = ex.ToString();
                }
            })
            {
                IsBackground = true,
                Name = $"wire10-worker-{t}"
            };
            threads[t].Start();
        }

        foreach (Thread thread in threads)
        {
            Assert.True(thread.Join(Bound), $"A worker did not finish within {Bound.TotalSeconds} s.");
        }

        elapsed.Stop();
        Assert.All(failures, failure => Assert.Null(failure));
        for (int i = 0; i < Snapshots; i++)
        {
            Assert.True(sharedPayloads[i].IsEncoded);
        }

        output.WriteLine(
            $"{Threads} threads: {encodes} encodes, {decodes} decodes in {elapsed.ElapsedMilliseconds} ms; " +
            $"largest snapshot {snapshots.Max(s => s.Snapshot.Actors.Count)} actors, {expectedSnapshotPayloads.Max(p => p.Length)} B");
    }

    // The frame's payload bytes are what the server's hand-written converters wrote; the
    // reflection reference must write exactly the same bytes for the decoded message.
    internal static void AssertMatchesReference(Frame frame)
    {
        IProtocolMessage message = frame.Decoded.Message;
        byte[] reference = JsonSerializer.SerializeToUtf8Bytes(message, message.GetType(), ProtocolWireOptions.V2);
        Assert.True(
            reference.AsSpan().SequenceEqual(frame.PayloadJson),
            $"{frame.Decoded.Envelope.MessageKind}#{frame.Decoded.Envelope.Sequence}: wire payload differs from the reference.\n" +
            $"wire: {Encoding.UTF8.GetString(frame.PayloadJson)}\nref:  {Encoding.UTF8.GetString(reference)}");
    }

    internal static void Expect(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    internal static ServerSnapshotEnvelope SnapshotWith(ulong tick, IReadOnlyList<ActorSnapshot> actors)
    {
        Dictionary<string, ulong> sequences = new(StringComparer.Ordinal);
        foreach (ActorSnapshot actor in actors)
        {
            if (actor.SessionId is string sessionId)
            {
                sequences[sessionId] = (ulong)actor.ActorId;
            }
        }

        return new ServerSnapshotEnvelope(
            tick,
            sequences,
            actors,
            PlayerInteractionMode.Combat,
            null,
            [new WorldMutationDelta(WorldMutationKind.CubePlaced, new Vector3(1f, 2f, 3f), Sequence: tick, ActorId: 101)],
            [new DamageEventDelta(2000, CreatureKind.Enemy, 5, false, 35, 40)],
            WorldChecksum: tick * 31,
            WorldChecksumSequence: tick);
    }

    internal static ActorSnapshot[] RandomActors(Random random, int count)
    {
        ActorSnapshot[] actors = new ActorSnapshot[count];
        for (int i = 0; i < count; i++)
        {
            int mask = random.Next(64);
            actors[i] = new ActorSnapshot(
                i + 1,
                (CreatureKind)random.Next(0, 4),
                (mask & 1) != 0 ? RandomString(random) : null,
                RandomVector(random),
                RandomVector(random),
                random.Next(-1000, 1000),
                random.Next(0, 1000),
                random.Next(2) == 0,
                (mask & 2) != 0 ? (PlayerInteractionMode)random.Next(0, 3) : null,
                (mask & 4) != 0 ? new PlayerCombatStatsSnapshot(random.Next(100), RandomFloat(random), random.Next(100)) : null,
                (mask & 8) != 0 ? RandomString(random) : null,
                (mask & 16) != 0 ? new PlayerProgressionSnapshot(random.Next(50), random.Next(), random.Next()) : null,
                (mask & 32) != 0 ? (EnemyBehaviorState)random.Next(0, 5) : null);
        }

        return actors;
    }

    internal static PlayerCommandFrame RandomFrame(Random random)
    {
        PlayerInteractionMode? mode = random.Next(3) switch
        {
            0 => null,
            1 => PlayerInteractionMode.Building,
            _ => PlayerInteractionMode.Combat
        };

        return new PlayerCommandFrame(
            new PlayerSessionId(RandomString(random)),
            (ulong)random.NextInt64(),
            new PlayerModeSwitchIntent(mode),
            new PlayerMoveIntent(RandomFloat(random), RandomFloat(random), RandomFloat(random), random.Next(2) == 0, random.Next(2) == 0, random.Next(2) == 0),
            new PlayerLookIntent(RandomFloat(random), RandomFloat(random), random.Next(2) == 0 ? null : RandomVector(random)),
            random.Next(2) == 0);
    }

    // Round-trippable strings only (a lone surrogate is written as U+FFFD), with escapes.
    internal static string RandomString(Random random)
    {
        return random.Next(5) switch
        {
            0 => $"session-{random.Next()}",
            1 => "Q\"uote <b>&</b> caf" + (char)0x00E9 + " " + char.ConvertFromUtf32(0x1F389) + " \\ \t\n",
            2 => new string('w', random.Next(1, 200)),
            3 => "",
            _ => $"Player {random.Next(1000)}"
        };
    }

    internal static float RandomFloat(Random random)
    {
        return random.Next(6) switch
        {
            0 => -0f,
            1 => float.Epsilon,
            2 => float.MaxValue,
            _ => (float)((random.NextDouble() - 0.5) * Math.Pow(10, random.Next(-8, 9)))
        };
    }

    internal static Vector3 RandomVector(Random random)
    {
        return new Vector3(RandomFloat(random), RandomFloat(random), RandomFloat(random));
    }

    // The real service over loopback TCP, ticked by the test (as in the WIRE-5 loopback test).
    internal sealed class LoopbackServer : IAsyncDisposable
    {
        private readonly List<ServerConnection> _connections = [];

        private LoopbackServer(DedicatedServerHost host, DedicatedServerService service)
        {
            Host = host;
            Service = service;
        }

        public DedicatedServerHost Host { get; }

        public DedicatedServerService Service { get; }

        public static LoopbackServer Start()
        {
            DedicatedServerHost host = DedicatedServerHost.CreateRuntimeAttached(persistence: null);
            DedicatedServerService service = new(
                host,
                new TcpServerTransport(IPAddress.Loopback, 0),
                new TransportJsonCodec(),
                new LengthPrefixedMessageFramer());
            service.Start();
            return new LoopbackServer(host, service);
        }

        public async Task<RawClient> ConnectAndJoinAsync(string sessionId)
        {
            Task<ServerConnection> accept = Service.AcceptOnThreadPoolAsync();
            RawClient client = await RawClient.ConnectAsync(Service.Port, sessionId, receiveBufferSize: null);
            ServerConnection connection = await accept.WaitAsync(NetTimeout);
            _connections.Add(connection);
            await client.HandshakeAsync();
            DateTime deadline = DateTime.UtcNow + NetTimeout;
            while (!Host.IsConnectionJoined(connection.ConnectionId))
            {
                Assert.True(DateTime.UtcNow < deadline, $"{sessionId} never joined");
                await Task.Delay(10);
            }

            await client.WaitForJoinAcceptedAsync();
            return client;
        }

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
        }

        public async Task<bool> FlushAllAsync()
        {
            bool flushed = true;
            foreach (ServerConnection connection in _connections)
            {
                flushed &= await OutboundDrain.FlushAsync(connection, NetTimeout);
            }

            return flushed;
        }

        public async ValueTask DisposeAsync()
        {
            await Service.DisposeAsync();
            Host.Dispose();
        }
    }
}
