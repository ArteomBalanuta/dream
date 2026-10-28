using Xunit;
using Xunit.Abstractions;
using Dream.Multiplayer.Protocol;
using Dream.Server;
using Dream.Simulation.Combat;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Tests.Utilities;
using static Dream.Tests.Server.ServerConnectionTestFixtures;

namespace Dream.Tests.Server;

// NETR-1: each snapshot merged into a lagging connection's pending one used to copy the whole
// merged mutation and damage lists (O(n^2) on the tick thread, an allocation per merge). Merges
// now append into a pooled buffer. The wire bytes must not change.
public class ServerSnapshotCoalescingTests
{
    private readonly ITestOutputHelper _output;

    public ServerSnapshotCoalescingTests(ITestOutputHelper output)
    {
        _output = output;
    }

    public static TheoryData<string> Scenarios => new() { "three-builds", "sessions-and-damage", "random-50" };

    [Theory]
    [MemberData(nameof(Scenarios))]
    public void CoalesceBuffer_MatchesChainedTryCoalesce_ByteForByte(string scenario)
    {
        ServerSnapshotEnvelope[] snapshots = BuildScenario(scenario);
        ServerSnapshotEnvelope expected = FoldTryCoalesce(snapshots);
        ServerSnapshotCoalesceBuffer buffer = new();

        // Twice, so the second pass runs on a reused (Reset) buffer.
        for (int pass = 0; pass < 2; pass++)
        {
            buffer.Begin(snapshots[0]);
            for (int i = 1; i < snapshots.Length; i++)
            {
                buffer.Append(snapshots[i]);
            }

            Assert.Equal(EncodePayload(expected), EncodePayload(buffer.ToEnvelope()));
            buffer.Reset();
        }
    }

    [Theory]
    [MemberData(nameof(Scenarios))]
    public async Task ConnectionMergedFrames_AreByteIdenticalToChainedTryCoalesce(string scenario)
    {
        ServerSnapshotEnvelope[] snapshots = BuildScenario(scenario);
        GatedTransport transport = new("slow");
        await using ServerConnection connection = new(transport, CreateHost(new MutatingAuthority()), new TransportJsonCodec(), new LengthPrefixedMessageFramer());

        transport.Close();
        connection.EnqueueSnapshot(new ServerSnapshotMessage(snapshots[0]));
        Assert.True(await transport.SendStarted.WaitAsync(Deadline));
        for (int i = 1; i < snapshots.Length; i++)
        {
            connection.EnqueueSnapshot(new ServerSnapshotMessage(snapshots[i]));
        }

        Assert.Equal(snapshots.Length - 2, connection.CoalescedSnapshots);
        transport.Open();
        Assert.True(await connection.FlushOutboundAsync(Deadline));

        byte[][] expected = await ReferenceFramesAsync(
            new ServerSnapshotMessage(snapshots[0]),
            new ServerSnapshotMessage(FoldTryCoalesce(snapshots[1..])));
        AssertFramesEqual(expected, transport.Frames);
    }

    [Fact]
    public async Task MergeBoundaries_ActionsAndFullWorlds_MatchTheOldMerge()
    {
        ServerSnapshotEnvelope[] s = BuildScenario("random-50")[..8];
        ServerSnapshotEnvelope world = s[4] with { World = new WorldSnapshot(10, 8, 10, []) };
        ServerActionMessage action = new(new AuthoritativeActionResult("session-a", 7, 3, true, null));
        GatedTransport transport = new("slow");
        await using ServerConnection connection = new(transport, CreateHost(new MutatingAuthority()), new TransportJsonCodec(), new LengthPrefixedMessageFramer());

        transport.Close();
        connection.EnqueueSnapshot(new ServerSnapshotMessage(s[0]));
        Assert.True(await transport.SendStarted.WaitAsync(Deadline));
        connection.EnqueueSnapshot(new ServerSnapshotMessage(s[1]));
        connection.EnqueueSnapshot(new ServerSnapshotMessage(s[2]));
        connection.Enqueue(action);
        connection.EnqueueSnapshot(new ServerSnapshotMessage(s[3]));
        connection.EnqueueSnapshot(new ServerSnapshotMessage(world));
        connection.EnqueueSnapshot(new ServerSnapshotMessage(s[5]));
        connection.EnqueueSnapshot(new ServerSnapshotMessage(s[6]));
        connection.EnqueueSnapshot(new ServerSnapshotMessage(s[7]));
        transport.Open();
        Assert.True(await connection.FlushOutboundAsync(Deadline));

        byte[][] expected = await ReferenceFramesAsync(
            new ServerSnapshotMessage(s[0]),
            new ServerSnapshotMessage(FoldTryCoalesce([s[1], s[2]])),
            action,
            new ServerSnapshotMessage(s[3]),
            new ServerSnapshotMessage(world),
            new ServerSnapshotMessage(FoldTryCoalesce([s[5], s[6], s[7]])));
        AssertFramesEqual(expected, transport.Frames);
        Assert.Equal(3, connection.CoalescedSnapshots);
    }

