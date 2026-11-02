using System.Buffers;
using System.Text.Json;
using OpenTK.Mathematics;
using Xunit;
using Xunit.Abstractions;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Combat;
using Dream.Simulation.Enemies;
using Dream.Simulation.Inventory;
using Dream.Simulation.Stats;
using Dream.Simulation.Players;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using Dream.Tests.Utilities;

namespace Dream.Tests.Multiplayer.Protocol;

public class TransportJsonCodecEncodeTests(ITestOutputHelper output)
{
    // Quote, HTML-sensitive characters, a non-ASCII letter (e-acute), an emoji, control characters
    // and U+2028, built from code points so the source stays ASCII.
    private static readonly string EAcute = ((char)0x00E9).ToString();
    private static readonly string EscapedName =
        "Q\"uote <b>&</b> caf" + EAcute + " " + char.ConvertFromUtf32(0x1F389) + " back\\slash\ttab\nline '" + (char)0x2028 + "'";

    public static TheoryData<IProtocolMessage> GoldenMessages()
    {
        TheoryData<IProtocolMessage> data = new();
        foreach (IProtocolMessage message in TransportJsonCodecDecodeTests.AllMessageKinds())
        {
            data.Add(message);
        }

        // Display names and free text that need escaping.
        data.Add(new ClientHelloMessage(EscapedName));
        data.Add(new ClientJoinRequestMessage("session-" + EAcute, EscapedName));
        data.Add(new JoinRejectedMessage(EscapedName));
        data.Add(new ServerHelloMessage(EscapedName, TransportEnvelope.CurrentProtocolVersion, ulong.MaxValue));
        data.Add(new JoinAcceptedMessage(
            "session-a",
            int.MinValue,
            0,
            InitialWorld: new WorldSnapshot(
                16,
                8,
                16,
                [Vector3.One],
                FracturedCubeCells: [new Vector3(4f, 0f, 4f)],
                AuthoredSubcellCells: [],
                PlacedCubes: [new PlacedCubeSnapshot(Vector3.One, new Vector3(1.5f, 1.5f, 1.5f), GeometryMaterialId.Stone, 7)],
                FracturedCubes: [new FracturedCubeSnapshot(new Vector3(4f, 0f, 4f), GeometryMaterialId.Dirt, [new GeometryFracturePiece(new Vector3(4.25f, 0.25f, 4.25f), 0.5f)])],
                MutationSequence: 99),
            Roster: [new PlayerRosterEntry("session-a", 101, EscapedName)],
            Inventory: null,
            InitialActors: [],
            InitialWorldMutationSequence: null));

        // Both polymorphic payload families, every derived type.
        PlayerSessionId session = new("session-a");
        data.Add(new PlayerActionMessage(session, 1, new BuildUseIntent(new Vector3(-1f, 0.5f, 2f))));
        data.Add(new PlayerActionMessage(session, 2, new CombatUseIntent(Vector3.Zero, Vector3.UnitY)));
        data.Add(new PlayerActionMessage(session, 3, new InventoryEquipIntent(5)));
        data.Add(new PlayerActionMessage(session, 4, new PlayerStatsUpdateIntent(new PlayerCombatStatsSnapshot(10, 2.5f, 12))));
        data.Add(new ServerActionMessage(new AuthoritativeActionResult(
            "session-a", 5, 6, true, new WorldMutationResult(WorldMutationKind.CubeDestroyed, new Vector3(3f, 0f, -3f), true))));
        data.Add(new ServerActionMessage(new AuthoritativeActionResult(
            "session-a", 6, 7, true, new InventoryChangedResult(new PlayerInventorySnapshot(
                EscapedName,
                2,
                2,
                [
                    // A kind the catalog does not know travels with its own name and visual (escaped).
                    new InventorySlotSnapshot(0, InventoryWireSamples.Unknown("tool.x", 1, EscapedName, "held." + EscapedName, stackCount: 3)),
                    new InventorySlotSnapshot(10, InventoryWireSamples.Item("gear.iron-blade", 2, ItemRarity.Rare, rolled: true, [new StatModifier(StatId.Strength, StatOp.Flat, 6f)])),
                    new InventorySlotSnapshot(200, InventoryWireSamples.Item("gear.leather-cap", 3)),
                ],
                new InventorySlotSnapshot(InventoryConstants.EquippedSlotIndex, InventoryWireSamples.Item("tool.torch", 4), true),
                [new InventorySlotSnapshot(102, InventoryWireSamples.Item("gear.leather-cap", 5, ItemRarity.Uncommon, rolled: true, [new StatModifier(StatId.Armor, StatOp.Flat, 3f)]), true)])))));
        data.Add(new ServerActionMessage(new AuthoritativeActionResult(
            "session-a", 7, 8, true, new PlayerStatsUpdateResult(new PlayerCombatStatsSnapshot(1, 0.1f, 1)))));
        data.Add(new ServerActionMessage(new AuthoritativeActionResult(
            "session-a",
            8,
            9,
            false,
            null,
            new ClientCorrectionDelta(new Vector3(0.1f, 0.2f, 0.3f), null),
            new RejectedUseDelta(EscapedName, null))));

        data.Add(new ServerSnapshotMessage(CreateEdgeSnapshot()));
        data.Add(new ServerSnapshotMessage(CreateEmptySnapshot()));
        return data;
    }

