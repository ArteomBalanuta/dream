using System.Buffers;
using System.Text;
using System.Text.Json;
using OpenTK.Mathematics;
using Xunit;
using Xunit.Abstractions;
using Dream.Client.Replication;
using Dream.Client.World;
using Dream.Multiplayer.Protocol;
using Dream.Simulation.Combat;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;

namespace Dream.Tests.Multiplayer.Protocol;

// SCALE-1: the protocol v3 compact world (WorldSnapshotJsonConverter), its chunked transfer
// (WorldChunkMessage, WorldTransferSender, WorldTransferReassembler) and the pooled export.
public class WorldTransferProtocolTests(ITestOutputHelper output)
{
    // Quantized centers and pieces are within half a 1/4096 m step of the original.
    private const float QuantizationTolerance = 0.5f / WorldSnapshotJsonConverter.QuantizationScale + 1e-5f;

    // ---- Golden: compact (v3) against the reflection world of protocol v2 -------------------

    // The server's startup world with a fracture left as first-pass rubble, rubble whose pieces
    // moved, and a cube pushed off its cell center, sent as protocol v2 encoded it and as v3
    // does: both clients end with the same cells, materials, ids and rubble, centers within the
    // quantization step, and the server's checksum.
    [Fact]
    public void CompactWorld_GivesTheClientTheSameWorldAndChecksum_AsTheV2Encoding()
    {
        using RuntimeAuthoritativeSessionHost server = RuntimeAuthoritativeSessionHost.CreateDefault();
        Map serverMap = server.Runtime.Map;
        List<Cube> raised = serverMap.SolidCubes
            .Where(cube => cube.Position.Y > 1f && serverMap.TryGetCellForCube(cube, out _))
            .OrderBy(cube => cube.Position.X).ThenBy(cube => cube.Position.Z).ThenBy(cube => cube.Position.Y)
            .ToList();
        Assert.True(raised.Count >= 3, "the startup terrain has no raised cubes to work with");
        Assert.True(serverMap.TryGetCellForCube(raised[0], out GeometryCellKey patternCell));
        Assert.True(serverMap.FractureCubeIntoPieces(patternCell));
        Assert.True(serverMap.TryGetCellForCube(raised[1], out GeometryCellKey movedRubbleCell));
        Assert.True(serverMap.FractureCubeIntoPieces(movedRubbleCell));
        IReadOnlyList<GeometryFracturePiece> pattern = serverMap.TryGetRubbleAtCell(movedRubbleCell)!.FracturePieces!;
        GeometryFracturePiece[] moved = pattern.Select((piece, i) => piece with { Center = piece.Center + new Vector3(0.0137f * i, -0.02f, 0.031f) }).ToArray();
        Assert.True(serverMap.RestoreFracturePieces(movedRubbleCell, GeometryMaterialId.Rock, moved, withPhysics: false));
        Cube shifted = raised[2];
        serverMap.UpdateCubePosition(shifted, shifted.Position + new Vector3(0.123f, -0.0371f, 0.2f), syncPhysics: false);

        JoinAcceptedMessage join = server.JoinSession("session-a", "Player A");
        Assert.NotNull(join.InitialWorld);
        WorldSnapshot world = join.InitialWorld!.Value;

        // Protocol v2: the reflection contract of WorldSnapshot.
        byte[] v2Bytes = JsonSerializer.SerializeToUtf8Bytes(join, ProtocolWireOptions.V2);
        JoinAcceptedMessage v2Join = JsonSerializer.Deserialize<JoinAcceptedMessage>(v2Bytes, ProtocolWireOptions.V2)!;
        // Protocol v3: the codec's compact world.
        TransportJsonCodec codec = new();
        byte[] v3Bytes = codec.Encode(new TransportEnvelope(TransportEnvelope.CurrentProtocolVersion, join.MessageKind, 1), join);
        JoinAcceptedMessage v3Join = Assert.IsType<JoinAcceptedMessage>(codec.Decode(v3Bytes).Message);
        output.WriteLine(
            $"{world.PlacedCubes!.Count} cubes ({world.PlacedCubes.Count(cube => cube.ActorId.HasValue)} with ids), " +
            $"{world.FracturedCubes!.Count} rubble cells: v2 {v2Bytes.Length:N0} B, v3 {v3Bytes.Length:N0} B");
        Assert.True(v3Bytes.Length * 4 < v2Bytes.Length, $"v3 {v3Bytes.Length} B is not under a quarter of v2 {v2Bytes.Length} B");

        using ClientWorldSession v2Client = CreateClient(world.Width);
        using ClientWorldSession v3Client = CreateClient(world.Width);
        ClientJoinAcceptedApplier.ApplyToRuntime(v2Client, v2Join);
        ClientJoinAcceptedApplier.ApplyToRuntime(v3Client, v3Join);

        ulong serverChecksum = WorldStateChecksum.Compute(serverMap);
        Assert.Equal(serverChecksum, WorldStateChecksum.Compute(v2Client.Map));
        Assert.Equal(serverChecksum, WorldStateChecksum.Compute(v3Client.Map));
        Assert.Equal(v2Client.LastAppliedReplicatedWorldMutationSequence, v3Client.LastAppliedReplicatedWorldMutationSequence);
        AssertSameClientWorld(v2Client.Map, v3Client.Map);
        Assert.NotNull(v3Client.Map.TryGetRubbleAtCell(patternCell));
        Assert.Equal(GeometryMaterialId.Rock, v3Client.Map.TryGetRubbleAtCell(movedRubbleCell)!.Definition.MaterialId);
    }

