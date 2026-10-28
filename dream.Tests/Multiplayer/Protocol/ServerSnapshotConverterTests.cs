using System.Buffers;
using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenTK.Mathematics;
using Xunit;
using Xunit.Abstractions;
using Dream.Multiplayer.Protocol;
using Dream.Simulation.Combat;
using Dream.Simulation.Players;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using Dream.Tests.Utilities;
using static Dream.Tests.Multiplayer.Protocol.ServerSnapshotSamples;

namespace Dream.Tests.Multiplayer.Protocol;

// WIRE-11: TransportJsonCodec writes ServerSnapshotMessage, its envelope, the world mutations
// (with their fracture pieces) and the damage events with hand-written converters.
// ProtocolWireOptions.V2 has none of them, so it stays the reflection reference: every encode must
// match it byte for byte, and every decode, however the payload is reordered, padded or trimmed,
// must produce what the reference produces.
public class ServerSnapshotConverterTests(ITestOutputHelper output)
{
    private const int RandomCases = 300;

    private static readonly JsonSerializerOptions Reference = ProtocolWireOptions.V2;

    // ---- Byte identity -------------------------------------------------------------------

    [Fact]
    public void RandomSnapshots_AreByteIdenticalToReference()
    {
        Random random = new(11_2026);
        for (int i = 0; i < RandomCases; i++)
        {
            AssertByteIdentical(new ServerSnapshotMessage(RandomSnapshot(random)));
        }
    }

    [Fact]
    public void EveryMutationKindWithEveryOptionalPresentAndAbsent_IsByteIdenticalToReference()
    {
        Random random = new(64);
        List<WorldMutationDelta> mutations = [];
        foreach (WorldMutationKind kind in new[]
                 {
                     WorldMutationKind.CubePlaced, WorldMutationKind.CubeDestroyed, WorldMutationKind.CubeFractured,
                     WorldMutationKind.CubeMoved, (WorldMutationKind)(-7), (WorldMutationKind)int.MaxValue
                 })
        {
            for (int mask = 0; mask < 64; mask++)
            {
                mutations.Add(RandomMutation(random, kind, mask));
            }
        }

        mutations.Add(default);
        mutations.Add(WorldMutationDelta.Moved(new Vector3(1f, 2f, 3f), new Vector3(4.5f, -0.25f, 1e-7f)));
        mutations.Add(WorldMutationDelta.Fractured(Vector3.One, GeometryMaterialId.Rock, RandomPieces(random, 5)));
        mutations.Add(new WorldMutationDelta(WorldMutationKind.CubeFractured, Vector3.Zero, FracturePieces: []));
        mutations.Add(new WorldMutationDelta(WorldMutationKind.CubeFractured, Vector3.Zero, FracturePieces: new List<GeometryFracturePiece> { default }));

        AssertByteIdentical(new ServerSnapshotMessage(SnapshotWith(mutations: mutations)));
        foreach (WorldMutationDelta mutation in mutations)
        {
            AssertByteIdentical(new ServerSnapshotMessage(SnapshotWith(mutations: [mutation])));
        }
    }

    [Fact]
    public void DamageEvents_ChecksumAndWorld_PresentAndAbsent_AreByteIdenticalToReference()
    {
        Random random = new(5);
        DamageEventDelta[] damage =
        [
            default,
            new DamageEventDelta(int.MinValue, (CreatureKind)(-1), int.MaxValue, true, int.MinValue, int.MaxValue),
            new DamageEventDelta(2000, CreatureKind.Enemy, 5, false, 35, 40),
            .. RandomDamageEvents(random, 20)
        ];

        foreach (ulong? checksum in new ulong?[] { null, 0, 1, ulong.MaxValue })
        {
            foreach (WorldSnapshot? world in new WorldSnapshot?[]
                     {
                         null,
                         default(WorldSnapshot),
                         new WorldSnapshot(1, 2, 3, []),
                         RandomWorld(random),
                         RandomWorld(random)
                     })
            {
                foreach (IReadOnlyList<DamageEventDelta>? events in new IReadOnlyList<DamageEventDelta>?[] { null, [], damage })
                {
                    AssertByteIdentical(new ServerSnapshotMessage(new ServerSnapshotEnvelope(
                        ulong.MaxValue, new Dictionary<string, ulong>(), [], PlayerInteractionMode.Building,
                        world, [], events!, checksum, checksum ?? ulong.MaxValue)));
                }
            }
        }
    }