    [Theory]
    [MemberData(nameof(GoldenMessages))]
    public void Encode_IsByteIdenticalToLegacyEncode(IProtocolMessage message)
    {
        Dispatch(message, new GoldenVisitor(sequence: 314));
        Dispatch(message, new GoldenVisitor(sequence: 0));
        Dispatch(message, new GoldenVisitor(sequence: ulong.MaxValue));
    }

    [Fact]
    public void PooledFrameWriter_MatchesFramedEncode_AcrossGrowthShrinkAndReuse()
    {
        TransportJsonCodec codec = new();
        LengthPrefixedMessageFramer framer = new();
        using PooledFrameWriter writer = new(initialCapacity: 16);
        List<IProtocolMessage> messages = [.. GoldenMessages()];
        messages.Add(new ServerSnapshotMessage(CreateLargeSnapshot(400)));
        messages.AddRange([.. GoldenMessages()]);

        bool sawOversized = false;
        ulong sequence = 1;
        foreach (IProtocolMessage message in messages)
        {
            TransportEnvelope envelope = new(TransportEnvelope.CurrentProtocolVersion, message.MessageKind, sequence++);
            byte[] expected = framer.Frame(EncodeBytes(codec, envelope, message));

            writer.BeginFrame(framer);
            Dispatch(message, new PooledEncodeVisitor(codec, envelope, writer));
            ReadOnlyMemory<byte> frame = writer.CompleteFrame(framer);

            Assert.True(expected.AsSpan().SequenceEqual(frame.Span), $"Frame mismatch for {message.GetType().Name}.");
            if (frame.Length > PooledFrameWriter.RetainedCapacityLimit)
            {
                sawOversized = true;
                Assert.True(writer.Capacity > PooledFrameWriter.RetainedCapacityLimit);
            }
            else
            {
                Assert.True(writer.Capacity <= Math.Max(PooledFrameWriter.RetainedCapacityLimit, frame.Length * 2));
            }
        }

        Assert.True(sawOversized);
    }

    [Fact]
    public void PooledFrameWriter_AfterOversizedFrame_ShrinksOnNextFrame()
    {
        TransportJsonCodec codec = new();
        LengthPrefixedMessageFramer framer = new();
        using PooledFrameWriter writer = new();
        ServerSnapshotMessage large = new(CreateLargeSnapshot(400));

        writer.BeginFrame(framer);
        codec.Encode(new TransportEnvelope(TransportEnvelope.CurrentProtocolVersion, large.MessageKind, 1), large, writer);
        writer.CompleteFrame(framer);
        Assert.True(writer.Capacity > PooledFrameWriter.RetainedCapacityLimit);

        writer.BeginFrame(framer);
        Assert.True(writer.Capacity <= PooledFrameWriter.RetainedCapacityLimit);
    }