    // Every field the client reads, through the edge cases of the compact form: ids missing,
    // decreasing and extreme, every material, centers off the cell center, subcells, first-pass
    // rubble, moved rubble and rubble with no valid piece left.
    [Fact]
    public void CompactWorld_HandBuiltEdgeCases_RoundTripEveryClientField()
    {
        GeometryCellKey patternCell = new(9, 3, -4);
        GeometryFracturePiece[] pattern = [.. CubeFracturePattern.CreateFirstPassRubble(patternCell)];
        PlacedCubeSnapshot[] cubes =
        [
            new(new Vector3(0, 1, 0), new Vector3(0.5f, 1.5f, 0.5f), GeometryMaterialId.Grass, 1000),
            new(new Vector3(1, 1, 0), new Vector3(1.5f, 1.5f, 0.5f), GeometryMaterialId.Grass, 1001),
            new(new Vector3(-5, 7, 300), new Vector3(-4.5f, 7.5f, 300.5f), GeometryMaterialId.Stone, null),
            new(new Vector3(2, 2, 2), new Vector3(2.75f, 2.0002f, 2.25f), GeometryMaterialId.GrassPath, 17),
            new(new Vector3(3, 1, 0), new Vector3(3.5f, 1.5f, 0.5f), GeometryMaterialId.Snow, int.MaxValue),
            new(new Vector3(4, 1, 0), new Vector3(4.5f, 1.5f, 0.5f), GeometryMaterialId.Snow, int.MinValue),
            new(new Vector3(5, 1, 0), new Vector3(5.5f, 1.5f, 0.5f), GeometryMaterialId.Mud, 0),
            new(new Vector3(6, 1, 0), new Vector3(6.5f, 1.5f, 0.5f), GeometryMaterialId.Rock, null),
            new(new Vector3(7, 1, 0), new Vector3(7.0f, 1.999f, 0.001f), GeometryMaterialId.Sand, 42),
            new(new Vector3(8, 1, 0), new Vector3(8.5f, 1.5f, 0.5f), GeometryMaterialId.Dirt, 41)
        ];
        FracturedCubeSnapshot[] rubble =
        [
            new(new Vector3(patternCell.X, patternCell.Y, patternCell.Z), GeometryMaterialId.Stone, pattern),
            new(new Vector3(1, 1, 1), GeometryMaterialId.Dirt, [new GeometryFracturePiece(new Vector3(1.3f, 1.1f, 1.7f), 0.2f), new GeometryFracturePiece(new Vector3(0.9f, 0.95f, 2.1f), 1f)]),
            new(new Vector3(2, 5, 1), GeometryMaterialId.Dirt, [])
        ];
        Vector3[] subcells = [new Vector3(10, 2, 10), new Vector3(10, 3, 10), new Vector3(-1, 1, 64)];
        WorldSnapshot world = new(
            64,
            32,
            64,
            [.. cubes.Select(cube => cube.CellPosition)],
            [.. rubble.Select(cell => cell.CellPosition)],
            subcells,
            cubes,
            rubble,
            MutationSequence: ulong.MaxValue);

        WorldSnapshot decoded = RoundTrip(world, out string json);
        output.WriteLine(json);

        Assert.Equal((world.Width, world.Height, world.Depth, world.MutationSequence), (decoded.Width, decoded.Height, decoded.Depth, decoded.MutationSequence));
        Assert.Equal(subcells, decoded.AuthoredSubcellCells);
        Assert.Equal(cubes.Select(cube => cube.CellPosition), decoded.PlacedCubeCells);
        Assert.Equal(cubes.Length, decoded.PlacedCubes!.Count);
        for (int i = 0; i < cubes.Length; i++)
        {
            PlacedCubeSnapshot expected = cubes[i];
            PlacedCubeSnapshot actual = decoded.PlacedCubes[i];
            Assert.Equal(expected.CellPosition, actual.CellPosition);
            Assert.Equal(expected.MaterialId, actual.MaterialId);
            Assert.Equal(expected.ActorId, actual.ActorId);
            AssertClose(expected.CenterPosition, actual.CenterPosition);
            if (expected.CenterPosition == expected.CellPosition + new Vector3(0.5f))
            {
                // Resting at the cell center is exact, not just close.
                Assert.Equal(expected.CenterPosition, actual.CenterPosition);
            }
        }

        Assert.Equal(rubble.Length, decoded.FracturedCubes!.Count);
        Assert.Equal(rubble.Select(cell => cell.CellPosition), decoded.FracturedCubeCells);
        // First-pass rubble is regenerated exactly; so is a cell whose pieces were all invalid,
        // which the v2 client turned into the same first-pass rubble with its fallback fracture.
        Assert.Equal(pattern, decoded.FracturedCubes[0].Pieces);
        Assert.Equal(CubeFracturePattern.CreateFirstPassRubble(new GeometryCellKey(2, 5, 1)), decoded.FracturedCubes[2].Pieces);
        for (int i = 0; i < rubble.Length; i++)
        {
            Assert.Equal(rubble[i].MaterialId, decoded.FracturedCubes[i].MaterialId);
        }

        Assert.Equal(2, decoded.FracturedCubes[1].Pieces.Count);
        for (int p = 0; p < 2; p++)
        {
            AssertClose(rubble[1].Pieces[p].Center, decoded.FracturedCubes[1].Pieces[p].Center);
            Assert.InRange(decoded.FracturedCubes[1].Pieces[p].SideLength - rubble[1].Pieces[p].SideLength, -QuantizationTolerance, QuantizationTolerance);
        }

        // A decoded world encodes to the same bytes again.
        RoundTrip(decoded, out string again);
        Assert.Equal(json, again);
    }