    [Fact]
    public async Task TwentyThousandMerges_KeepEveryMutationInOrder()
    {
        const int Merges = 20_000;
        ServerConnectionOptions options = new() { MaxCoalescedWorldMutations = int.MaxValue };
        GatedTransport transport = new("slow");
        await using ServerConnection connection = new(transport, CreateHost(new MutatingAuthority()), new TransportJsonCodec(), new LengthPrefixedMessageFramer(1 << 24), options);
        Dictionary<string, ulong> sequences = new() { ["session-a"] = 1 };

        transport.Close();
        connection.EnqueueSnapshot(new ServerSnapshotMessage(CreateSnapshot(0, sequences, [Mutation(1)])));
        Assert.True(await transport.SendStarted.WaitAsync(Deadline));
        for (int i = 1; i <= Merges + 1; i++)
        {
            connection.EnqueueSnapshot(new ServerSnapshotMessage(CreateSnapshot((ulong)i, sequences, [Mutation((ulong)i + 1)])));
        }

        Assert.Equal(Merges, connection.CoalescedSnapshots);
        Assert.Equal(1, connection.PendingOutboundCount);
        transport.Open();
        Assert.True(await connection.FlushOutboundAsync(Deadline));

        IReadOnlyList<IProtocolMessage> sent = Decode(transport.Frames);
        ServerSnapshotEnvelope merged = Assert.IsType<ServerSnapshotMessage>(sent[1]).Snapshot;
        Assert.Equal((ulong)Merges + 1, merged.ServerTick);
        Assert.Equal(
            Enumerable.Range(2, Merges + 1).Select(sequence => (ulong)sequence).ToArray(),
            merged.WorldMutations.Select(mutation => mutation.Sequence).ToArray());
    }

    [Fact]
    public async Task EnqueueIntoAPendingMerge_AllocatesNothingAfterWarmup()
    {
        GatedTransport transport = new("slow", recordFrames: false);
        await using ServerConnection connection = new(transport, CreateHost(new MutatingAuthority()), new TransportJsonCodec(), new LengthPrefixedMessageFramer(1 << 24));
        ServerSnapshotMessage[] messages = Enumerable.Range(0, 64)
            .Select(i => new ServerSnapshotMessage(CreateSnapshot(
                (ulong)i,
                new Dictionary<string, ulong> { ["session-a"] = (ulong)i, ["session-b"] = 3 },
                [Mutation((ulong)i + 1)])))
            .ToArray();

        // Warm-up: grow a merge buffer to its working size, send it, and so return it to the pool.
        transport.Close();
        connection.EnqueueSnapshot(messages[0]);
        Assert.True(await transport.SendStarted.WaitAsync(Deadline));
        for (int i = 0; i < 4000; i++)
        {
            connection.EnqueueSnapshot(messages[i % messages.Length]);
        }

        transport.Open();
        Assert.True(await connection.FlushOutboundAsync(Deadline));

        // A pending merge behind a blocked write: each enqueue only appends one mutation.
        transport.Close();
        connection.EnqueueSnapshot(messages[0]);
        Assert.True(await transport.SendStarted.WaitAsync(Deadline));
        int next = 0;
        long bytes = AllocationAssert.AtMost(
            0,
            () => connection.EnqueueSnapshot(messages[next++ % messages.Length]),
            iterations: 20);
        _output.WriteLine($"merge enqueue: {bytes} B after {next} enqueues");
        Assert.False(connection.IsFaulted);
        transport.Open();
        Assert.True(await connection.FlushOutboundAsync(Deadline));
    }