    [Fact]
    public void PooledFrameWriter_EmptyPayload_ProducesHeaderOnlyFrame()
    {
        LengthPrefixedMessageFramer framer = new();
        using PooledFrameWriter writer = new();

        writer.BeginFrame(framer);
        ReadOnlyMemory<byte> frame = writer.CompleteFrame(framer);

        Assert.Equal(framer.Frame(ReadOnlySpan<byte>.Empty), frame.ToArray());
    }

    [Fact]
    public void PooledFrameWriter_OversizedPayload_ThrowsLikeFrame()
    {
        TransportJsonCodec codec = new();
        LengthPrefixedMessageFramer framer = new(maxPayloadBytes: 128);
        using PooledFrameWriter writer = new();
        ClientHelloMessage message = new(new string('a', 256));
        TransportEnvelope envelope = new(TransportEnvelope.CurrentProtocolVersion, message.MessageKind, 1);
        byte[] encoded = codec.Encode(envelope, message);

        InvalidOperationException expected = Assert.Throws<InvalidOperationException>(() => framer.Frame(encoded));
        writer.BeginFrame(framer);
        codec.Encode(envelope, message, writer);
        InvalidOperationException actual = Assert.Throws<InvalidOperationException>(() => writer.CompleteFrame(framer));

        Assert.Equal(expected.Message, actual.Message);

        // The writer recovers: the next frame starts clean.
        ClientHelloMessage small = new("a");
        TransportEnvelope smallEnvelope = new(TransportEnvelope.CurrentProtocolVersion, small.MessageKind, 2);
        writer.BeginFrame(framer);
        codec.Encode(smallEnvelope, small, writer);
        Assert.Equal(framer.Frame(codec.Encode(smallEnvelope, small)), writer.CompleteFrame(framer).ToArray());
    }

    [Fact]
    public void PooledFrameWriter_CompleteWithoutBegin_Throws()
    {
        using PooledFrameWriter writer = new();

        Assert.Throws<InvalidOperationException>(() => writer.CompleteFrame(new LengthPrefixedMessageFramer()));
    }

    [Fact]
    public void PooledFrameWriter_AfterDispose_Throws()
    {
        PooledFrameWriter writer = new();
        writer.Dispose();
        writer.Dispose();

        Assert.Throws<ObjectDisposedException>(() => writer.BeginFrame(new LengthPrefixedMessageFramer()));
        Assert.Throws<ObjectDisposedException>(() => writer.GetSpan());
    }

    [Fact]
    public void WriteHeader_EnforcesMaximumWithFrameException()
    {
        LengthPrefixedMessageFramer framer = new(maxPayloadBytes: 8);
        byte[] header = new byte[framer.HeaderLength];

        InvalidOperationException expected = Assert.Throws<InvalidOperationException>(() => framer.Frame(new byte[9]));
        InvalidOperationException actual = Assert.Throws<InvalidOperationException>(() => framer.WriteHeader(header, 9));
        Assert.Equal(expected.Message, actual.Message);

        framer.WriteHeader(header, 8);
        Assert.Equal(framer.Frame(new byte[8]).AsSpan(0, framer.HeaderLength).ToArray(), header);
        Assert.Throws<ArgumentOutOfRangeException>(() => framer.WriteHeader(header, -1));
        Assert.Throws<ArgumentException>(() => framer.WriteHeader(new byte[framer.HeaderLength - 1], 1));
    }

    [Fact]
    public void Encode_RejectsUnsupportedVersionAndKindMismatch_WithUnchangedMessages()
    {
        TransportJsonCodec codec = new();
        ClientHelloMessage message = new("a");
        ArrayBufferWriter<byte> buffer = new();

        InvalidOperationException version = Assert.Throws<InvalidOperationException>(
            () => codec.Encode(new TransportEnvelope(1, message.MessageKind, 1), message, buffer));
        Assert.Equal("Cannot encode protocol version 1. Supported version: 19.", version.Message);

        InvalidOperationException kind = Assert.Throws<InvalidOperationException>(
            () => codec.Encode(new TransportEnvelope(TransportEnvelope.CurrentProtocolVersion, "server.hello", 1), message));
        Assert.Equal("Envelope message kind 'server.hello' does not match payload kind 'client.hello'.", kind.Message);

        InvalidOperationException envelopeVersion = Assert.Throws<InvalidOperationException>(
            () => codec.WriteEnvelope(new TransportEnvelope(0, message.MessageKind, 1), "{}"u8, buffer));
        Assert.Equal("Cannot encode protocol version 0. Supported version: 19.", envelopeVersion.Message);

        Assert.Throws<ArgumentException>(
            () => codec.WriteEnvelope(new TransportEnvelope(TransportEnvelope.CurrentProtocolVersion, message.MessageKind, 1), ReadOnlySpan<byte>.Empty, buffer));
        Assert.Equal(0, buffer.WrittenCount);
    }