    [Fact]
    public void CompactWorld_CostsUnderTwentyFiveBytesPerRestingCube()
    {
        const int Cubes = 10_000;
        PlacedCubeSnapshot[] cubes = new PlacedCubeSnapshot[Cubes];
        for (int i = 0; i < Cubes; i++)
        {
            Vector3 cell = new(i % 100, 1 + (i / 10_000), (i / 100) % 100);
            cubes[i] = new PlacedCubeSnapshot(cell, cell + new Vector3(0.5f), (GeometryMaterialId)(i % 3), 100_000 + i);
        }

        WorldSnapshot world = new(100, 64, 100, [.. cubes.Select(cube => cube.CellPosition)], [], [], cubes, [], 5);
        RoundTrip(world, out string json);
        double perCube = (double)Encoding.UTF8.GetByteCount(json) / Cubes;
        output.WriteLine($"{Cubes} cubes with ids, three interleaved materials: {perCube:0.0} B per cube");
        Assert.True(perCube < 25d, $"{perCube:0.0} B per cube");
    }

    [Theory]
    [InlineData("""{"w":1,"p":[0,0,0]}""")]
    [InlineData("""{"w":1,"p":[0,0,0,2]}""")]
    [InlineData("""{"w":1,"p":[0,0,0,6,1.5]}""")]
    [InlineData("""{"w":1,"p":[0,0,0,5,1,7]}""")]
    [InlineData("""{"w":1,"p":{}}""")]
    [InlineData("""{"w":1,"r":[0,0,0,-2,1]}""")]
    [InlineData("""{"w":1,"r":[0,0,0,5,1,1,1,1]}""")]
    [InlineData("""{"w":1,"s":[1,2]}""")]
    [InlineData("""{"w":1,"s":[1,2,"3"]}""")]
    [InlineData("""{"w":1,"seq":-1}""")]
    [InlineData("""{"w":1,"p":[2147483648,0,0,4,0]}""")]
    public void CompactWorld_MalformedArrays_FailWithJsonException(string json)
    {
        Assert.ThrowsAny<JsonException>(() => JsonSerializer.Deserialize<WorldSnapshot>(json, ProtocolWireOptions.V3));
    }

