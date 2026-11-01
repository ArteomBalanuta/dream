using System.Text;
using System.Text.Json;
using OpenTK.Mathematics;
using Xunit;
using Xunit.Abstractions;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Combat;
using Dream.Simulation.Enemies;
using Dream.Simulation.Inventory;
using Dream.Simulation.Players;
using Dream.Simulation.World;
using Dream.Tests.Utilities;

namespace Dream.Tests.Multiplayer.Protocol;

public class TransportJsonCodecDecodeTests(ITestOutputHelper output)
{
    public static TheoryData<IProtocolMessage> AllMessageKinds()
    {
        TheoryData<IProtocolMessage> data = new()
        {
            new ClientHelloMessage("client-a"),
            new ClientJoinRequestMessage("session-a", "Player A"),
            new PlayerCommandMessage(
                new PlayerCommandFrame(
                    new PlayerSessionId("session-a"),
                    42,
                    new PlayerModeSwitchIntent(PlayerInteractionMode.Combat),
                    new PlayerMoveIntent(1f, -1f, 0f, JumpPressed: true, FreezePressed: false, ToggleNoclipPressed: false),
                    new PlayerLookIntent(3f, -2f, Facing: Vector3.UnitX),
                    PrimaryUsePressed: true)),
            new PlayerActionMessage(
                new PlayerSessionId("session-a"),
                77,
                new CombatUseIntent(new Vector3(1f, 2f, 3f), -Vector3.UnitZ, 9)),
            new ClientWorldResyncRequestMessage(0xDEADBEEFUL),
            new ServerHelloMessage("server-a", TransportEnvelope.CurrentProtocolVersion, 1234),
            new JoinAcceptedMessage(
                "session-a",
                101,
                22,
                InitialWorld: null,
                Roster: [new PlayerRosterEntry("session-a", 101, "Player A")],
                Inventory: new PlayerInventorySnapshot(
                    "Player A",
                    4,
                    4,
                    [new InventorySlotSnapshot(0, new InventoryItemSnapshot(101, "tool.builder-hammer", "Builder Hammer", "held.tool.builder", 1, "Builder", ["Role: Building"]))],
                    new InventorySlotSnapshot(InventoryConstants.EquippedSlotIndex, null, true)),
                InitialActors: [CreateActor(0)],
                InitialWorldMutationSequence: 5),
            new JoinRejectedMessage("server full"),
            new ServerSnapshotMessage(CreateSnapshot(4)),
            new ServerActionMessage(
                new AuthoritativeActionResult(
                    "session-a",
                    78,
                    22,
                    Accepted: true,
                    Payload: new CombatResolutionResult(2000, CreatureKind.Enemy, 12, false, 28, 40)))
        };
        return data;
    }

    [Theory]
    [MemberData(nameof(AllMessageKinds))]
    public void Decode_RoundTripsEveryMessageKind(IProtocolMessage message)
    {
        TransportJsonCodec codec = new();
        TransportEnvelope envelope = new(TransportEnvelope.CurrentProtocolVersion, message.MessageKind, 314);
        byte[] encoded = EncodeAny(codec, envelope, message);

        DecodedProtocolMessage decoded = codec.Decode(encoded);

        Assert.Equal(envelope, decoded.Envelope);
        Assert.Equal(message.GetType(), decoded.Message.GetType());
        Assert.Equal(encoded, EncodeAny(codec, decoded.Envelope, decoded.Message));
        if (message is ClientHelloMessage or ClientJoinRequestMessage or ClientWorldResyncRequestMessage
            or ServerHelloMessage or JoinRejectedMessage or PlayerCommandMessage or PlayerActionMessage
            or ServerActionMessage)
        {
            Assert.Equal(message, decoded.Message);
        }
    }

    [Fact]
    public void Decode_FromSpanSlice_MatchesByteArrayDecode()
    {
        TransportJsonCodec codec = new();
        ServerSnapshotMessage message = new(CreateSnapshot(3));
        byte[] encoded = codec.Encode(new TransportEnvelope(TransportEnvelope.CurrentProtocolVersion, message.MessageKind, 8), message);
        byte[] framed = new byte[encoded.Length + 10];
        encoded.CopyTo(framed, 4);

        DecodedProtocolMessage decoded = codec.Decode(framed.AsSpan(4, encoded.Length));

        Assert.Equal((ulong)8, decoded.Envelope.Sequence);
        Assert.Equal(encoded, EncodeAny(codec, decoded.Envelope, decoded.Message));
    }

    [Fact]
    public void Decode_AcceptsPayloadBeforeMessageKind()
    {
        DecodedProtocolMessage decoded = DecodeJson(
            """{"payload":{"clientName":"client-a"},"protocolVersion":11,"messageKind":"client.hello","sequence":7}""");

        Assert.Equal(new TransportEnvelope(TransportEnvelope.CurrentProtocolVersion, "client.hello", 7), decoded.Envelope);
        Assert.Equal(new ClientHelloMessage("client-a"), decoded.Message);
    }