    // Everything the codec and PooledFrameWriter own (envelope, framing, the cached Utf8JsonWriter
    // and the pooled buffer) allocates nothing once warm.
    [Fact]
    public void EnvelopeAndFraming_IntoWarmedPooledWriter_AllocateNothing()
    {
        TransportJsonCodec codec = new();
        LengthPrefixedMessageFramer framer = new();
        using PooledFrameWriter writer = new();
        PlayerCommandMessage message = CreateCommand();
        TransportEnvelope envelope = new(TransportEnvelope.CurrentProtocolVersion, message.MessageKind, 314);
        ArrayBufferWriter<byte> payload = new();
        codec.EncodePayload(message, payload);
        byte[] payloadJson = payload.WrittenSpan.ToArray();

        long bytes = AllocationAssert.AtMost(
            0,
            () =>
            {
                writer.BeginFrame(framer);
                codec.WriteEnvelope(envelope, payloadJson, writer);
                writer.CompleteFrame(framer);
            },
            iterations: 200,
            warmupIterations: 20);

        output.WriteLine($"Envelope + framing into a warmed PooledFrameWriter: {bytes} B.");
    }

    // The whole PlayerCommand encode. What remains is System.Text.Json's reflection serializer
    // for the payload: PlayerCommandFrame and its four nested intents are readonly record structs,
    // each boxed once per write, and the first nested object allocates the serializer's
    // WriteStack frame array (~310 B). Measured 520 B against 2608 B for the old Encode+Frame
    // path; the budget is a quarter of the legacy cost so a regression in the envelope or
    // framing shows up.
    [Fact]
    public void Encode_PlayerCommandIntoWarmedPooledWriter_AllocatesAQuarterOfLegacyOrLess()
    {
        TransportJsonCodec codec = new();
        LengthPrefixedMessageFramer framer = new();
        using PooledFrameWriter writer = new();
        PlayerCommandMessage message = CreateCommand();
        TransportEnvelope envelope = new(TransportEnvelope.CurrentProtocolVersion, message.MessageKind, 314);

        long legacyBytes = AllocationAssert.MeasureBytesPerIteration(
            () => framer.Frame(LegacyEncode(envelope, message)),
            iterations: 200,
            warmupIterations: 20);
        long pooledBytes = AllocationAssert.AtMost(
            legacyBytes / 4,
            () =>
            {
                writer.BeginFrame(framer);
                codec.Encode(envelope, message, writer);
                writer.CompleteFrame(framer);
            },
            iterations: 200,
            warmupIterations: 20);
        long payloadOnlyBytes = AllocationAssert.MeasureBytesPerIteration(
            () =>
            {
                writer.BeginFrame(framer);
                codec.EncodePayload(message, writer);
                writer.CompleteFrame(framer);
            },
            iterations: 200,
            warmupIterations: 20);

        Assert.Equal(payloadOnlyBytes, pooledBytes);
        output.WriteLine(
            $"PlayerCommand frame ({framer.Frame(LegacyEncode(envelope, message)).Length} B): " +
            $"legacy encode+frame {legacyBytes} B, pooled single-pass {pooledBytes} B " +
            $"(payload serializer alone {payloadOnlyBytes} B).");
    }

    private interface IMessageVisitor
    {
        void Visit<TMessage>(TMessage message) where TMessage : IProtocolMessage;
    }