    // ---- Chunked transfer ---------------------------------------------------------------------

    // A 5,000-cube world written as chunks of a 4 KB budget: every chunk stays near the budget,
    // carries the whole world's header, decodes on its own, and the fragments reassemble to the
    // world a single encode gives.
    [Fact]
    public void Chunks_StayNearTheBudget_AndReassembleToTheSameWorld()
    {
        const int ChunkBytes = 4096;
        WorldSnapshot world = CreateWorld(cubes: 5000, rubble: 300, subcells: 40);
        WorldSnapshot single = RoundTrip(world, out _);
        WorldSnapshotCapture capture = WorldSnapshotCapture.FromSnapshot(world);
        WorldTransferSender sender = new(transferId: 7, capture, releaseCaptureWhenDone: true);
        TransportJsonCodec codec = new();
        List<WorldChunkMessage> chunks = [];
        while (!sender.IsComplete)
        {
            ArrayBufferWriter<byte> payload = new();
            using (Utf8JsonWriter writer = new(payload))
            {
                sender.WriteNextChunk(writer, ChunkBytes);
            }

            // One cube or rubble record and the closing brackets may pass the budget.
            Assert.True(payload.WrittenCount <= ChunkBytes + 400, $"chunk {chunks.Count} is {payload.WrittenCount} B");
            ArrayBufferWriter<byte> frame = new();
            codec.WriteEnvelope(new TransportEnvelope(TransportEnvelope.CurrentProtocolVersion, WorldChunkMessage.Kind, (ulong)chunks.Count + 1), payload.WrittenSpan, frame);
            WorldChunkMessage chunk = Assert.IsType<WorldChunkMessage>(codec.Decode(frame.WrittenSpan).Message);
            Assert.Equal(7, chunk.TransferId);
            Assert.Equal(chunks.Count, chunk.ChunkIndex);
            Assert.Equal(sender.IsComplete, chunk.IsFinal);
            Assert.Equal((world.Width, world.Height, world.Depth, world.MutationSequence), (chunk.World.Width, chunk.World.Height, chunk.World.Depth, chunk.World.MutationSequence));
            chunks.Add(chunk);
        }

        Assert.True(capture.IsReleased, "the transfer did not release its capture after the final chunk");
        Assert.True(chunks.Count > 10, $"only {chunks.Count} chunks");
        output.WriteLine($"{chunks.Count} chunks");

        JoinAcceptedMessage carrier = new("session-a", 1, 2, InitialWorld: null, InitialWorldMutationSequence: 5, WorldTransferId: 7);
        List<IServerToClientMessage> delivered = WorldTransferReassembler.Reassemble([carrier, .. chunks]);
        JoinAcceptedMessage assembled = Assert.IsType<JoinAcceptedMessage>(Assert.Single(delivered));
        Assert.Null(assembled.WorldTransferId);
        Assert.Equal(carrier with { InitialWorld = assembled.InitialWorld, WorldTransferId = null }, assembled);
        AssertSameWorld(single, assembled.InitialWorld!.Value);
    }