    [Fact]
    public async Task Overflow_DropsTheMergedDeltas_AndTheServiceServesAWorldResync()
    {
        MutatingAuthority authority = new() { MutationsPerTick = 3 };
        DedicatedServerHost host = CreateHost(authority);
        ServerConnectionOptions options = new() { MaxCoalescedWorldMutations = 8 };
        await using DedicatedServerService service = new(host, new NoopServerTransport(), new TransportJsonCodec(), new LengthPrefixedMessageFramer(), options);
        GatedTransport transport = new("slow");
        ServerConnection connection = new(transport, host, new TransportJsonCodec(), new LengthPrefixedMessageFramer(), options);
        service.AttachConnection(connection);
        host.HandleClientMessage("slow", new ClientJoinRequestMessage("session-a", "a"));
        transport.Close();

        // Tick 1 blocks in the write; 2 waits; 3 merges (6 mutations); 4 would make 9 > 8.
        for (int tick = 1; tick <= 4; tick++)
        {
            await service.AdvanceOnceAsync(SimulationClock.FixedDeltaSeconds);
            if (tick == 1)
            {
                Assert.True(await transport.SendStarted.WaitAsync(Deadline));
            }
        }

        Assert.Equal(1, connection.OverflowResyncs);
        Assert.Equal(1, service.CaptureMetrics().CoalesceOverflowResyncs);
        // Tick 5 serves the resync after its tick; it goes out behind the merged snapshot.
        await service.AdvanceOnceAsync(SimulationClock.FixedDeltaSeconds);
        Assert.Equal(1, authority.WorldResyncs);
        transport.Open();
        Assert.True(await connection.FlushOutboundAsync(Deadline));

        ServerSnapshotEnvelope[] sent = Decode(transport.Frames)
            .Select(message => Assert.IsType<ServerSnapshotMessage>(message).Snapshot)
            .ToArray();
        Assert.Equal(4, sent.Length);
        Assert.Equal(1ul, sent[0].ServerTick);
        Assert.Equal(3, sent[0].WorldMutations.Count);
        Assert.Equal(4ul, sent[1].ServerTick);
        Assert.Empty(sent[1].WorldMutations);
        Assert.NotNull(sent[2].World);
        Assert.Equal(5ul, sent[3].ServerTick);
        Assert.Equal([13ul, 14ul, 15ul], sent[3].WorldMutations.Select(mutation => mutation.Sequence).ToArray());
        Assert.Equal(1, service.ConnectionCount);
        Assert.False(connection.IsFaulted);
    }