    [Fact]
    public void EmptyNullAndLargeCollections_AreByteIdenticalToReference()
    {
        Random random = new(9);
        AssertByteIdentical(new ServerSnapshotMessage(default));
        AssertByteIdentical(new ServerSnapshotMessage(new ServerSnapshotEnvelope(0, new Dictionary<string, ulong>(), [], default, null, [], [])));
        AssertByteIdentical(new ServerSnapshotMessage(SnapshotWith(actors: RandomActors(random, 600))));
        AssertByteIdentical(new ServerSnapshotMessage(SnapshotWith(mutations: RandomMutations(random, 400))));
        AssertByteIdentical(new ServerSnapshotMessage(SnapshotWith(damage: RandomDamageEvents(random, 400))));

        Dictionary<string, ulong> many = new();
        for (int i = 0; i < 500; i++)
        {
            many[$"session-{i}"] = (ulong)i * 977;
        }

        AssertByteIdentical(new ServerSnapshotMessage(SnapshotWith(sequences: many)));
    }

    // The reflection serializer writes an IReadOnlyDictionary in its enumeration order. For a
    // Dictionary that is insertion order only until a key is removed: the next add reuses the
    // freed slot. The converter must follow the dictionary, not the insertion history.
    [Fact]
    public void SequenceDictionary_KeepsTheDictionaryEnumerationOrder()
    {
        Dictionary<string, ulong> sequences = new(StringComparer.Ordinal)
        {
            ["session-c"] = 3,
            ["session-a"] = 1,
            ["session-b"] = 2
        };
        sequences.Remove("session-a");
        sequences["session-z"] = 26;
        sequences[EdgeStrings[3]] = 4;
        Assert.Equal(["session-c", "session-z", "session-b", EdgeStrings[3]], sequences.Keys.ToArray());

        ServerSnapshotMessage message = new(SnapshotWith(sequences: sequences));
        AssertByteIdentical(message);

        System.Buffers.ArrayBufferWriter<byte> payload = new();
        new TransportJsonCodec().EncodePayload(message, payload);
        using JsonDocument document = JsonDocument.Parse(payload.WrittenMemory);
        string[] wireKeys = document.RootElement.GetProperty("snapshot").GetProperty("lastAcceptedCommandSequences")
            .EnumerateObject().Select(p => p.Name).ToArray();
        Assert.Equal(sequences.Keys.ToArray(), wireKeys);

        // Plain insertion order for a dictionary that never had a removal.
        Dictionary<string, ulong> inserted = new() { ["z"] = 1, ["a"] = 2, ["m"] = 3 };
        new TransportJsonCodec().EncodePayload(new ServerSnapshotMessage(SnapshotWith(sequences: inserted)), payload = new());
        Assert.Contains("\"lastAcceptedCommandSequences\":{\"z\":1,\"a\":2,\"m\":3}", Encoding.UTF8.GetString(payload.WrittenSpan));
    }

    // Other collection implementations go through the interfaces and still match.
    [Fact]
    public void OtherCollectionImplementations_AreByteIdenticalToReference()
    {
        Random random = new(31);
        AssertByteIdentical(new ServerSnapshotMessage(new ServerSnapshotEnvelope(
            7,
            new SortedDictionary<string, ulong>(StringComparer.Ordinal) { ["b"] = 2, ["a"] = 1, [EdgeStrings[5]] = 0 },
            new List<ActorSnapshot>(RandomActors(random, 3)),
            PlayerInteractionMode.Combat,
            null,
            new ReadOnlyCollection<WorldMutationDelta>(RandomMutations(random, 5)),
            new List<DamageEventDelta>(RandomDamageEvents(random, 3)),
            9,
            10)));
        AssertByteIdentical(new ServerSnapshotMessage(new ServerSnapshotEnvelope(
            7,
            new ReadOnlyDictionary<string, ulong>(new Dictionary<string, ulong> { ["q"] = 5, ["p"] = 6 }),
            RandomActors(random, 2),
            PlayerInteractionMode.Building,
            null,
            RandomMutations(random, 2).ToList(),
            new ReadOnlyCollection<DamageEventDelta>(RandomDamageEvents(random, 2)))));
    }