    // The client order of protocol v2: the carrier comes out with its world, then whatever arrived
    // between the chunks, in arrival order; nothing before the carrier is held.
    [Fact]
    public void Reassembler_HoldsEverythingBehindTheCarrier_UntilTheFinalChunk()
    {
        WorldSnapshot world = CreateWorld(cubes: 50, rubble: 2, subcells: 1);
        WorldChunkMessage[] chunks = Chunk(world, transferId: 3, chunkBytes: 200);
        Assert.True(chunks.Length >= 3);
        ServerSnapshotMessage before = new(Snapshot(1));
        ServerSnapshotMessage carrier = new(Snapshot(2) with { World = null }, WorldTransferId: 3);
        ServerSnapshotMessage between = new(Snapshot(3));
        ServerActionMessage action = new(new AuthoritativeActionResult("session-a", 1, 3, true, null));
        ServerSnapshotMessage after = new(Snapshot(4));

        WorldTransferReassembler reassembler = new();
        List<IServerToClientMessage> delivered = [];
        reassembler.Accept(before, delivered.Add);
        reassembler.Accept(carrier, delivered.Add);
        reassembler.Accept(chunks[0], delivered.Add);
        reassembler.Accept(between, delivered.Add);
        reassembler.Accept(action, delivered.Add);
        for (int i = 1; i < chunks.Length - 1; i++)
        {
            reassembler.Accept(chunks[i], delivered.Add);
        }

        Assert.Equal(new IServerToClientMessage[] { before }, delivered);
        Assert.NotNull(reassembler.Current);
        Assert.NotNull(reassembler.Accept(chunks[^1], delivered.Add));
        Assert.Null(reassembler.Current);
        reassembler.Accept(after, delivered.Add);

        Assert.Equal(5, delivered.Count);
        Assert.Same(before, delivered[0]);
        ServerSnapshotMessage resync = Assert.IsType<ServerSnapshotMessage>(delivered[1]);
        Assert.Null(resync.WorldTransferId);
        Assert.Equal(2ul, resync.Snapshot.ServerTick);
        AssertSameWorld(RoundTrip(world, out _), resync.Snapshot.World!.Value);
        Assert.Same(between, delivered[2]);
        Assert.Same(action, delivered[3]);
        Assert.Same(after, delivered[4]);
    }

    [Fact]
    public void Reassembler_RejectsChunksOutOfPlace()
    {
        WorldChunkMessage[] chunks = Chunk(CreateWorld(cubes: 40, rubble: 0, subcells: 0), transferId: 1, chunkBytes: 100);
        JoinAcceptedMessage carrier = new("session-a", 1, 2, InitialWorld: null, WorldTransferId: 1);

        // No transfer in progress.
        Assert.Throws<InvalidOperationException>(() => new WorldTransferReassembler().Accept(chunks[0], _ => { }));

        // Skipped chunk, or another transfer's chunk.
        WorldTransferReassembler skipped = new();
        skipped.Accept(carrier, _ => { });
        Assert.Throws<InvalidOperationException>(() => skipped.Accept(chunks[1], _ => { }));
        WorldTransferReassembler foreign = new();
        foreign.Accept(carrier with { WorldTransferId = 2 }, _ => { });
        Assert.Throws<InvalidOperationException>(() => foreign.Accept(chunks[0], _ => { }));

        // A second carrier before the first transfer finished.
        WorldTransferReassembler nested = new();
        nested.Accept(carrier, _ => { });
        Assert.Throws<InvalidOperationException>(() => nested.Accept(new ServerSnapshotMessage(Snapshot(1), WorldTransferId: 2), _ => { }));
    }

    // ---- Export --------------------------------------------------------------------------------

