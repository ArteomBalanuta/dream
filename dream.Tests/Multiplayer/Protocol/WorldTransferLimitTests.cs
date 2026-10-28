using System.Text;
using System.Text.Json;
using OpenTK.Mathematics;
using Xunit;
using Xunit.Abstractions;
using Dream.Client.Networking;
using Dream.Multiplayer.Protocol;
using Dream.Simulation.Combat;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using static Dream.Tests.Server.ServerConnectionTestFixtures;

namespace Dream.Tests.Multiplayer.Protocol;

// Wave 10 fix loop 1 (reviewer S1, S2 and nits): the client's world-transfer reassembler is
// bounded (items, held messages, a deadline), the compact world rejects insane dimensions and
// overflowing deltas, the server refuses server-only kinds from a client before decoding them,
// and the server-to-client frame cap is sized from the largest legitimate frame.
public sealed class WorldTransferLimitTests(ITestOutputHelper output)
{
    private static readonly TransportJsonCodec Codec = new();

    // ---- Reassembler limits ------------------------------------------------------------------

    [Fact]
    public void HeldMessages_PastTheLimit_EndTheTransfer()
    {
        WorldTransferReassembler reassembler = new(new WorldTransferLimits { MaxHeldMessages = 3 });
        List<IServerToClientMessage> delivered = [];
        reassembler.Accept(Carrier(7), delivered.Add);
        for (int i = 0; i < 3; i++)
        {
            reassembler.Accept(Snapshot((ulong)i), delivered.Add);
        }

        WorldTransferLimitException error = Assert.Throws<WorldTransferLimitException>(
            () => reassembler.Accept(Snapshot(3), delivered.Add));
        Assert.Contains("held more than 3 messages", error.Message);
        Assert.Empty(delivered);
    }

    [Fact]
    public void DefaultHeldMessageLimit_Is4096()
    {
        Assert.Equal(4096, WorldTransferLimits.Default.MaxHeldMessages);
        WorldTransferReassembler reassembler = new();
        reassembler.Accept(Carrier(1), _ => { });
        for (int i = 0; i < 4096; i++)
        {
            reassembler.Accept(Snapshot((ulong)i), _ => { });
        }

        Assert.Throws<WorldTransferLimitException>(() => reassembler.Accept(Snapshot(4096), _ => { }));
    }

    // The item budget comes from chunk 0's dimensions: 3 items per cell, at least MinItems.
    [Fact]
    public void Items_PastTheBudgetOfChunkZerosDimensions_EndTheTransfer()
    {
        WorldTransferLimits limits = new() { MinItems = 10 };
        Assert.Equal(3 * 2 * 2 * 2, limits.MaxItemsFor(2, 2, 2));
        Assert.Equal(10, limits.MaxItemsFor(1, 1, 1));
        Assert.Equal(10, limits.MaxItemsFor(0, 5, 5));
        Assert.Equal(limits.MaxItems, limits.MaxItemsFor(WorldSnapshotJsonConverter.MaxDimension, WorldSnapshotJsonConverter.MaxDimension, 4));

        WorldTransferReassembler reassembler = new(limits);
        reassembler.Accept(Carrier(2), _ => { });
        // 2x2x2: 24 items. 20 cubes fit, 5 more do not.
        reassembler.Accept(Chunk(2, 0, final: false, Cubes(2, 2, 2, 20)), _ => { });
        WorldTransferLimitException error = Assert.Throws<WorldTransferLimitException>(
            () => reassembler.Accept(Chunk(2, 1, final: true, Cubes(2, 2, 2, 5)), _ => { }));
        Assert.Contains("passed 24 items", error.Message);
    }