    private static void Dispatch(IProtocolMessage message, IMessageVisitor visitor)
    {
        switch (message)
        {
            case ClientHelloMessage typed: visitor.Visit(typed); break;
            case ClientJoinRequestMessage typed: visitor.Visit(typed); break;
            case PlayerCommandMessage typed: visitor.Visit(typed); break;
            case PlayerActionMessage typed: visitor.Visit(typed); break;
            case ClientWorldResyncRequestMessage typed: visitor.Visit(typed); break;
            case ServerHelloMessage typed: visitor.Visit(typed); break;
            case JoinAcceptedMessage typed: visitor.Visit(typed); break;
            case JoinRejectedMessage typed: visitor.Visit(typed); break;
            case ServerSnapshotMessage typed: visitor.Visit(typed); break;
            case ServerActionMessage typed: visitor.Visit(typed); break;
            default: throw new InvalidOperationException($"No encoder case for {message.GetType().Name}.");
        }
    }

    private static byte[] EncodeBytes(TransportJsonCodec codec, TransportEnvelope envelope, IProtocolMessage message)
    {
        ByteEncodeVisitor visitor = new(codec, envelope);
        Dispatch(message, visitor);
        return visitor.Result!;
    }

    private sealed class GoldenVisitor(ulong sequence) : IMessageVisitor
    {
        public void Visit<TMessage>(TMessage message) where TMessage : IProtocolMessage
        {
            TransportJsonCodec codec = new();
            LengthPrefixedMessageFramer framer = new();
            TransportEnvelope envelope = new(TransportEnvelope.CurrentProtocolVersion, message.MessageKind, sequence);
            byte[] legacy = LegacyEncode(envelope, message);

            Assert.Equal(legacy, codec.Encode(envelope, message));

            ArrayBufferWriter<byte> single = new();
            codec.Encode(envelope, message, single);
            Assert.True(legacy.AsSpan().SequenceEqual(single.WrittenSpan));

            ArrayBufferWriter<byte> payload = new();
            codec.EncodePayload(message, payload);
            ArrayBufferWriter<byte> wrapped = new();
            codec.WriteEnvelope(envelope, payload.WrittenSpan, wrapped);
            Assert.True(legacy.AsSpan().SequenceEqual(wrapped.WrittenSpan));

            using PooledFrameWriter pooled = new();
            pooled.BeginFrame(framer);
            codec.Encode(envelope, message, pooled);
            Assert.True(framer.Frame(legacy).AsSpan().SequenceEqual(pooled.CompleteFrame(framer).Span));

            pooled.BeginFrame(framer);
            pooled.Write(payload.WrittenSpan);
            ArrayBufferWriter<byte> payloadOnly = new();
            payloadOnly.Write(pooled.CompleteFrame(framer).Span[framer.HeaderLength..]);
            Assert.True(payload.WrittenSpan.SequenceEqual(payloadOnly.WrittenSpan));
        }
    }

    private sealed class PooledEncodeVisitor(TransportJsonCodec codec, TransportEnvelope envelope, PooledFrameWriter writer)
        : IMessageVisitor
    {
        public void Visit<TMessage>(TMessage message) where TMessage : IProtocolMessage
        {
            codec.Encode(envelope, message, writer);
        }
    }

    private sealed class ByteEncodeVisitor(TransportJsonCodec codec, TransportEnvelope envelope) : IMessageVisitor
    {
        public byte[]? Result { get; private set; }

        public void Visit<TMessage>(TMessage message) where TMessage : IProtocolMessage
        {
            Result = codec.Encode(envelope, message);
        }
    }

    private static PlayerCommandMessage CreateCommand()
    {
        return new PlayerCommandMessage(
            new PlayerCommandFrame(
                new PlayerSessionId("session-a"),
                42,
                new PlayerModeSwitchIntent(PlayerInteractionMode.Combat),
                new PlayerMoveIntent(1f, -1f, 0f, JumpPressed: true, FreezePressed: false, ToggleNoclipPressed: false),
                new PlayerLookIntent(3f, -2f, Facing: Vector3.UnitX),
                PrimaryUsePressed: true));
    }