    // The tick-side export fills pooled arrays in one pass: after the pool is warm it allocates
    // only the capture object and the map's two dictionary enumerators, however large the world.
    [Fact]
    public void CaptureWorldSnapshot_OnTheTick_AllocatesAlmostNothingOnceThePoolIsWarm()
    {
        using RuntimeAuthoritativeSessionHost server = RuntimeAuthoritativeSessionHost.CreateDefault();
        WorldSessionRuntime runtime = server.Runtime;
        for (int i = 0; i < 3; i++)
        {
            runtime.CaptureWorldSnapshot().Release();
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        WorldSnapshotCapture capture = runtime.CaptureWorldSnapshot();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        output.WriteLine($"capture of {capture.CubeCount} cubes, {capture.SubcellCount} subcells, {capture.RubbleCount} rubble cells: {allocated} B");
        Assert.True(capture.CubeCount > 100);
        Assert.True(allocated <= 1024, $"{allocated} B allocated by the capture");

        // The list-based export allocated per cube; the capture's views read the arrays.
        WorldSnapshot world = capture.ToWorldSnapshot();
        Assert.Equal(capture.CubeCount, world.PlacedCubes!.Count);
        capture.Release();
        Assert.Throws<ObjectDisposedException>(() => world.PlacedCubes[0]);
    }

    // The writer thread's encode of one chunk allocates nothing per cube.
    [Fact]
    public void ChunkEncode_OfRestingCubes_AllocatesNothingPerCube()
    {
        WorldSnapshotCapture capture = WorldSnapshotCapture.FromSnapshot(CreateWorld(cubes: 20_000, rubble: 0, subcells: 0));
        ArrayBufferWriter<byte> payload = new(1 << 20);
        using Utf8JsonWriter writer = new(payload);
        new WorldTransferSender(1, capture, releaseCaptureWhenDone: false).WriteNextChunk(writer, int.MaxValue);
        writer.Flush();
        payload.ResetWrittenCount();
        writer.Reset(payload);

        WorldTransferSender sender = new(2, capture, releaseCaptureWhenDone: false);
        long before = GC.GetAllocatedBytesForCurrentThread();
        sender.WriteNextChunk(writer, int.MaxValue);
        writer.Flush();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        output.WriteLine($"20,000 cubes in {payload.WrittenCount:N0} B, {allocated} B allocated");
        Assert.True(allocated <= 512, $"{allocated} B allocated");
    }

    // ---- Helpers -------------------------------------------------------------------------------

    private static WorldSnapshot RoundTrip(WorldSnapshot world, out string json)
    {
        json = JsonSerializer.Serialize(world, ProtocolWireOptions.V3);
        return JsonSerializer.Deserialize<WorldSnapshot>(json, ProtocolWireOptions.V3);
    }

    private static WorldChunkMessage[] Chunk(WorldSnapshot world, int transferId, int chunkBytes)
    {
        WorldTransferSender sender = new(transferId, WorldSnapshotCapture.FromSnapshot(world), releaseCaptureWhenDone: true);
        List<WorldChunkMessage> chunks = [];
        while (!sender.IsComplete)
        {
            ArrayBufferWriter<byte> payload = new();
            using (Utf8JsonWriter writer = new(payload))
            {
                sender.WriteNextChunk(writer, chunkBytes);
            }

            chunks.Add(JsonSerializer.Deserialize<WorldChunkMessage>(payload.WrittenSpan, ProtocolWireOptions.V3)!);
        }

        return [.. chunks];
    }

    internal static WorldSnapshot CreateWorld(int cubes, int rubble, int subcells)
    {
        Random random = new(cubes * 31 + rubble);
        PlacedCubeSnapshot[] placed = new PlacedCubeSnapshot[cubes];
        for (int i = 0; i < cubes; i++)
        {
            Vector3 cell = new(i % 128, 1 + (i / 16_384), (i / 128) % 128);
            Vector3 center = i % 17 == 0
                ? cell + new Vector3(0.5f) + new Vector3((float)random.NextDouble() - 0.5f, -0.1f, 0.25f)
                : cell + new Vector3(0.5f);
            placed[i] = new PlacedCubeSnapshot(cell, center, (GeometryMaterialId)(i / 700 % 8), i % 11 == 0 ? null : 100_000 + i);
        }

        FracturedCubeSnapshot[] fractured = new FracturedCubeSnapshot[rubble];
        for (int i = 0; i < rubble; i++)
        {
            GeometryCellKey cell = new(i % 64, 6, i / 64);
            IReadOnlyList<GeometryFracturePiece> pieces = CubeFracturePattern.CreateFirstPassRubble(cell);
            if (i % 2 == 1)
            {
                pieces = [.. pieces.Select(piece => piece with { Center = piece.Center + new Vector3(0f, -0.07f, 0.011f) })];
            }

            fractured[i] = new FracturedCubeSnapshot(new Vector3(cell.X, cell.Y, cell.Z), (GeometryMaterialId)(i % 8), pieces);
        }

        Vector3[] subcellCells = [.. Enumerable.Range(0, subcells).Select(i => new Vector3(i, 7, 5))];
        return new WorldSnapshot(
            128,
            64,
            128,
            [.. placed.Select(cube => cube.CellPosition)],
            [.. fractured.Select(cell => cell.CellPosition)],
            subcellCells,
            placed,
            fractured,
            MutationSequence: 77);
    }

    private static ServerSnapshotEnvelope Snapshot(ulong tick)
    {
        return new ServerSnapshotEnvelope(
            tick,
            new Dictionary<string, ulong>(),
            [new ActorSnapshot(1, CreatureKind.Player, "session-a", new Vector3(tick, 1f, 2f), Vector3.UnitZ, 100, 100, true)],
            PlayerInteractionMode.Building,
            null,
            [],
            []);
    }

    private static ClientWorldSession CreateClient(int floorSize)
    {
        return new ClientWorldSession(floorSize, new Vector3(5f, 1.5f, 5f), new SceneEventHub());
    }

    private static void AssertSameWorld(WorldSnapshot expected, WorldSnapshot actual)
    {
        Assert.Equal((expected.Width, expected.Height, expected.Depth, expected.MutationSequence), (actual.Width, actual.Height, actual.Depth, actual.MutationSequence));
        Assert.Equal(expected.PlacedCubes, actual.PlacedCubes);
        Assert.Equal(expected.PlacedCubeCells, actual.PlacedCubeCells);
        Assert.Equal(expected.AuthoredSubcellCells, actual.AuthoredSubcellCells);
        Assert.Equal(expected.FracturedCubeCells, actual.FracturedCubeCells);
        Assert.Equal(expected.FracturedCubes!.Count, actual.FracturedCubes!.Count);
        for (int i = 0; i < expected.FracturedCubes.Count; i++)
        {
            Assert.Equal(expected.FracturedCubes[i].CellPosition, actual.FracturedCubes[i].CellPosition);
            Assert.Equal(expected.FracturedCubes[i].MaterialId, actual.FracturedCubes[i].MaterialId);
            Assert.Equal(expected.FracturedCubes[i].Pieces, actual.FracturedCubes[i].Pieces);
        }
    }

    // Cells, kinds, materials and replicated ids exactly; centers and rubble within the step.
    private static void AssertSameClientWorld(Map expected, Map actual)
    {
        Assert.Equal(expected.GeometryCells.Count, actual.GeometryCells.Count);
        foreach ((GeometryCellKey key, GeometryCellRecord record) in expected.GeometryCells)
        {
            Assert.True(actual.GeometryCells.TryGetValue(key, out GeometryCellRecord? other), $"cell {key} missing");
            Assert.Equal(record.Definition.MaterialId, other!.Definition.MaterialId);
            Assert.Equal(record.Definition.ShapeKind, other.Definition.ShapeKind);
            Assert.Equal(record.SubdivisionState == null, other.SubdivisionState == null);
            Cube? cube = expected.TryGetCubeAtCell(key.X, key.Y, key.Z);
            Cube? otherCube = actual.TryGetCubeAtCell(key.X, key.Y, key.Z);
            Assert.Equal(cube == null, otherCube == null);
            if (cube != null)
            {
                AssertClose(cube.Position, otherCube!.Position);
                bool hasId = expected.TryGetReplicatedId(cube, out int id);
                Assert.Equal(hasId, actual.TryGetReplicatedId(otherCube, out int otherId));
                Assert.Equal(id, otherId);
            }
        }

        Assert.Equal(expected.RubbleCells.Count, actual.RubbleCells.Count);
        foreach ((GeometryCellKey key, GeometryCellRecord record) in expected.RubbleCells)
        {
            GeometryCellRecord other = actual.TryGetRubbleAtCell(key) ?? throw new Xunit.Sdk.XunitException($"rubble {key} missing");
            Assert.Equal(record.Definition.MaterialId, other.Definition.MaterialId);
            Assert.Equal(record.FracturePieces!.Count, other.FracturePieces!.Count);
            for (int p = 0; p < record.FracturePieces.Count; p++)
            {
                AssertClose(record.FracturePieces[p].Center, other.FracturePieces[p].Center);
                Assert.InRange(other.FracturePieces[p].SideLength - record.FracturePieces[p].SideLength, -QuantizationTolerance, QuantizationTolerance);
            }
        }
    }

    private static void AssertClose(Vector3 expected, Vector3 actual)
    {
        Assert.True(
            MathF.Abs(expected.X - actual.X) <= QuantizationTolerance &&
            MathF.Abs(expected.Y - actual.Y) <= QuantizationTolerance &&
            MathF.Abs(expected.Z - actual.Z) <= QuantizationTolerance,
            $"expected {expected}, got {actual}");
    }
}