    [Fact]
    public void Decode_IgnoresUnknownTopLevelProperties()
    {
        DecodedProtocolMessage decoded = DecodeJson(
            """
            {
              "extra": {"nested": [1, 2, {"payload": 3}], "messageKind": "server.hello"},
              "protocolVersion":11,
              "payload": {"reason": "full", "unknown": true},
              "other": "value",
              "messageKind": "server.join-rejected",
              "sequence": 3,
              "trailer": [null, false]
            }
            """);

        Assert.Equal(new TransportEnvelope(TransportEnvelope.CurrentProtocolVersion, "server.join-rejected", 3), decoded.Envelope);
        Assert.Equal(new JoinRejectedMessage("full"), decoded.Message);
    }

    [Fact]
    public void Decode_MissingSequence_DefaultsToZero()
    {
        DecodedProtocolMessage decoded = DecodeJson(
            """{"protocolVersion":11,"messageKind":"client.hello","payload":{"clientName":"a"}}""");

        Assert.Equal((ulong)0, decoded.Envelope.Sequence);
    }

    [Fact]
    public void Decode_RejectsUnsupportedProtocolVersion_WithUnchangedMessage()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => DecodeJson(
            """{"protocolVersion":999,"messageKind":"client.hello","sequence":1,"payload":{"clientName":"a"}}"""));

        Assert.Equal("Unsupported protocol version 999. Supported version: 11.", ex.Message);
    }

    [Fact]
    public void Decode_MissingProtocolVersion_IsRejectedAsVersionZero()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => DecodeJson(
            """{"messageKind":"client.hello","payload":{"clientName":"a"}}"""));

        Assert.Equal("Unsupported protocol version 0. Supported version: 11.", ex.Message);
    }

    [Fact]
    public void Decode_RejectsUnknownMessageKind_WithUnchangedMessage()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => DecodeJson(
            """{"protocolVersion":11,"messageKind":"client.bogus","sequence":1,"payload":{"clientName":"a"}}"""));

        Assert.Equal("Unknown protocol message kind 'client.bogus'.", ex.Message);
    }

    [Fact]
    public void Decode_UnknownMessageKind_IsRejectedBeforePayloadIsRead()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => DecodeJson(
            """{"protocolVersion":11,"messageKind":"client.bogus","payload":[1,2,3]}"""));

        Assert.Equal("Unknown protocol message kind 'client.bogus'.", ex.Message);
    }

    [Theory]
    [InlineData("""{"protocolVersion":11,"sequence":1,"payload":{"clientName":"a"}}""")]
    [InlineData("""{"protocolVersion":11,"messageKind":null,"sequence":1,"payload":{"clientName":"a"}}""")]
    [InlineData("""{"protocolVersion":999,"sequence":1}""")]
    public void Decode_RejectsMissingMessageKind_WithUnchangedMessage(string json)
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => DecodeJson(json));

        Assert.Equal("Envelope message kind is required.", ex.Message);
    }

    [Fact]
    public void Decode_ChecksVersionBeforeMessageKindName()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => DecodeJson(
            """{"protocolVersion":999,"messageKind":"client.bogus","sequence":1}"""));

        Assert.Equal("Unsupported protocol version 999. Supported version: 11.", ex.Message);
    }

    [Fact]
    public void Decode_RejectsNullEnvelope_WithUnchangedMessage()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => DecodeJson("null"));

        Assert.Equal("Failed to deserialize transport envelope.", ex.Message);
    }

    [Fact]
    public void Decode_NullPayload_IsRejectedWithMessageTypeName()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => DecodeJson(
            """{"protocolVersion":11,"messageKind":"client.hello","sequence":1,"payload":null}"""));

        Assert.Equal("Failed to deserialize message 'ClientHelloMessage'.", ex.Message);
    }

    [Fact]
    public void Decode_MissingPayload_IsRejectedWithInvalidOperationException()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => DecodeJson(
            """{"protocolVersion":11,"messageKind":"client.hello","sequence":1}"""));

        Assert.Equal(new InvalidOperationException().Message, ex.Message);
    }

    [Theory]
    [InlineData("""[1,2]""")]
    [InlineData("""{"protocolVersion":"1","messageKind":"client.hello","payload":{"clientName":"a"}}""")]
    [InlineData("""{"protocolVersion":11,"messageKind":5,"payload":{"clientName":"a"}}""")]
    [InlineData("""{"protocolVersion":11,"messageKind":"client.hello","sequence":-1,"payload":{"clientName":"a"}}""")]
    [InlineData("""{"protocolVersion":11,"messageKind":"client.hello","payload":{"clientName":"a"}} x""")]
    [InlineData("""{"protocolVersion":11,"messageKind":"client.hello","payload":{"clientName":"a"}""")]
    public void Decode_RejectsMalformedJson_WithJsonException(string json)
    {
        Assert.ThrowsAny<JsonException>(() => DecodeJson(json));
    }

    [Fact]
    public void Decode_SnapshotWith24Actors_AllocatesLessThanSixTenthsOfLegacyDecode()
    {
        TransportJsonCodec codec = new();
        ServerSnapshotMessage message = new(CreateSnapshot(24));
        byte[] encoded = codec.Encode(new TransportEnvelope(TransportEnvelope.CurrentProtocolVersion, message.MessageKind, 900), message);
        Assert.Equal(encoded, EncodeAny(codec, LegacyDecode(encoded).Envelope, LegacyDecode(encoded).Message));

        long legacyBytes = AllocationAssert.MeasureBytesPerIteration(() => LegacyDecode(encoded));
        long budget = legacyBytes * 6 / 10;
        long currentBytes = AllocationAssert.AtMost(budget, () => codec.Decode(encoded));

        output.WriteLine($"Snapshot decode, 24 actors: legacy {legacyBytes} B, single-pass {currentBytes} B.");
    }

    private static DecodedProtocolMessage DecodeJson(string json)
    {
        return new TransportJsonCodec().Decode(Encoding.UTF8.GetBytes(json));
    }

    private static byte[] EncodeAny(TransportJsonCodec codec, TransportEnvelope envelope, IProtocolMessage message)
    {
        return message switch
        {
            ClientHelloMessage typed => codec.Encode(envelope, typed),
            ClientJoinRequestMessage typed => codec.Encode(envelope, typed),
            PlayerCommandMessage typed => codec.Encode(envelope, typed),
            PlayerActionMessage typed => codec.Encode(envelope, typed),
            ClientWorldResyncRequestMessage typed => codec.Encode(envelope, typed),
            ServerHelloMessage typed => codec.Encode(envelope, typed),
            JoinAcceptedMessage typed => codec.Encode(envelope, typed),
            JoinRejectedMessage typed => codec.Encode(envelope, typed),
            ServerSnapshotMessage typed => codec.Encode(envelope, typed),
            ServerActionMessage typed => codec.Encode(envelope, typed),
            _ => throw new InvalidOperationException($"No encoder case for {message.GetType().Name}.")
        };
    }

    private static ServerSnapshotEnvelope CreateSnapshot(int actorCount)
    {
        ActorSnapshot[] actors = new ActorSnapshot[actorCount];
        Dictionary<string, ulong> sequences = new();
        for (int i = 0; i < actorCount; i++)
        {
            actors[i] = CreateActor(i);
            if (actors[i].SessionId is string sessionId)
            {
                sequences[sessionId] = (ulong)(100 + i);
            }
        }

        return new ServerSnapshotEnvelope(
            4242,
            sequences,
            actors,
            PlayerInteractionMode.Combat,
            null,
            [new WorldMutationDelta(WorldMutationKind.CubePlaced, new Vector3(1f, 2f, 3f), Sequence: 17, ActorId: 101)],
            [new DamageEventDelta(2000, CreatureKind.Enemy, 5, false, 35, 40)],
            WorldChecksum: 0x1234UL,
            WorldChecksumSequence: 17);
    }

    private static ActorSnapshot CreateActor(int index)
    {
        bool isPlayer = index % 3 == 0;
        return new ActorSnapshot(
            isPlayer ? 100 + index : 2000 + index,
            isPlayer ? CreatureKind.Player : CreatureKind.Enemy,
            isPlayer ? $"session-{index}" : null,
            new Vector3(index * 1.25f, 1.5f, -index * 0.75f),
            -Vector3.UnitZ,
            90 - index,
            100,
            true,
            isPlayer ? PlayerInteractionMode.Combat : null,
            isPlayer ? new PlayerCombatStatsSnapshot(16, 3f, 11) : null,
            isPlayer ? $"Player {index}" : "Wolf",
            isPlayer ? new PlayerProgressionSnapshot(2, 14, 1023) : null,
            isPlayer ? null : EnemyBehaviorState.Attacking);
    }

    // The decode path TransportJsonCodec used before the single-pass reader: a WireEnvelope whose
    // JsonElement payload is cloned into a JsonDocument and then deserialized a second time.
    private static readonly JsonSerializerOptions LegacyOptions = CreateLegacyOptions();

    private static JsonSerializerOptions CreateLegacyOptions()
    {
        JsonSerializerOptions options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        options.Converters.Add(new Vector3JsonConverter());
        return options;
    }

    private static DecodedProtocolMessage LegacyDecode(byte[] payload)
    {
        LegacyWireEnvelope wireEnvelope = JsonSerializer.Deserialize<LegacyWireEnvelope>(payload, LegacyOptions)!;
        ServerSnapshotMessage message = wireEnvelope.Payload.Deserialize<ServerSnapshotMessage>(LegacyOptions)!;
        return new DecodedProtocolMessage(
            new TransportEnvelope(wireEnvelope.ProtocolVersion, wireEnvelope.MessageKind!, wireEnvelope.Sequence),
            message);
    }

    private sealed class LegacyWireEnvelope
    {
        public int ProtocolVersion { get; init; }

        public string? MessageKind { get; init; }

        public ulong Sequence { get; init; }

        public JsonElement Payload { get; init; }
    }
}