    [Fact]
    public void DefaultItemBudget_FitsTheLargestTestWorld_AndBoundsAHugeOne()
    {
        WorldTransferLimits limits = WorldTransferLimits.Default;
        // CreateWorld's 128x64x128 worlds (World85k) and the server's largest floor (100x8x100).
        Assert.True(limits.MaxItemsFor(128, 64, 128) >= 85_000 + 1_000 + 10);
        Assert.True(limits.MaxItemsFor(100, 8, 100) >= 3 * 100 * 8 * 100);
        Assert.Equal(1 << 22, limits.MaxItemsFor(4096, 4096, 64));
    }

    [Fact]
    public void Chunks_ThatDisagreeOnTheDimensions_EndTheTransfer()
    {
        WorldTransferReassembler reassembler = new();
        reassembler.Accept(Carrier(3), _ => { });
        reassembler.Accept(Chunk(3, 0, final: false, Cubes(4, 4, 4, 2)), _ => { });
        WorldTransferLimitException error = Assert.Throws<WorldTransferLimitException>(
            () => reassembler.Accept(Chunk(3, 1, final: true, Cubes(4000, 4000, 4, 1)), _ => { }));
        Assert.Contains("4x4x4", error.Message);
    }

    [Fact]
    public void Deadline_CountsFromTheCarrier_OnTheInjectedClock()
    {
        ManualTimeProvider clock = new();
        // The progress timeout is off: this is about the total deadline alone.
        WorldTransferReassembler reassembler = new(new WorldTransferLimits { Deadline = TimeSpan.FromSeconds(60), ProgressTimeout = TimeSpan.Zero }, clock);
        reassembler.CheckDeadline();
        clock.Advance(TimeSpan.FromSeconds(500));
        reassembler.CheckDeadline(); // No transfer in progress: nothing to time.

        reassembler.Accept(Carrier(4), _ => { });
        clock.Advance(TimeSpan.FromSeconds(59));
        reassembler.CheckDeadline();
        clock.Advance(TimeSpan.FromSeconds(2));
        WorldTransferLimitException error = Assert.Throws<WorldTransferLimitException>(reassembler.CheckDeadline);
        Assert.Contains("did not finish within 60 s", error.Message);
    }

    [Fact]
    public void Deadline_DoesNotCarryOverToTheNextTransfer()
    {
        ManualTimeProvider clock = new();
        WorldTransferReassembler reassembler = new(new WorldTransferLimits { Deadline = TimeSpan.FromSeconds(60), ProgressTimeout = TimeSpan.Zero }, clock);
        reassembler.Accept(Carrier(1), _ => { });
        clock.Advance(TimeSpan.FromSeconds(50));
        Assert.NotNull(reassembler.Accept(Chunk(1, 0, final: true, Cubes(4, 4, 4, 3)), _ => { }));
        clock.Advance(TimeSpan.FromSeconds(50));
        reassembler.Accept(Carrier(2), _ => { });
        clock.Advance(TimeSpan.FromSeconds(50));
        reassembler.CheckDeadline();
        // Wave 10 fix loop 2 (reviewer S3): 300 s in total, 30 s without a chunk.
        Assert.Equal(TimeSpan.FromSeconds(300), WorldTransferLimits.Default.Deadline);
        Assert.Equal(TimeSpan.FromSeconds(30), WorldTransferLimits.Default.ProgressTimeout);
    }