    [Fact]
    public void NonFiniteFloats_FailLikeReference()
    {
        foreach (float bad in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
        {
            AssertSameFailure(new ServerSnapshotMessage(SnapshotWith(mutations: [new WorldMutationDelta(WorldMutationKind.CubePlaced, new Vector3(0f, bad, 0f))])));
            AssertSameFailure(new ServerSnapshotMessage(SnapshotWith(mutations: [WorldMutationDelta.Moved(Vector3.Zero, new Vector3(bad, 0f, 0f))])));
            AssertSameFailure(new ServerSnapshotMessage(SnapshotWith(mutations:
                [new WorldMutationDelta(WorldMutationKind.CubeFractured, Vector3.Zero, FracturePieces: [new GeometryFracturePiece(Vector3.Zero, bad)])])));
            AssertSameFailure(new ServerSnapshotMessage(SnapshotWith(mutations:
                [new WorldMutationDelta(WorldMutationKind.CubeFractured, Vector3.Zero, FracturePieces: [new GeometryFracturePiece(new Vector3(0f, 0f, bad), 1f)])])));
        }
    }

    // ---- Round trip ----------------------------------------------------------------------

    [Fact]
    public void RandomSnapshots_RoundTrip()
    {
        TransportJsonCodec codec = new();
        Random random = new(777);
        for (int i = 0; i < RandomCases; i++)
        {
            ServerSnapshotMessage message = new(RandomSnapshot(random, roundTrippable: true));
            DecodedProtocolMessage decoded = codec.Decode(codec.Encode(Envelope(message, (ulong)i), message));
            ServerSnapshotEnvelope expected = message.Snapshot;
            ServerSnapshotEnvelope actual = Assert.IsType<ServerSnapshotMessage>(decoded.Message).Snapshot;

            Assert.Equal((ulong)i, decoded.Envelope.Sequence);
            Assert.Equal(expected.ServerTick, actual.ServerTick);
            Assert.Equal(expected.InteractionMode, actual.InteractionMode);
            Assert.Equal(expected.WorldChecksum, actual.WorldChecksum);
            Assert.Equal(expected.WorldChecksumSequence, actual.WorldChecksumSequence);
            Assert.Equal(expected.LastAcceptedCommandSequences?.ToArray(), actual.LastAcceptedCommandSequences?.ToArray());
            Assert.Equal(expected.Actors, actual.Actors);
            Assert.Equal(expected.DamageEvents, actual.DamageEvents);
            AssertMutationsEqual(expected.WorldMutations, actual.WorldMutations);
            Assert.Equal(expected.World.HasValue, actual.World.HasValue);

            // The decoded snapshot re-encodes, through the reference, to the same bytes.
            Assert.Equal(
                Encoding.UTF8.GetString(JsonSerializer.SerializeToUtf8Bytes(message, Reference)),
                Encoding.UTF8.GetString(JsonSerializer.SerializeToUtf8Bytes((ServerSnapshotMessage)decoded.Message, Reference)));
        }
    }

    [Fact]
    public void Decode_MatchesReferenceDecodeOfTheSameBytes()
    {
        TransportJsonCodec codec = new();
        Random random = new(1234);
        for (int i = 0; i < RandomCases; i++)
        {
            ServerSnapshotMessage message = new(RandomSnapshot(random));
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(message, Reference);
            AssertDecodesLikeReference(codec, Encoding.UTF8.GetString(payload));
        }
    }

    // ---- Tolerant decode -----------------------------------------------------------------

    // Shuffled property order, unknown properties of every JSON shape, and randomly dropped
    // properties (optional or not) at every level, including inside mutations, fracture pieces,
    // damage events and the world: the codec decodes to exactly what the reflection reference does.
    [Fact]
    public void MangledPayloads_DecodeLikeReference()
    {
        TransportJsonCodec codec = new();
        Random random = new(4711);
        for (int i = 0; i < RandomCases; i++)
        {
            ServerSnapshotMessage message = new(RandomSnapshot(random, actorCount: random.Next(0, 6)));
            JsonNode payload = JsonSerializer.SerializeToNode(message, Reference)!;
            AssertDecodesLikeReference(codec, Mangle(payload, random, inSequences: false).ToJsonString());
        }
    }

    [Fact]
    public void HandWrittenPayloads_ReorderedExtraMissingAndDuplicated_Decode()
    {
        TransportJsonCodec codec = new();
        string[] payloads =
        [
            "{}",
            """{"snapshot":{}}""",
            """{"other":1,"snapshot":{"serverTick":3},"snapshot":{"serverTick":4}}""",
            """
            {"snapshot":{"damageEvents":[{"maxHealth":40,"junk":{"a":[1,2]},"actorId":7,"kind":1,"actorDied":true},{}],
             "worldChecksumSequence":9,"lastAcceptedCommandSequences":{"b":2,"a":1,"b":5},
             "worldMutations":[{"sequence":3,"fracturePieces":[{"sideLength":0.5,"x":[]},{"center":{"z":3,"y":2,"x":1}}],
                                "kind":2,"cellPosition":{"x":1,"y":2,"z":3,"w":4},"materialId":6,"actorId":null,"centerPosition":null},
                               {"kind":3,"targetCellPosition":{"x":0,"y":0,"z":0}}],
             "actors":[],"worldChecksum":null,"world":null,"interactionMode":1,"serverTick":12,"tail":"x"}}
            """,
            """{"snapshot":{"lastAcceptedCommandSequences":null,"actors":null,"worldMutations":null,"damageEvents":null,"world":{"width":2,"placedCubeCells":[]}}}""",
            """{"snapshot":{"worldMutations":[{"fracturePieces":null,"materialId":null,"targetCellPosition":null}]}}""",
            """{"snapshot":{"lastAcceptedCommandSequences":{"café\"":1,"":0}}}"""
        ];

        foreach (string payload in payloads)
        {
            AssertDecodesLikeReference(codec, payload);
        }

        ServerSnapshotEnvelope snapshot = Assert.IsType<ServerSnapshotMessage>(DecodePayload(codec, payloads[3])).Snapshot;
        Assert.Equal(12UL, snapshot.ServerTick);
        Assert.Equal([new KeyValuePair<string, ulong>("b", 5), new KeyValuePair<string, ulong>("a", 1)], snapshot.LastAcceptedCommandSequences.ToArray());
        Assert.Equal(
            [new DamageEventDelta(7, CreatureKind.Enemy, 0, true, 0, 40), default],
            snapshot.DamageEvents);
        Assert.Equal(2, snapshot.WorldMutations.Count);
        Assert.Equal(WorldMutationKind.CubeFractured, snapshot.WorldMutations[0].Kind);
        Assert.Equal(new Vector3(1f, 2f, 3f), snapshot.WorldMutations[0].CellPosition);
        Assert.Equal(GeometryMaterialId.Rock, snapshot.WorldMutations[0].MaterialId);
        Assert.Equal(
            [new GeometryFracturePiece(Vector3.Zero, 0.5f), new GeometryFracturePiece(new Vector3(1f, 2f, 3f), 0f)],
            snapshot.WorldMutations[0].FracturePieces!);
        Assert.Equal(new WorldMutationDelta(WorldMutationKind.CubeMoved, Vector3.Zero, TargetCellPosition: Vector3.Zero), snapshot.WorldMutations[1]);
        Assert.Equal(PlayerInteractionMode.Combat, snapshot.InteractionMode);
        Assert.Null(snapshot.WorldChecksum);
        Assert.Equal(9UL, snapshot.WorldChecksumSequence);

        ServerSnapshotEnvelope empty = Assert.IsType<ServerSnapshotMessage>(DecodePayload(codec, "{}")).Snapshot;
        Assert.Equal(default, empty);
    }

    // W9-2: an actor's starved ticks go on the wire only when non-zero, so a snapshot without
    // starvation keeps its pre-W9-2 bytes; when present they round-trip, match the reference, and
    // sit last in the actor, where a reader that predates them skips them as an unknown property.
    [Fact]
    public void StarvedTicks_AreLeftOutWhenZero_AndRoundTripOtherwise()
    {
        TransportJsonCodec codec = new();
        ActorSnapshot walking = new(7, CreatureKind.Player, "session-a", new Vector3(1f, 2f, 3f), Vector3.UnitZ, 90, 100, true);
        ServerSnapshotMessage unstarved = new(SnapshotWith(actors: [walking]));
        string unstarvedJson = Encoding.UTF8.GetString(codec.Encode(Envelope(unstarved, 1), unstarved));
        Assert.DoesNotContain("starvedTicks", unstarvedJson, StringComparison.Ordinal);
        AssertByteIdentical(unstarved);

        ServerSnapshotMessage starved = new(SnapshotWith(actors: [walking with { StarvedTicks = 3 }]));
        AssertByteIdentical(starved);
        string starvedJson = Encoding.UTF8.GetString(codec.Encode(Envelope(starved, 1), starved));
        Assert.Contains("\"isAlive\":true,\"starvedTicks\":3}", starvedJson, StringComparison.Ordinal);
        Assert.Equal(unstarvedJson.Length + ",\"starvedTicks\":3".Length, starvedJson.Length);
        ActorSnapshot decoded = Assert.Single(Assert.IsType<ServerSnapshotMessage>(codec.Decode(Encoding.UTF8.GetBytes(starvedJson)).Message).Snapshot.Actors);
        Assert.Equal(walking with { StarvedTicks = 3 }, decoded);

        ActorSnapshot reordered = Assert.Single(Assert.IsType<ServerSnapshotMessage>(DecodePayload(
            codec,
            """{"snapshot":{"actors":[{"starvedTicks":5,"future":{"x":[1]},"actorId":7,"kind":0}]}}""")).Snapshot.Actors);
        Assert.Equal(5, reordered.StarvedTicks);
        Assert.Equal(7, reordered.ActorId);
    }

    // The envelope decode returns the shared literal for a known message kind; an escaped
    // spelling of the same kind still matches it.
    [Fact]
    public void Decode_KnownMessageKind_IsTheSharedLiteral_EvenWhenEscaped()
    {
        TransportJsonCodec codec = new();
        ServerSnapshotMessage message = new(TwentyFourActorSnapshot());
        DecodedProtocolMessage decoded = codec.Decode(codec.Encode(Envelope(message, 3), message));
        Assert.Same(message.MessageKind, decoded.Envelope.MessageKind);

        HashSet<string> kinds = [];
        foreach (IProtocolMessage any in TransportJsonCodecDecodeTests.AllMessageKinds())
        {
            string json = "{\"protocolVersion\":" + TransportEnvelope.CurrentProtocolVersion
                + ",\"messageKind\":\"" + any.MessageKind + "\",\"sequence\":1,\"payload\":"
                + JsonSerializer.Serialize(any, any.GetType(), Reference) + "}";
            Assert.Same(any.MessageKind, codec.Decode(Encoding.UTF8.GetBytes(json)).Envelope.MessageKind);
            kinds.Add(any.MessageKind);
        }

        Assert.Equal(10, kinds.Count);

        string escaped = "{\"protocolVersion\":" + TransportEnvelope.CurrentProtocolVersion
            + ",\"messageKind\":\"server\\u002esnapshot\",\"sequence\":1,\"payload\":{}}";
        DecodedProtocolMessage fromEscaped = codec.Decode(Encoding.UTF8.GetBytes(escaped));
        Assert.Equal("server.snapshot", fromEscaped.Envelope.MessageKind);
        Assert.IsType<ServerSnapshotMessage>(fromEscaped.Message);
    }

    [Theory]
    [InlineData("""{"snapshot":null}""")]
    [InlineData("""{"snapshot":[]}""")]
    [InlineData("""{"snapshot":3}""")]
    [InlineData("""{"snapshot":{"serverTick":-1}}""")]
    [InlineData("""{"snapshot":{"serverTick":"1"}}""")]
    [InlineData("""{"snapshot":{"serverTick":1.5}}""")]
    [InlineData("""{"snapshot":{"interactionMode":"Combat"}}""")]
    [InlineData("""{"snapshot":{"interactionMode":null}}""")]
    [InlineData("""{"snapshot":{"worldChecksum":"5"}}""")]
    [InlineData("""{"snapshot":{"worldChecksumSequence":null}}""")]
    [InlineData("""{"snapshot":{"lastAcceptedCommandSequences":[]}}""")]
    [InlineData("""{"snapshot":{"lastAcceptedCommandSequences":{"a":null}}}""")]
    [InlineData("""{"snapshot":{"lastAcceptedCommandSequences":{"a":-1}}}""")]
    [InlineData("""{"snapshot":{"lastAcceptedCommandSequences":{"a":"1"}}}""")]
    [InlineData("""{"snapshot":{"lastAcceptedCommandSequences":{"a":{}}}}""")]
    [InlineData("""{"snapshot":{"actors":{}}}""")]
    [InlineData("""{"snapshot":{"world":[]}}""")]
    [InlineData("""{"snapshot":{"world":{"width":"1"}}}""")]
    [InlineData("""{"snapshot":{"worldMutations":{}}}""")]
    [InlineData("""{"snapshot":{"worldMutations":[null]}}""")]
    [InlineData("""{"snapshot":{"worldMutations":[3]}}""")]
    [InlineData("""{"snapshot":{"worldMutations":[{"kind":"CubeMoved"}]}}""")]
    [InlineData("""{"snapshot":{"worldMutations":[{"kind":null}]}}""")]
    [InlineData("""{"snapshot":{"worldMutations":[{"cellPosition":null}]}}""")]
    [InlineData("""{"snapshot":{"worldMutations":[{"cellPosition":{"x":1}}]}}""")]
    [InlineData("""{"snapshot":{"worldMutations":[{"targetCellPosition":5}]}}""")]
    [InlineData("""{"snapshot":{"worldMutations":[{"materialId":"Stone"}]}}""")]
    [InlineData("""{"snapshot":{"worldMutations":[{"sequence":-3}]}}""")]
    [InlineData("""{"snapshot":{"worldMutations":[{"actorId":2147483648}]}}""")]
    [InlineData("""{"snapshot":{"worldMutations":[{"fracturePieces":{}}]}}""")]
    [InlineData("""{"snapshot":{"worldMutations":[{"fracturePieces":[null]}]}}""")]
    [InlineData("""{"snapshot":{"worldMutations":[{"fracturePieces":[{"sideLength":"1"}]}]}}""")]
    [InlineData("""{"snapshot":{"worldMutations":[{"fracturePieces":[{"center":null}]}]}}""")]
    [InlineData("""{"snapshot":{"damageEvents":{}}}""")]
    [InlineData("""{"snapshot":{"damageEvents":[null]}}""")]
    [InlineData("""{"snapshot":{"damageEvents":[{"actorDied":1}]}}""")]
    [InlineData("""{"snapshot":{"damageEvents":[{"amount":1.5}]}}""")]
    [InlineData("""{"snapshot":{"damageEvents":[{"kind":"Enemy"}]}}""")]
    public void InvalidPayloads_FailWithJsonExceptionLikeReference(string payload)
    {
        Assert.ThrowsAny<JsonException>(() => JsonSerializer.Deserialize<ServerSnapshotMessage>(payload, Reference));
        Assert.ThrowsAny<JsonException>(() => DecodePayload(new TransportJsonCodec(), payload));
    }

    // ---- Allocations ---------------------------------------------------------------------

    // Before WIRE-11 (measured on 1b7bdae with this test): 824 B, all inside the reflection
    // serializer for ServerSnapshotEnvelope: the boxed envelope, the WriteStack frame array, and
    // the dictionary, mutation and damage-event enumerators.
    [Fact]
    public void Encode_SnapshotWith24ActorsIntoWarmedPooledWriter_AllocatesNothing()
    {
        TransportJsonCodec codec = new();
        LengthPrefixedMessageFramer framer = new();
        using PooledFrameWriter writer = new();
        ServerSnapshotMessage message = new(TwentyFourActorSnapshot());
        TransportEnvelope envelope = Envelope(message, 900);

        long bytes = AllocationAssert.AtMost(
            SnapshotEncodeBudgetBytes,
            () =>
            {
                writer.BeginFrame(framer);
                codec.Encode(envelope, message, writer);
                writer.CompleteFrame(framer);
            },
            iterations: 50,
            warmupIterations: 20);

        output.WriteLine($"24-actor snapshot encode into a warmed PooledFrameWriter: {bytes} B.");
    }

    // The WIRE-5 server path: the payload alone is encoded once per tick into the writer thread's
    // pooled scratch, then copied into the shared array.
    [Fact]
    public void EncodePayload_SnapshotWithMovesFracturesAndDamage_AllocatesNothing()
    {
        Random random = new(3);
        TransportJsonCodec codec = new();
        using PooledFrameWriter writer = new();
        LengthPrefixedMessageFramer framer = new();
        ServerSnapshotMessage message = new(TwentyFourActorSnapshot() with
        {
            WorldMutations =
            [
                WorldMutationDelta.Moved(new Vector3(1f, 2f, 3f), new Vector3(1.5f, 2.25f, 3.75f)),
                WorldMutationDelta.Moved(new Vector3(4f, 2f, 3f), new Vector3(4.5f, 2.25f, 3.75f)),
                WorldMutationDelta.Fractured(new Vector3(5f, 1f, 5f), GeometryMaterialId.Stone, RandomPieces(random, 8)),
                new WorldMutationDelta(WorldMutationKind.CubeDestroyed, new Vector3(6f, 1f, 6f), Sequence: 18, ActorId: 101)
            ],
            DamageEvents = RandomDamageEvents(random, 4)
        });

        long bytes = AllocationAssert.AtMost(
            SnapshotEncodeBudgetBytes,
            () =>
            {
                writer.BeginFrame(framer);
                codec.EncodePayload(message, writer);
                writer.CompleteFrame(framer);
            },
            iterations: 50,
            warmupIterations: 20);

        output.WriteLine($"Mutation-heavy 24-actor snapshot payload encode: {bytes} B.");
    }

    // Before WIRE-11 (measured on 1b7bdae with this test): 7,016 B. What is left is what the
    // decoded snapshot holds: the message, the exactly sized actor array (about 2.9 KB), the
    // session-id, display-name and dictionary-key strings, the dictionary and the two small arrays.
    [Fact]
    public void Decode_SnapshotWith24Actors_StaysWithinBudget()
    {
        TransportJsonCodec codec = new();
        ServerSnapshotMessage message = new(TwentyFourActorSnapshot());
        byte[] encoded = codec.Encode(Envelope(message, 900), message);

        long bytes = AllocationAssert.AtMost(SnapshotDecodeBudgetBytes, () => codec.Decode(encoded), iterations: 50);

        output.WriteLine($"24-actor snapshot decode: {bytes} B.");
    }

    // Measured 0 B after WIRE-11 for both encode tests (before: 824 B and 2,032 B).
    private const long SnapshotEncodeBudgetBytes = 0;

    // Measured 5,008 B after WIRE-11 (before: 7,016 B). The envelope's reflection state, the
    // List<T> growth of the mutation and damage lists, the dictionary rehashes and the
    // message-kind string are gone.
    private const long SnapshotDecodeBudgetBytes = 5_504;

    // ---- Helpers -------------------------------------------------------------------------

    private static ServerSnapshotEnvelope SnapshotWith(
        IReadOnlyDictionary<string, ulong>? sequences = null,
        IReadOnlyList<ActorSnapshot>? actors = null,
        IReadOnlyList<WorldMutationDelta>? mutations = null,
        IReadOnlyList<DamageEventDelta>? damage = null)
    {
        return new ServerSnapshotEnvelope(
            11,
            sequences ?? new Dictionary<string, ulong> { ["session-a"] = 3 },
            actors ?? [],
            PlayerInteractionMode.Combat,
            null,
            mutations ?? [],
            damage ?? [],
            WorldChecksum: 5,
            WorldChecksumSequence: 6);
    }

    private static void AssertByteIdentical(ServerSnapshotMessage message)
    {
        TransportJsonCodec codec = new();
        byte[] expectedPayload = JsonSerializer.SerializeToUtf8Bytes(message, Reference);

        System.Buffers.ArrayBufferWriter<byte> payload = new();
        codec.EncodePayload(message, payload);
        Assert.Equal(Encoding.UTF8.GetString(expectedPayload), Encoding.UTF8.GetString(payload.WrittenSpan));
        Assert.True(expectedPayload.AsSpan().SequenceEqual(payload.WrittenSpan));

        TransportEnvelope envelope = Envelope(message, 17);
        System.Buffers.ArrayBufferWriter<byte> expectedFrame = new();
        codec.WriteEnvelope(envelope, expectedPayload, expectedFrame);
        Assert.True(expectedFrame.WrittenSpan.SequenceEqual(codec.Encode(envelope, message)));

        using PooledFrameWriter pooled = new();
        LengthPrefixedMessageFramer framer = new();
        pooled.BeginFrame(framer);
        codec.EncodePayload(message, pooled);
        Assert.True(expectedPayload.AsSpan().SequenceEqual(pooled.WrittenMemory.Span[framer.HeaderLength..]));
    }

    private static void AssertSameFailure(ServerSnapshotMessage message)
    {
        Exception expected = Assert.ThrowsAny<Exception>(() => JsonSerializer.SerializeToUtf8Bytes(message, Reference));
        Exception actual = Assert.ThrowsAny<Exception>(() => new TransportJsonCodec().EncodePayload(message, new System.Buffers.ArrayBufferWriter<byte>()));
        Assert.Equal(expected.GetType(), actual.GetType());
    }

    // Both decodes are compared through the reference encoder, which writes every field (lists
    // and the dictionary in their decoded order), so equal bytes mean equal snapshots.
    private static void AssertDecodesLikeReference(TransportJsonCodec codec, string payload)
    {
        ServerSnapshotMessage expected = JsonSerializer.Deserialize<ServerSnapshotMessage>(payload, Reference)!;
        ServerSnapshotMessage actual = Assert.IsType<ServerSnapshotMessage>(DecodePayload(codec, payload));
        Assert.Equal(
            Encoding.UTF8.GetString(JsonSerializer.SerializeToUtf8Bytes(expected, Reference)),
            Encoding.UTF8.GetString(JsonSerializer.SerializeToUtf8Bytes(actual, Reference)));
        Assert.Equal(expected.Snapshot.World.HasValue, actual.Snapshot.World.HasValue);
        Assert.Equal(expected.Snapshot.WorldChecksum, actual.Snapshot.WorldChecksum);
        Assert.Equal(expected.Snapshot.LastAcceptedCommandSequences is null, actual.Snapshot.LastAcceptedCommandSequences is null);
        Assert.Equal(expected.Snapshot.Actors is null, actual.Snapshot.Actors is null);
        Assert.Equal(expected.Snapshot.WorldMutations is null, actual.Snapshot.WorldMutations is null);
        Assert.Equal(expected.Snapshot.DamageEvents is null, actual.Snapshot.DamageEvents is null);
    }

    private static void AssertMutationsEqual(IReadOnlyList<WorldMutationDelta>? expected, IReadOnlyList<WorldMutationDelta>? actual)
    {
        if (expected is null)
        {
            Assert.Null(actual);
            return;
        }

        Assert.NotNull(actual);
        Assert.Equal(expected.Count, actual.Count);
        for (int i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i] with { FracturePieces = null }, actual[i] with { FracturePieces = null });
            Assert.Equal(expected[i].FracturePieces, actual[i].FracturePieces);
        }
    }

    private static TransportEnvelope Envelope(IProtocolMessage message, ulong sequence)
    {
        return new TransportEnvelope(TransportEnvelope.CurrentProtocolVersion, message.MessageKind, sequence);
    }

    private static IProtocolMessage DecodePayload(TransportJsonCodec codec, string payload)
    {
        string json = "{\"protocolVersion\":" + TransportEnvelope.CurrentProtocolVersion
            + ",\"messageKind\":\"server.snapshot\",\"sequence\":1,\"payload\":" + payload + "}";
        return codec.Decode(Encoding.UTF8.GetBytes(json)).Message;
    }

    // Recursively shuffles every object's properties, adds unknown properties and drops some.
    // Inside lastAcceptedCommandSequences every property is an entry, so extras there are numbers.
    private static JsonNode Mangle(JsonNode node, Random random, bool inSequences)
    {
        if (node is JsonArray array)
        {
            for (int i = 0; i < array.Count; i++)
            {
                if (array[i] is JsonNode child)
                {
                    array[i] = Mangle(child.DeepClone(), random, inSequences: false);
                }
            }

            return array;
        }

        if (node is not JsonObject obj)
        {
            return node;
        }

        List<KeyValuePair<string, JsonNode?>> properties = [];
        foreach (KeyValuePair<string, JsonNode?> property in obj)
        {
            properties.Add(new KeyValuePair<string, JsonNode?>(property.Key, property.Value?.DeepClone()));
        }

        // Vector3 requires all three components, so a vector keeps them; it may still gain extras.
        bool isVector = obj.Count == 3 && obj.ContainsKey("x") && obj.ContainsKey("y") && obj.ContainsKey("z");
        JsonObject result = [];
        foreach (KeyValuePair<string, JsonNode?> property in properties.OrderBy(_ => random.Next()))
        {
            if (!isVector && random.Next(6) == 0)
            {
                continue;
            }

            if (random.Next(4) == 0)
            {
                result["x-unknown-" + random.Next(1000)] = inSequences
                    ? JsonValue.Create((ulong)random.NextInt64())
                    : RandomUnknownValue(random);
            }

            result[property.Key] = property.Value is null
                ? null
                : Mangle(property.Value, random, inSequences: property.Key == "lastAcceptedCommandSequences");
        }

        if (random.Next(3) == 0)
        {
            // Same name as a real property but different case: the reference is case-sensitive.
            result["X" + random.Next(10)] = inSequences ? JsonValue.Create(random.Next()) : RandomUnknownValue(random);
        }

        return result;
    }

    private static JsonNode? RandomUnknownValue(Random random)
    {
        return random.Next(6) switch
        {
            0 => null,
            1 => JsonValue.Create(random.Next()),
            2 => JsonValue.Create("str\"ing"),
            3 => new JsonArray(1, "two", new JsonObject { ["three"] = new JsonArray() }),
            4 => new JsonObject { ["nested"] = new JsonObject { ["deeper"] = true } },
            _ => JsonValue.Create(false)
        };
    }
}