    private static ServerSnapshotEnvelope[] BuildScenario(string scenario)
    {
        switch (scenario)
        {
            case "three-builds":
                return
                [
                    CreateSnapshot(1, new Dictionary<string, ulong> { ["session-a"] = 1 }, [Mutation(1)]),
                    CreateSnapshot(2, new Dictionary<string, ulong> { ["session-a"] = 2 }, [Mutation(2)]),
                    CreateSnapshot(3, new Dictionary<string, ulong> { ["session-a"] = 3 }, [Mutation(3)]),
                    CreateSnapshot(4, new Dictionary<string, ulong> { ["session-a"] = 4 }, [Mutation(4), Mutation(5, WorldMutationKind.CubeMoved)])
                ];
            case "sessions-and-damage":
                return
                [
                    CreateSnapshot(10, new Dictionary<string, ulong> { ["session-a"] = 9 }, []),
                    CreateSnapshot(11, new Dictionary<string, ulong> { ["session-b"] = 4, ["session-a"] = 7 }, [Mutation(20, WorldMutationKind.CubeMoved), Mutation(21, WorldMutationKind.CubeMoved)],
                        [new DamageEventDelta(5, CreatureKind.Enemy, 3, false, 47, 50)], checksum: 99),
                    CreateSnapshot(12, new Dictionary<string, ulong> { ["session-c"] = 1 }, [], mode: PlayerInteractionMode.Combat),
                    CreateSnapshot(13, new Dictionary<string, ulong> { ["session-b"] = 6, ["session-a"] = 12 }, [Mutation(22, WorldMutationKind.CubeDestroyed)],
                        [new DamageEventDelta(5, CreatureKind.Enemy, 47, true, 0, 50), new DamageEventDelta(6, CreatureKind.Player, 1, false, 99, 100)]),
                    CreateSnapshot(14, new Dictionary<string, ulong> { ["session-a"] = 13 }, [Mutation(23)], checksum: 1234)
                ];
            case "random-50":
                Random random = new(1028);
                ulong sequence = 1;
                ServerSnapshotEnvelope[] snapshots = new ServerSnapshotEnvelope[50];
                for (int i = 0; i < snapshots.Length; i++)
                {
                    Dictionary<string, ulong> sequences = [];
                    foreach (string session in new[] { "session-a", "session-b", "session-c" })
                    {
                        if (random.Next(3) != 0)
                        {
                            sequences[session] = (ulong)random.Next(1, 200);
                        }
                    }

                    WorldMutationDelta[] mutations = new WorldMutationDelta[random.Next(0, 4)];
                    for (int m = 0; m < mutations.Length; m++)
                    {
                        mutations[m] = Mutation(sequence++, (WorldMutationKind)random.Next(0, 4));
                    }

                    DamageEventDelta[] damage = new DamageEventDelta[random.Next(0, 2)];
                    for (int d = 0; d < damage.Length; d++)
                    {
                        damage[d] = new DamageEventDelta(random.Next(1, 9), CreatureKind.Enemy, random.Next(1, 20), random.Next(4) == 0, random.Next(0, 50), 50);
                    }

                    snapshots[i] = CreateSnapshot(
                        (ulong)(i + 1),
                        sequences,
                        mutations,
                        damage,
                        checksum: random.Next(5) == 0 ? (ulong)random.NextInt64() : null,
                        mode: random.Next(2) == 0 ? PlayerInteractionMode.Building : PlayerInteractionMode.Combat);
                }

                return snapshots;
            default:
                throw new ArgumentOutOfRangeException(nameof(scenario));
        }
    }

    private static ServerSnapshotEnvelope FoldTryCoalesce(IReadOnlyList<ServerSnapshotEnvelope> snapshots)
    {
        ServerSnapshotEnvelope merged = snapshots[0];
        for (int i = 1; i < snapshots.Count; i++)
        {
            Assert.True(ServerSnapshotEnvelope.TryCoalesce(merged, snapshots[i], out merged));
        }

        return merged;
    }

    private static byte[] EncodePayload(ServerSnapshotEnvelope snapshot)
    {
        TransportJsonCodec codec = new();
        return codec.Encode(new TransportEnvelope(TransportEnvelope.CurrentProtocolVersion, "server.snapshot", 1), new ServerSnapshotMessage(snapshot));
    }

    // The frames a connection writes for these messages sent one by one, unmerged.
    private static async Task<byte[][]> ReferenceFramesAsync(params IServerToClientMessage[] messages)
    {
        GatedTransport transport = new("reference");
        await using ServerConnection connection = new(transport, CreateHost(new MutatingAuthority()), new TransportJsonCodec(), new LengthPrefixedMessageFramer());
        foreach (IServerToClientMessage message in messages)
        {
            switch (message)
            {
                case ServerSnapshotMessage snapshot:
                    await connection.SendSnapshotAsync(snapshot);
                    break;
                case ServerActionMessage action:
                    await connection.SendActionAsync(action);
                    break;
            }
        }

        return transport.Frames;
    }

    private static void AssertFramesEqual(byte[][] expected, byte[][] actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.True(expected[i].AsSpan().SequenceEqual(actual[i]), $"frame {i} differs from the old merge's bytes.");
        }
    }
}