    // The client session: a carrier whose final chunk never comes, while snapshots keep arriving
    // (so the silence watchdog stays satisfied), ends the session at the deadline with a clear
    // reason instead of holding every message forever.
    [Fact]
    public async Task Session_WithATransferThatNeverFinishes_EndsAtTheDeadline_WithAClearReason()
    {
        ManualTimeProvider clock = new();
        GatedTransport transport = new("never-final");
        await using RemoteClientSession session = new(
            transport,
            new TransportJsonCodec(),
            new LengthPrefixedMessageFramer(1 << 20),
            new RemoteClientOptions("127.0.0.1", 1, "session-a", "A", "limits"),
            clock,
            new WorldTransferLimits { Deadline = TimeSpan.FromSeconds(60) });
        session.StartBackgroundReceive();
        ulong sequence = 1;
        transport.Feed(Frame(Carrier(9), sequence++));
        transport.Feed(Frame(Chunk(9, 0, final: false, Cubes(4, 4, 4, 5)), sequence++));
        for (int i = 0; i < 5; i++)
        {
            transport.Feed(Frame(Snapshot((ulong)i), sequence++));
        }

        await WaitUntilAsync(() => session.InboundFrameCount == 7);
        Assert.True(session.IsReceivingWorld);
        Assert.True(session.IsConnected);

        clock.Advance(TimeSpan.FromSeconds(61));
        transport.Feed(Frame(Snapshot(99), sequence));
        await WaitUntilAsync(() => !session.IsConnected);
        Assert.StartsWith("World transfer failed:", session.DisconnectReason);
        Assert.Contains("did not finish within 60 s", session.DisconnectReason);
        Assert.False(session.TryDequeueInbound(out _));
    }

    // ---- Progress-based deadline (wave 10 fix loop 2, reviewer S3) -----------------------------

    // A World85k resync on a 12.5-15 KB/s link: a chunk every 9 s with a snapshot in between
    // takes 270 s in total, past the old 120 s bound. It keeps making progress, so it completes.
    [Fact]
    public void SlowButSteadyTransfer_PastTheOld120sBound_Completes()
    {
        ManualTimeProvider clock = new();
        WorldTransferReassembler reassembler = new(WorldTransferLimits.Default, clock);
        List<IServerToClientMessage> delivered = [];
        reassembler.Accept(Carrier(5), delivered.Add);
        const int chunks = 30;
        WorldTransferReceiver? completed = null;
        for (int i = 0; i < chunks; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(4.5));
            reassembler.Accept(Snapshot((ulong)i), delivered.Add);
            reassembler.CheckDeadline();
            clock.Advance(TimeSpan.FromSeconds(4.5));
            completed = reassembler.Accept(Chunk(5, i, final: i == chunks - 1, Cubes(64, 8, 64, 3)), delivered.Add);
            reassembler.CheckDeadline();
        }