    private static ServerSnapshotEnvelope CreateEdgeSnapshot()
    {
        Vector3[] edges =
        [
            new Vector3(float.MaxValue, float.MinValue, float.Epsilon),
            new Vector3(-0f, 0f, -float.Epsilon),
            new Vector3(0.1f, 1f / 3f, 1e-7f),
            new Vector3(16777217f, -123456.789f, 3.4e38f)
        ];

        return new ServerSnapshotEnvelope(
            ulong.MaxValue,
            new Dictionary<string, ulong> { ["session-a"] = 1, [EscapedName] = ulong.MaxValue },
            [
                new ActorSnapshot(
                    1,
                    CreatureKind.Player,
                    EscapedName,
                    edges[0],
                    edges[1],
                    1,
                    1,
                    true,
                    PlayerInteractionMode.Building,
                    new PlayerCombatStatsSnapshot(1, float.Epsilon, 1),
                    EscapedName,
                    new PlayerProgressionSnapshot(1, 0, 0),
                    null)
            ],
            PlayerInteractionMode.Building,
            null,
            [
                new WorldMutationDelta(WorldMutationKind.CubePlaced, edges[0], MaterialId: GeometryMaterialId.Sand, Sequence: 1, ActorId: 1),
                new WorldMutationDelta(WorldMutationKind.CubeDestroyed, edges[1], Sequence: 2),
                WorldMutationDelta.Moved(edges[2], edges[3]) with { Sequence = 3 },
                WorldMutationDelta.Fractured(
                    Vector3.One,
                    GeometryMaterialId.Stone,
                    [new GeometryFracturePiece(edges[2], 0.05f), new GeometryFracturePiece(Vector3.One, 1f)]) with { Sequence = ulong.MaxValue }
            ],
            [new DamageEventDelta(1, CreatureKind.Player, 0, true, 0, 1)],
            WorldChecksum: ulong.MaxValue,
            WorldChecksumSequence: ulong.MaxValue);
    }

    private static ServerSnapshotEnvelope CreateEmptySnapshot()
    {
        return new ServerSnapshotEnvelope(
            0,
            new Dictionary<string, ulong>(),
            [],
            PlayerInteractionMode.Combat,
            null,
            [],
            []);
    }

    private static ServerSnapshotEnvelope CreateLargeSnapshot(int actorCount)
    {
        ActorSnapshot[] actors = new ActorSnapshot[actorCount];
        for (int i = 0; i < actorCount; i++)
        {
            actors[i] = new ActorSnapshot(
                2000 + i,
                CreatureKind.Enemy,
                null,
                new Vector3(i * 1.25f, 1.5f, -i * 0.75f),
                -Vector3.UnitZ,
                90,
                100,
                true,
                null,
                null,
                $"Wolf {i}",
                null,
                EnemyBehaviorState.Attacking);
        }

        return new ServerSnapshotEnvelope(
            7,
            new Dictionary<string, ulong>(),
            actors,
            PlayerInteractionMode.Combat,
            null,
            [],
            []);
    }

    // The encode path TransportJsonCodec used before the single-pass writer: the payload is
    // serialized to a JsonElement, wrapped in a WireEnvelope and serialized a second time. It uses
    // independently built protocol v3 options (v2 plus the compact world), so it is a
    // byte-identity reference for the codec.
    private static readonly JsonSerializerOptions LegacyOptions = ProtocolWireOptions.V3;

    private static byte[] LegacyEncode<TMessage>(TransportEnvelope envelope, TMessage message)
        where TMessage : IProtocolMessage
    {
        LegacyWireEnvelope wireEnvelope = new()
        {
            ProtocolVersion = envelope.ProtocolVersion,
            MessageKind = envelope.MessageKind,
            Sequence = envelope.Sequence,
            Payload = JsonSerializer.SerializeToElement(message, LegacyOptions)
        };

        return JsonSerializer.SerializeToUtf8Bytes(wireEnvelope, LegacyOptions);
    }

    private sealed class LegacyWireEnvelope
    {
        public int ProtocolVersion { get; init; }

        public string? MessageKind { get; init; }

        public ulong Sequence { get; init; }

        public JsonElement Payload { get; init; }
    }
}