        Assert.NotNull(completed);
        Assert.Equal(chunks, completed.ChunksReceived);
        Assert.Null(reassembler.Current);
        ServerSnapshotMessage world = Assert.IsType<ServerSnapshotMessage>(delivered[0]);
        Assert.NotNull(world.Snapshot.World);
        Assert.Equal(chunks + 1, delivered.Count);
    }

    // Snapshots keep arriving (so the silence watchdog would stay satisfied) but no chunk does:
    // 30 s after the last chunk the transfer fails with its reason, long before the 300 s total.
    [Fact]
    public void StalledTransfer_WithNoChunkFor30s_FailsWithItsReason()
    {
        ManualTimeProvider clock = new();
        WorldTransferReassembler reassembler = new(WorldTransferLimits.Default, clock);
        reassembler.Accept(Carrier(6), _ => { });
        clock.Advance(TimeSpan.FromSeconds(20));
        reassembler.Accept(Chunk(6, 0, final: false, Cubes(8, 8, 8, 2)), _ => { });
        reassembler.CheckDeadline();
        for (int i = 0; i < 6; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(5));
            reassembler.Accept(Snapshot((ulong)i), _ => { });
            reassembler.CheckDeadline(); // 30 s exactly is still inside the bound.
        }

        clock.Advance(TimeSpan.FromSeconds(1));
        reassembler.Accept(Snapshot(6), _ => { });
        WorldTransferLimitException error = Assert.Throws<WorldTransferLimitException>(reassembler.CheckDeadline);
        Assert.Contains("World transfer 6 stalled: no chunk for 30 s", error.Message);
        Assert.Contains("1 chunks", error.Message);
    }

    // The carrier counts as progress until the first chunk: a transfer whose first chunk never
    // comes fails 30 s after the carrier.
    [Fact]
    public void StalledTransfer_BeforeItsFirstChunk_FailsAfterTheProgressTimeout()
    {
        ManualTimeProvider clock = new();
        WorldTransferReassembler reassembler = new(WorldTransferLimits.Default, clock);
        reassembler.Accept(Carrier(8), _ => { });
        clock.Advance(TimeSpan.FromSeconds(30));
        reassembler.CheckDeadline();
        clock.Advance(TimeSpan.FromSeconds(1));
        WorldTransferLimitException error = Assert.Throws<WorldTransferLimitException>(reassembler.CheckDeadline);
        Assert.Contains("stalled: no chunk for 30 s", error.Message);
    }

    // The total deadline still ends a transfer that trickles in forever, chunk by chunk.
    [Fact]
    public void TricklingTransfer_EndsAtTheTotalDeadline()
    {
        ManualTimeProvider clock = new();
        WorldTransferReassembler reassembler = new(WorldTransferLimits.Default, clock);
        reassembler.Accept(Carrier(9), _ => { });
        int index = 0;
        for (; index < 30; index++)
        {
            clock.Advance(TimeSpan.FromSeconds(10));
            reassembler.Accept(Chunk(9, index, final: false, Cubes(64, 8, 64, 1)), _ => { });
            reassembler.CheckDeadline();
        }

        clock.Advance(TimeSpan.FromSeconds(10));
        reassembler.Accept(Chunk(9, index, final: false, Cubes(64, 8, 64, 1)), _ => { });
        WorldTransferLimitException error = Assert.Throws<WorldTransferLimitException>(reassembler.CheckDeadline);
        Assert.Contains("did not finish within 300 s", error.Message);
    }

    // The client session checks the bounds after it accepted a frame: a final chunk arriving
    // just past the total deadline completes its transfer instead of being rejected, and the
    // session stays connected with the world delivered.
    [Fact]
    public async Task Session_FinalChunkArrivingRightAtTheDeadline_CompletesTheTransfer()
    {
        ManualTimeProvider clock = new();
        GatedTransport transport = new("final-at-deadline");
        await using RemoteClientSession session = new(
            transport,
            new TransportJsonCodec(),
            new LengthPrefixedMessageFramer(1 << 20),
            new RemoteClientOptions("127.0.0.1", 1, "session-a", "A", "limits"),
            clock,
            new WorldTransferLimits { Deadline = TimeSpan.FromSeconds(60) });
        session.StartBackgroundReceive();
        ulong sequence = 1;
        transport.Feed(Frame(Carrier(11), sequence++));
        transport.Feed(Frame(Chunk(11, 0, final: false, Cubes(8, 8, 8, 2)), sequence++));
        await WaitUntilAsync(() => session.InboundFrameCount == 2);
        clock.Advance(TimeSpan.FromSeconds(25));
        transport.Feed(Frame(Chunk(11, 1, final: false, Cubes(8, 8, 8, 2)), sequence++));
        transport.Feed(Frame(Snapshot(1), sequence++));
        // The frame count goes up once a frame was processed (accepted and its bounds checked), so
        // the last one has finished before the clock passes the deadline.
        await WaitUntilAsync(() => session.InboundFrameCount == 4);
        Assert.True(session.IsReceivingWorld);
        clock.Advance(TimeSpan.FromSeconds(25));

        // 60.5 s after the carrier and 35.5 s after the last chunk: past both bounds, but this
        // frame is the final chunk, so it is accepted before the bounds are checked.
        clock.Advance(TimeSpan.FromSeconds(10.5));
        transport.Feed(Frame(Chunk(11, 2, final: true, Cubes(8, 8, 8, 2)), sequence++));
        await WaitUntilAsync(() => session.WorldTransfersCompleted == 1 || !session.IsConnected);
        Assert.True(session.IsConnected, session.DisconnectReason);
        Assert.False(session.IsReceivingWorld);
        Assert.True(session.TryDequeueInbound(out IServerToClientMessage? first));
        ServerSnapshotMessage world = Assert.IsType<ServerSnapshotMessage>(first);
        Assert.Equal(6, world.Snapshot.World!.Value.PlacedCubes!.Count);
        Assert.True(session.TryDequeueInbound(out IServerToClientMessage? held));
        Assert.Equal(1UL, Assert.IsType<ServerSnapshotMessage>(held).Snapshot.ServerTick);
    }

    // The session on the default limits: a transfer that progresses every 10 s for 200 s (past
    // the old 120 s bound) completes; one that then stalls for more than 30 s ends the session
    // with the stall reason.
    [Fact]
    public async Task Session_SlowSteadyTransferCompletes_AndAStalledOneEndsWithItsReason()
    {
        ManualTimeProvider clock = new();
        GatedTransport transport = new("slow-then-stalled");
        await using RemoteClientSession session = new(
            transport,
            new TransportJsonCodec(),
            new LengthPrefixedMessageFramer(1 << 20),
            new RemoteClientOptions("127.0.0.1", 1, "session-a", "A", "limits"),
            clock,
            WorldTransferLimits.Default);
        session.StartBackgroundReceive();
        ulong sequence = 1;
        int frames = 0;
        transport.Feed(Frame(Carrier(21), sequence++));
        await WaitUntilAsync(() => session.InboundFrameCount == 1);
        frames = 1;
        for (int i = 0; i < 20; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(10));
            transport.Feed(Frame(Chunk(21, i, final: i == 19, Cubes(64, 8, 64, 2)), sequence++));
            frames++;
            await WaitUntilAsync(() => session.InboundFrameCount == frames);
        }

        Assert.True(session.IsConnected, session.DisconnectReason);
        Assert.Equal(1, session.WorldTransfersCompleted);

        transport.Feed(Frame(Carrier(22), sequence++));
        transport.Feed(Frame(Chunk(22, 0, final: false, Cubes(64, 8, 64, 2)), sequence++));
        frames += 2;
        await WaitUntilAsync(() => session.InboundFrameCount == frames);
        for (int i = 0; i < 6; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(5));
            transport.Feed(Frame(Snapshot((ulong)i), sequence++));
            frames++;
            await WaitUntilAsync(() => session.InboundFrameCount == frames);
            Assert.True(session.IsConnected, session.DisconnectReason);
        }

        clock.Advance(TimeSpan.FromSeconds(1));
        transport.Feed(Frame(Snapshot(99), sequence));
        await WaitUntilAsync(() => !session.IsConnected);
        Assert.StartsWith("World transfer failed:", session.DisconnectReason);
        Assert.Contains("World transfer 22 stalled: no chunk for 30 s", session.DisconnectReason);
    }

    // ---- Compact world validation --------------------------------------------------------------

    [Theory]
    [InlineData("{\"w\":-1,\"h\":8,\"d\":8}", "must not be negative")]
    [InlineData("{\"w\":8,\"h\":-8,\"d\":8}", "must not be negative")]
    [InlineData("{\"w\":20000,\"h\":1,\"d\":1}", "out of range")]
    [InlineData("{\"w\":16384,\"h\":16384,\"d\":16}", "out of range")]
    public void CompactWorld_WithInsaneDimensions_IsRejected(string json, string expected)
    {
        JsonException error = Assert.Throws<JsonException>(() => ReadWorld(json));
        Assert.Contains(expected, error.Message);
    }

    [Fact]
    public void CompactWorld_AtTheDimensionBounds_IsAccepted()
    {
        WorldSnapshot world = ReadWorld($"{{\"w\":{WorldSnapshotJsonConverter.MaxDimension},\"h\":0,\"d\":{WorldSnapshotJsonConverter.MaxDimension}}}");
        Assert.Equal(WorldSnapshotJsonConverter.MaxDimension, world.Width);
        Assert.Equal(0, world.Height);
    }

    // A cell at -1 followed by a delta of long.MinValue: the running sum would wrap the long to
    // long.MaxValue. It is a decode error at the sum, for cubes, subcells and rubble.
    [Theory]
    [InlineData("{\"w\":8,\"h\":8,\"d\":8,\"p\":[-1,0,0,4,1,-9223372036854775808,0,0,0]}")]
    [InlineData("{\"w\":8,\"h\":8,\"d\":8,\"p\":[0,-1,0,4,1,0,-9223372036854775808,0,0]}")]
    [InlineData("{\"w\":8,\"h\":8,\"d\":8,\"s\":[-1,0,0,-9223372036854775808,0,0]}")]
    [InlineData("{\"w\":8,\"h\":8,\"d\":8,\"r\":[0,0,-1,1,1,0,0,-9223372036854775808,0]}")]
    public void CompactWorld_WithOverflowingCellDeltas_IsRejected(string json)
    {
        JsonException error = Assert.Throws<JsonException>(() => ReadWorld(json));
        Assert.Contains("overflows", error.Message);
    }

    // The id sum is checked too; an id delta that leaves the int range is rejected.
    [Fact]
    public void CompactWorld_WithAnIdDeltaOutOfRange_IsRejected()
    {
        // t = idDelta * 8 + flags (2: id, 4: material).
        long t = (long.MinValue / 8 * 8) + 2 + 4;
        string json = $"{{\"w\":8,\"h\":8,\"d\":8,\"p\":[0,0,0,1006,1,0,0,1,{t},1]}}";
        Assert.Throws<JsonException>(() => ReadWorld(json));
    }

    // ---- Server-only kinds from a client ---------------------------------------------------------

    // The payload is not a valid chunk, so decoding it would fail with a JSON error; the refusal
    // names the kind instead, which shows the payload was never decoded.
    [Theory]
    [InlineData(WorldChunkMessage.Kind)]
    [InlineData("server.snapshot")]
    [InlineData("server.join-accepted")]
    [InlineData("server.hello")]
    [InlineData("server.action")]
    [InlineData("server.join-rejected")]
    public void DecodeFromClient_RefusesServerOnlyKinds_BeforeTheirPayload(string kind)
    {
        byte[] payload = Encoding.UTF8.GetBytes(
            $"{{\"protocolVersion\":{TransportEnvelope.CurrentProtocolVersion},\"messageKind\":\"{kind}\",\"sequence\":1,\"payload\":12345}}");
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => Codec.DecodeFromClient(payload));
        Assert.Contains($"server-only message kind '{kind}'", error.Message);
        Assert.True(TransportJsonCodec.IsServerOnlyKind(kind));
        // The client side still decodes the kind (and here fails on the bad payload).
        Assert.ThrowsAny<Exception>(() => Codec.Decode(payload));
    }

    [Fact]
    public void DecodeFromClient_AcceptsClientKinds()
    {
        byte[] payload = Codec.Encode(
            new TransportEnvelope(TransportEnvelope.CurrentProtocolVersion, "client.join-request", 3),
            new ClientJoinRequestMessage("session-a", "A"));
        DecodedProtocolMessage decoded = Codec.DecodeFromClient(payload);
        Assert.IsType<ClientJoinRequestMessage>(decoded.Message);
        Assert.False(TransportJsonCodec.IsServerOnlyKind("client.join-request"));
        Assert.False(TransportJsonCodec.IsServerOnlyKind("player.command"));
    }

    // ---- The server-to-client frame cap ----------------------------------------------------------

    // Reviewer S2: with chunked transfer the largest legitimate server frame is a coalesced
    // snapshot at the merge caps (ServerConnectionOptions: 4096 world mutations and 4096 damage
    // events; past them the deltas are dropped for a resync) or a world chunk. Measured here at
    // the worst case (every mutation a fracture with its pieces, long float coordinates, hundreds
    // of actors; 2.74 MB when measured) together with a World85k transfer's carrier and chunks;
    // the 8 MB default cap keeps at least 2.5x the largest of them as headroom.
    [Fact]
    public void ServerToClientFrameCap_HoldsTheLargestLegitimateFrame_WithHeadroom()
    {
        LengthPrefixedMessageFramer framer = FrameLimitOptions.Default.CreateServerFramer();
        int coalesced = FrameBytes(framer, new ServerSnapshotMessage(WorstCaseCoalescedSnapshot()));

        WorldSnapshot world85k = WorldTransferProtocolTests.CreateWorld(cubes: 85_000, rubble: 1_000, subcells: 10);
        JoinAcceptedMessage join = new("session-a", 1, 100, world85k);
        int carrier = FrameBytes(framer, (JoinAcceptedMessage)WorldTransfer.ToCarrier(join, 1));
        int largestChunk = 0;
        int chunks = 0;
        long worldBytes = 0;
        WorldTransferSender sender = new(1, WorldSnapshotCapture.FromSnapshot(world85k), releaseCaptureWhenDone: true);
        while (!sender.IsComplete)
        {
            System.Buffers.ArrayBufferWriter<byte> chunkPayload = new();
            using (Utf8JsonWriter writer = new(chunkPayload))
            {
                sender.WriteNextChunk(writer, WorldChunkMessage.DefaultChunkBytes);
            }

            System.Buffers.ArrayBufferWriter<byte> frame = new();
            Codec.WriteEnvelope(new TransportEnvelope(TransportEnvelope.CurrentProtocolVersion, WorldChunkMessage.Kind, (ulong)chunks + 2), chunkPayload.WrittenSpan, frame);
            largestChunk = Math.Max(largestChunk, frame.WrittenCount + framer.HeaderLength);
            worldBytes += frame.WrittenCount + framer.HeaderLength;
            chunks++;
        }

        int largest = Math.Max(coalesced, Math.Max(carrier, largestChunk));
        output.WriteLine($"worst-case coalesced snapshot {coalesced:N0} B; World85k carrier {carrier:N0} B, {chunks} chunks, largest {largestChunk:N0} B, {worldBytes:N0} B in all");
        output.WriteLine($"server-to-client cap {FrameLimitOptions.DefaultServerToClientMaxPayloadBytes:N0} B = {FrameLimitOptions.DefaultServerToClientMaxPayloadBytes / (double)largest:0.0}x the largest legitimate frame");

        Assert.InRange(largestChunk, WorldChunkMessage.DefaultChunkBytes, WorldChunkMessage.DefaultChunkBytes + 4096);
        Assert.Equal(8 * 1024 * 1024, FrameLimitOptions.DefaultServerToClientMaxPayloadBytes);
        Assert.True(
            2.5 * largest <= FrameLimitOptions.DefaultServerToClientMaxPayloadBytes,
            $"the largest legitimate frame ({largest:N0} B) leaves less than 2.5x headroom under the {FrameLimitOptions.DefaultServerToClientMaxPayloadBytes:N0} B cap");
        // Every frame of a world transfer is far under it: no frame carries a whole world any more.
        Assert.True(largestChunk * 64L < FrameLimitOptions.DefaultServerToClientMaxPayloadBytes);
    }

    // ---- Helpers ---------------------------------------------------------------------------------

    private static ServerSnapshotEnvelope WorstCaseCoalescedSnapshot()
    {
        const int MutationCap = 4096;
        const int DamageCap = 4096;
        WorldMutationDelta[] mutations = new WorldMutationDelta[MutationCap];
        for (int i = 0; i < mutations.Length; i++)
        {
            GeometryCellKey cell = new(99 - (i % 100), 7 - (i / 100 % 8), 99 - (i / 800 % 100));
            IReadOnlyList<GeometryFracturePiece> pieces = CubeFracturePattern.CreateFirstPassRubble(cell);
            // Moved pieces: coordinates with every float digit.
            GeometryFracturePiece[] moved = [.. pieces.Select(piece => piece with { Center = piece.Center + new Vector3(0.0123457f, -0.0234567f, 0.0345679f) })];
            mutations[i] = WorldMutationDelta.Fractured(new Vector3(cell.X, cell.Y, cell.Z), GeometryMaterialId.Rock, moved) with
            {
                Sequence = 10_000_000_000UL + (ulong)i,
                ActorId = 1_073_741_824 + i
            };
        }

        DamageEventDelta[] damage = new DamageEventDelta[DamageCap];
        for (int i = 0; i < damage.Length; i++)
        {
            damage[i] = new DamageEventDelta(1_000_000 + i, CreatureKind.Enemy, 12345, i % 2 == 0, 54321, 99999);
        }

        ActorSnapshot[] actors = new ActorSnapshot[320];
        Dictionary<string, ulong> sequences = [];
        for (int i = 0; i < actors.Length; i++)
        {
            bool player = i < 64;
            string? session = player ? $"session-{i:D4}-{Guid.Empty}" : null;
            if (session != null)
            {
                sequences[session] = 10_000_000UL + (ulong)i;
            }

            actors[i] = new ActorSnapshot(
                1_000_000 + i,
                player ? CreatureKind.Player : CreatureKind.Enemy,
                session,
                new Vector3(12.3456789f + i, 1.2345678f, 98.7654321f - i),
                new Vector3(0.70710677f, 0f, -0.70710677f),
                99999,
                99999,
                true,
                DisplayName: player ? $"A long display name {i:D4}" : null);
        }

        return new ServerSnapshotEnvelope(
            ulong.MaxValue / 3,
            sequences,
            actors,
            default,
            null,
            mutations,
            damage,
            WorldChecksum: ulong.MaxValue,
            WorldChecksumSequence: ulong.MaxValue / 5);
    }

    private static int FrameBytes<TMessage>(LengthPrefixedMessageFramer framer, TMessage message)
        where TMessage : IServerToClientMessage
    {
        byte[] payload = Codec.Encode(new TransportEnvelope(TransportEnvelope.CurrentProtocolVersion, message.MessageKind, ulong.MaxValue), message);
        return framer.Frame(payload).Length;
    }

    private static WorldSnapshot ReadWorld(string json)
    {
        Utf8JsonReader reader = new(Encoding.UTF8.GetBytes(json));
        Assert.True(reader.Read());
        return WorldSnapshotJsonConverter.ReadWorld(ref reader);
    }

    private static ServerSnapshotMessage Carrier(int transferId)
    {
        return new ServerSnapshotMessage(CreateSnapshot(1, new Dictionary<string, ulong>(), []), transferId);
    }

    private static ServerSnapshotMessage Snapshot(ulong tick)
    {
        return new ServerSnapshotMessage(CreateSnapshot(tick, new Dictionary<string, ulong>(), []));
    }

    private static WorldChunkMessage Chunk(int transferId, int index, bool final, WorldSnapshot world)
    {
        return new WorldChunkMessage(transferId, index, final, world);
    }

    private static WorldSnapshot Cubes(int width, int height, int depth, int count)
    {
        PlacedCubeSnapshot[] cubes = [.. Enumerable.Range(0, count).Select(i =>
        {
            Vector3 cell = new(i % 7, 1, i / 7);
            return new PlacedCubeSnapshot(cell, cell + new Vector3(0.5f), GeometryMaterialId.Rock, 1000 + i);
        })];
        return new WorldSnapshot(
            width,
            height,
            depth,
            [.. cubes.Select(cube => cube.CellPosition)],
            [],
            [],
            cubes,
            [],
            MutationSequence: 5);
    }

    private static byte[] Frame<TMessage>(TMessage message, ulong sequence)
        where TMessage : IServerToClientMessage
    {
        byte[] payload = Codec.Encode(new TransportEnvelope(TransportEnvelope.CurrentProtocolVersion, message.MessageKind, sequence), message);
        return new LengthPrefixedMessageFramer(1 << 20).Frame(payload);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow + Deadline;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out");
            await Task.Delay(5);
        }
    }
}
