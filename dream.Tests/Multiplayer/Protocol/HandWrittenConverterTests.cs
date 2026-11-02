using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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

// TransportJsonCodec writes PlayerCommandFrame (with its nested intents) and ActorSnapshot with
// hand-written converters. ProtocolWireOptions.V3 has no such converters, so it is the
// reflection reference: every encode must match it byte for byte, and every decode, however the
// properties are ordered, padded or trimmed, must produce what the reference produces.
public class HandWrittenConverterTests(ITestOutputHelper output)
{
    private const int RandomCases = 400;

    private static readonly JsonSerializerOptions Reference = ProtocolWireOptions.V3;

    private static readonly float[] EdgeFloats =
    [
        0f, -0f, 1f, -1f, 0.1f, 1f / 3f, 1e-7f, -1e-7f, 16777217f, -123456.789f, 3.4e38f,
        float.Epsilon, -float.Epsilon, float.MaxValue, float.MinValue, 1e-45f, 2.5f, 100f, 0.5f, 1e10f
    ];

    private static readonly string[] EdgeStrings =
    [
        "",
        "session-a",
        "Wolf",
        "Q\"uote <b>&</b> caf" + (char)0x00E9 + " " + char.ConvertFromUtf32(0x1F389) + " back\\slash\ttab\nline '" + (char)0x2028 + "'",
        "+plus/slash?query=1&x=<y>",
        "\u0000\u0001\u001f\u007f",
        "lone " + (char)0xD800 + " surrogate",
        new string('x', 300)
    ];

    // ---- Byte identity -------------------------------------------------------------------

    [Fact]
    public void PlayerCommand_RandomFrames_AreByteIdenticalToReference()
    {
        Random random = new(20261028);
        for (int i = 0; i < RandomCases; i++)
        {
            PlayerCommandMessage message = new(RandomFrame(random));
            AssertByteIdentical(message);
        }
    }

    [Fact]
    public void PlayerCommand_EveryOptionalPresentAndAbsent_IsByteIdenticalToReference()
    {
        foreach (PlayerInteractionMode? mode in new PlayerInteractionMode?[] { null, PlayerInteractionMode.Building, PlayerInteractionMode.Combat, (PlayerInteractionMode)99 })
        {
            foreach (Vector3? facing in new Vector3?[] { null, Vector3.Zero, new Vector3(-0f, float.MaxValue, float.Epsilon) })
            {
                foreach (string? sessionId in new[] { null, "", "session-a", EdgeStrings[3] })
                {
                    AssertByteIdentical(new PlayerCommandMessage(new PlayerCommandFrame(
                        new PlayerSessionId(sessionId!),
                        ulong.MaxValue,
                        new PlayerModeSwitchIntent(mode),
                        new PlayerMoveIntent(-0f, float.MinValue, float.Epsilon, true, false, true),
                        new PlayerLookIntent(float.MaxValue, -float.Epsilon, facing),
                        PrimaryUsePressed: !mode.HasValue)));
                }
            }
        }

        AssertByteIdentical(new PlayerCommandMessage(default));
        AssertByteIdentical(new PlayerCommandMessage(PlayerCommandFrame.CreateIdle(PlayerSessionId.LocalDefault, 0)));
    }

    [Fact]
    public void Snapshot_RandomActors_AreByteIdenticalToReference()
    {
        Random random = new(4242);
        for (int i = 0; i < RandomCases / 4; i++)
        {
            int count = random.Next(0, 12);
            AssertByteIdentical(new ServerSnapshotMessage(SnapshotWith(RandomActors(random, count))));
        }
    }

    [Fact]
    public void Snapshot_EveryOptionalPresentAndAbsent_IsByteIdenticalToReference()
    {
        List<ActorSnapshot> actors = [];
        for (int mask = 0; mask < 64; mask++)
        {
            actors.Add(ActorWithMask(mask, mask));
        }

        actors.Add(default);
        actors.Add(new ActorSnapshot(int.MinValue, (CreatureKind)(-1), "", Vector3.Zero, Vector3.Zero, int.MaxValue, int.MinValue, false));
        AssertByteIdentical(new ServerSnapshotMessage(SnapshotWith(actors)));
    }

    // ActorSnapshot also travels in JoinAcceptedMessage, and PlayerSessionId and
    // PlayerCombatStatsSnapshot in PlayerActionMessage; those paths pick up the converters too.
    [Fact]
    public void OtherMessagesCarryingTheConvertedTypes_AreByteIdenticalToReference()
    {
        Random random = new(7);
        AssertByteIdentical(new JoinAcceptedMessage("session-a", 1, 2, null, InitialActors: RandomActors(random, 9)));
        AssertByteIdentical(new JoinAcceptedMessage("session-a", 1, 2, null, InitialActors: null));
        AssertByteIdentical(new PlayerActionMessage(new PlayerSessionId(EdgeStrings[3]), 9, new InventoryEquipIntent(3)));
        AssertByteIdentical(new PlayerActionMessage(new PlayerSessionId(null!), 9, new InventoryEquipIntent(3)));
        AssertByteIdentical(new PlayerActionMessage(
            new PlayerSessionId("s"), 1, new PlayerStatsUpdateIntent(new PlayerCombatStatsSnapshot(int.MinValue, -0f, int.MaxValue))));
        AssertByteIdentical(new ServerActionMessage(new AuthoritativeActionResult(
            "s", 1, 2, true, new PlayerStatsUpdateResult(new PlayerCombatStatsSnapshot(1, float.Epsilon, 2)))));
    }

    // Protocol v13: PlayerInventorySnapshot is written by PlayerInventorySnapshotJsonConverter, which the
    // reference registers too (the compact shape has no reflection equivalent). The codec must be using it
    // (the reflection shape is far larger), in the join, the push and an inventory action result alike.
    [Fact]
    public void InventorySnapshot_IsWrittenByTheCompactConverter_InEveryMessageThatCarriesIt()
    {
        PlayerInventorySnapshot inventory = InventoryWireSamples.Starter("Alice");
        JoinAcceptedMessage join = new("session-a", 1, 2, null, Inventory: inventory);
        InventoryPushMessage push = new(inventory, [new LootNotice("Leather Cap", Dream.Simulation.Inventory.ItemRarity.Rare, Lost: false)]);
        ServerActionMessage result = new(new AuthoritativeActionResult("s", 1, 2, true, new InventoryChangedResult(inventory)));

        AssertByteIdentical(join);
        AssertByteIdentical(push);
        AssertByteIdentical(result);

        AssertCompact(join);
        AssertCompact(push);
        AssertCompact(result);

        static void AssertCompact<TMessage>(TMessage message)
            where TMessage : IProtocolMessage
        {
            System.Buffers.ArrayBufferWriter<byte> compact = new();
            new TransportJsonCodec().EncodePayload(message, compact);
            int reflection = JsonSerializer.SerializeToUtf8Bytes(message, ProtocolWireOptions.V3Reflection).Length;
            Assert.Contains("\"bag\":[[", Encoding.UTF8.GetString(compact.WrittenSpan), StringComparison.Ordinal);
            Assert.True(compact.WrittenCount * 3 < reflection, $"{typeof(TMessage).Name}: {compact.WrittenCount} B compact, {reflection} B as reflection");
        }
    }

    [Fact]
    public void NonFiniteFloats_FailLikeReference()
    {
        foreach (float bad in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
        {
            PlayerCommandMessage command = new(new PlayerCommandFrame(
                new PlayerSessionId("s"), 1, PlayerModeSwitchIntent.None, new PlayerMoveIntent(bad, 0f), PlayerLookIntent.None, false));
            AssertSameFailure(command);

            ServerSnapshotMessage snapshot = new(SnapshotWith(
                [new ActorSnapshot(1, CreatureKind.Enemy, null, new Vector3(bad, 0f, 0f), Vector3.Zero, 1, 1, true)]));
            AssertSameFailure(snapshot);
        }
    }

    // ---- Round trip ----------------------------------------------------------------------

    [Fact]
    public void PlayerCommand_RandomFrames_RoundTrip()
    {
        TransportJsonCodec codec = new();
        Random random = new(99);
        for (int i = 0; i < RandomCases; i++)
        {
            PlayerCommandMessage message = new(RandomFrame(random, roundTrippable: true));
            byte[] encoded = codec.Encode(Envelope(message, (ulong)i), message);
            DecodedProtocolMessage decoded = codec.Decode(encoded);
            Assert.Equal(message, Assert.IsType<PlayerCommandMessage>(decoded.Message));
            Assert.Equal((ulong)i, decoded.Envelope.Sequence);
        }
    }

    [Fact]
    public void Snapshot_RandomActors_RoundTrip()
    {
        TransportJsonCodec codec = new();
        Random random = new(123);
        for (int i = 0; i < RandomCases / 4; i++)
        {
            ActorSnapshot[] actors = RandomActors(random, random.Next(0, 12), roundTrippable: true);
            ServerSnapshotMessage message = new(SnapshotWith(actors));
            DecodedProtocolMessage decoded = codec.Decode(codec.Encode(Envelope(message, 1), message));
            Assert.Equal(actors, Assert.IsType<ServerSnapshotMessage>(decoded.Message).Snapshot.Actors);
        }
    }

    // ---- Tolerant decode -----------------------------------------------------------------

    // Shuffled property order, unknown properties of every JSON shape, and randomly dropped
    // properties (optional or not): the codec decodes to exactly what the reflection reference does.
    [Fact]
    public void PlayerCommand_MangledPayloads_DecodeLikeReference()
    {
        TransportJsonCodec codec = new();
        Random random = new(5150);
        for (int i = 0; i < RandomCases; i++)
        {
            PlayerCommandMessage message = new(RandomFrame(random));
            JsonNode payload = JsonSerializer.SerializeToNode(message, Reference)!;
            string mangled = Mangle(payload, random).ToJsonString();

            PlayerCommandMessage expected = JsonSerializer.Deserialize<PlayerCommandMessage>(mangled, Reference)!;
            PlayerCommandMessage actual = Assert.IsType<PlayerCommandMessage>(DecodePayload(codec, "player.command", mangled));
            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void Snapshot_MangledPayloads_DecodeLikeReference()
    {
        TransportJsonCodec codec = new();
        Random random = new(8086);
        for (int i = 0; i < RandomCases / 4; i++)
        {
            ServerSnapshotMessage message = new(SnapshotWith(RandomActors(random, random.Next(1, 8))));
            JsonNode payload = JsonSerializer.SerializeToNode(message, Reference)!;
            JsonArray actors = payload["snapshot"]!["actors"]!.AsArray();
            for (int a = 0; a < actors.Count; a++)
            {
                actors[a] = Mangle(actors[a]!.DeepClone(), random);
            }

            string mangled = payload.ToJsonString();
            ServerSnapshotMessage expected = JsonSerializer.Deserialize<ServerSnapshotMessage>(mangled, Reference)!;
            ServerSnapshotMessage actual = Assert.IsType<ServerSnapshotMessage>(DecodePayload(codec, "server.snapshot", mangled));
            Assert.Equal(expected.Snapshot.Actors, actual.Snapshot.Actors);
        }
    }

    [Fact]
    public void HandWrittenPayloads_ReorderedExtraAndMissing_Decode()
    {
        TransportJsonCodec codec = new();

        PlayerCommandMessage command = Assert.IsType<PlayerCommandMessage>(DecodePayload(
            codec,
            "player.command",
            """
            {"extra":[1,{"a":null}],"commandFrame":{"primaryUsePressed":true,"lookIntent":{"facing":{"z":3,"y":2,"x":1},"pitchDelta":-2},
             "requestedMode":{},"sequence":7,"unknown":{"sessionId":"no"},"sessionId":{"value":"s-1","other":true}},"tail":"x"}
            """));
        Assert.Equal(
            new PlayerCommandFrame(
                new PlayerSessionId("s-1"), 7, PlayerModeSwitchIntent.None, PlayerMoveIntent.None,
                new PlayerLookIntent(0f, -2f, new Vector3(1f, 2f, 3f)), true),
            command.CommandFrame);

        PlayerCommandMessage empty = Assert.IsType<PlayerCommandMessage>(DecodePayload(codec, "player.command", "{}"));
        Assert.Equal(default, empty.CommandFrame);

        ServerSnapshotMessage snapshot = Assert.IsType<ServerSnapshotMessage>(DecodePayload(
            codec,
            "server.snapshot",
            """
            {"snapshot":{"serverTick":1,"lastAcceptedCommandSequences":{},"interactionMode":0,"worldMutations":[],"damageEvents":[],
             "actors":[{"isAlive":true,"facing":{"x":0,"y":0,"z":-1},"actorId":5,"junk":[[]],"position":{"x":1,"y":2,"z":3},
                        "combatStats":{"vitality":3,"strength":1},"progression":null,"displayName":"W","enemyBehaviorState":2,"kind":1},
                       {}]}}
            """));
        Assert.Equal(
            new ActorSnapshot[]
            {
                new ActorSnapshot(5, CreatureKind.Enemy, null, new Vector3(1f, 2f, 3f), -Vector3.UnitZ, 0, 0, true,
                    CombatStats: new PlayerCombatStatsSnapshot(1, 0f, 3), DisplayName: "W", EnemyBehaviorState: EnemyBehaviorState.Attacking),
                default
            },
            snapshot.Snapshot.Actors);
    }

    [Theory]
    [InlineData("""{"commandFrame":{"sequence":"7"}}""")]
    [InlineData("""{"commandFrame":{"sequence":-1}}""")]
    [InlineData("""{"commandFrame":{"primaryUsePressed":1}}""")]
    [InlineData("""{"commandFrame":{"requestedMode":{"mode":"Combat"}}}""")]
    [InlineData("""{"commandFrame":{"sessionId":{"value":5}}}""")]
    [InlineData("""{"commandFrame":{"moveIntent":{"forwardAxis":true}}}""")]
    [InlineData("""{"commandFrame":{"lookIntent":{"facing":{"x":1}}}}""")]
    [InlineData("""{"commandFrame":null}""")]
    [InlineData("""{"commandFrame":[]}""")]
    [InlineData("""{"commandFrame":{"moveIntent":null}}""")]
    public void PlayerCommand_InvalidPayloads_FailWithJsonExceptionLikeReference(string payload)
    {
        Assert.ThrowsAny<JsonException>(() => JsonSerializer.Deserialize<PlayerCommandMessage>(payload, Reference));
        Assert.ThrowsAny<JsonException>(() => DecodePayload(new TransportJsonCodec(), "player.command", payload));
    }

    [Theory]
    [InlineData("""{"actorId":1.5}""")]
    [InlineData("""{"kind":"Enemy"}""")]
    [InlineData("""{"sessionId":7}""")]
    [InlineData("""{"isAlive":"true"}""")]
    [InlineData("""{"position":null}""")]
    [InlineData("""{"combatStats":{"attackSpeed":"fast"}}""")]
    [InlineData("""{"progression":[]}""")]
    [InlineData("""{"currentHealth":2147483648}""")]
    [InlineData("""3""")]
    public void Actor_InvalidPayloads_FailWithJsonExceptionLikeReference(string actor)
    {
        string payload = "{\"snapshot\":{\"actors\":[" + actor + "]}}";
        Assert.ThrowsAny<JsonException>(() => JsonSerializer.Deserialize<ServerSnapshotMessage>(payload, Reference));
        Assert.ThrowsAny<JsonException>(() => DecodePayload(new TransportJsonCodec(), "server.snapshot", payload));
    }

    // ---- Allocations ---------------------------------------------------------------------

    // Before WIRE-10: 520 B, all inside the reflection serializer (boxed record structs plus the
    // WriteStack frame array).
    [Fact]
    public void Encode_PlayerCommandIntoWarmedPooledWriter_AllocatesNothing()
    {
        TransportJsonCodec codec = new();
        LengthPrefixedMessageFramer framer = new();
        using PooledFrameWriter writer = new();
        PlayerCommandMessage message = new(new PlayerCommandFrame(
            new PlayerSessionId("session-a"),
            42,
            new PlayerModeSwitchIntent(PlayerInteractionMode.Combat),
            new PlayerMoveIntent(1f, -1f, 0f, JumpPressed: true),
            new PlayerLookIntent(3f, -2f, Vector3.UnitX),
            PrimaryUsePressed: true));
        TransportEnvelope envelope = Envelope(message, 314);

        long bytes = AllocationAssert.AtMost(
            0,
            () =>
            {
                writer.BeginFrame(framer);
                codec.Encode(envelope, message, writer);
                writer.CompleteFrame(framer);
            },
            iterations: 200,
            warmupIterations: 20);

        output.WriteLine($"PlayerCommand encode into a warmed PooledFrameWriter: {bytes} B.");
    }

    // Before WIRE-10: 5,040 B (each actor boxed on write).
    [Fact]
    public void Encode_SnapshotWith24ActorsIntoWarmedPooledWriter_StaysWithinBudget()
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

    // Before WIRE-10: 14,736 B (List<ActorSnapshot> grown by doubling plus per-actor
    // constructor-argument state).
    [Fact]
    public void Decode_SnapshotWith24Actors_StaysWithinBudget()
    {
        TransportJsonCodec codec = new();
        ServerSnapshotMessage message = new(TwentyFourActorSnapshot());
        byte[] encoded = codec.Encode(Envelope(message, 900), message);

        long bytes = AllocationAssert.AtMost(SnapshotDecodeBudgetBytes, () => codec.Decode(encoded), iterations: 50);

        output.WriteLine($"24-actor snapshot decode: {bytes} B.");
    }

    // Measured 0 B after WIRE-11 (before: 824 B). The envelope is now written by hand too, so
    // nothing on the encode path allocates.
    private const long SnapshotEncodeBudgetBytes = 0;

    // Measured 5,008 B after WIRE-11 (before: 7,016 B): the exactly sized ActorSnapshot[], the
    // session-id and display-name strings, and the message objects. Same budget as
    // ServerSnapshotConverterTests.
    // Wave I added EnemyLevel and VisualKey to ActorSnapshot (16 B per actor with padding); the 24-actor
    // decode measured 5,640 B afterwards, so the budget went from 5,504 B to 5,760 B.
    private const long SnapshotDecodeBudgetBytes = 6_144; // protocol 14 widened the combat group

    // ---- Helpers -------------------------------------------------------------------------

    private static void AssertByteIdentical<TMessage>(TMessage message)
        where TMessage : IProtocolMessage
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
    }

    private static void AssertSameFailure<TMessage>(TMessage message)
        where TMessage : IProtocolMessage
    {
        Exception expected = Assert.ThrowsAny<Exception>(() => JsonSerializer.SerializeToUtf8Bytes(message, Reference));
        Exception actual = Assert.ThrowsAny<Exception>(() => new TransportJsonCodec().EncodePayload(message, new System.Buffers.ArrayBufferWriter<byte>()));
        Assert.Equal(expected.GetType(), actual.GetType());
    }

    private static TransportEnvelope Envelope(IProtocolMessage message, ulong sequence)
    {
        return new TransportEnvelope(TransportEnvelope.CurrentProtocolVersion, message.MessageKind, sequence);
    }

    private static IProtocolMessage DecodePayload(TransportJsonCodec codec, string messageKind, string payload)
    {
        string json = "{\"protocolVersion\":" + TransportEnvelope.CurrentProtocolVersion
            + ",\"messageKind\":\"" + messageKind + "\",\"sequence\":1,\"payload\":" + payload + "}";
        return codec.Decode(Encoding.UTF8.GetBytes(json)).Message;
    }

    // Recursively shuffles every object's properties, adds unknown properties and drops some.
    private static JsonNode Mangle(JsonNode node, Random random)
    {
        if (node is JsonArray array)
        {
            for (int i = 0; i < array.Count; i++)
            {
                if (array[i] is JsonNode child)
                {
                    array[i] = Mangle(child.DeepClone(), random);
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
                result["x-unknown-" + random.Next(1000)] = RandomUnknownValue(random);
            }

            result[property.Key] = property.Value is null ? null : Mangle(property.Value, random);
        }

        if (random.Next(3) == 0)
        {
            // Same name as a real property but different case: the reference is case-sensitive.
            result["X" + random.Next(10)] = RandomUnknownValue(random);
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

    private static float RandomFloat(Random random)
    {
        return random.Next(3) == 0
            ? EdgeFloats[random.Next(EdgeFloats.Length)]
            : (float)((random.NextDouble() - 0.5) * Math.Pow(10, random.Next(-8, 9)));
    }

    private static Vector3 RandomVector(Random random)
    {
        return new Vector3(RandomFloat(random), RandomFloat(random), RandomFloat(random));
    }

    // A lone surrogate is written as U+FFFD, so it cannot round-trip; byte identity still covers it.
    private static string? RandomString(Random random, bool allowNull = true, bool roundTrippable = false)
    {
        int pick = random.Next(EdgeStrings.Length + 2);
        if (pick == EdgeStrings.Length)
        {
            return allowNull ? null : "fallback";
        }

        if (pick == EdgeStrings.Length + 1 || (roundTrippable && EdgeStrings[pick].Contains((char)0xD800)))
        {
            return $"player-{random.Next()}";
        }

        return EdgeStrings[pick];
    }

    private static PlayerCommandFrame RandomFrame(Random random, bool roundTrippable = false)
    {
        PlayerInteractionMode? mode = random.Next(4) switch
        {
            0 => null,
            1 => PlayerInteractionMode.Building,
            2 => PlayerInteractionMode.Combat,
            _ => (PlayerInteractionMode)random.Next(-5, 50)
        };

        return new PlayerCommandFrame(
            new PlayerSessionId(RandomString(random, roundTrippable: roundTrippable)!),
            random.Next(4) == 0 ? ulong.MaxValue : (ulong)random.NextInt64(),
            new PlayerModeSwitchIntent(mode),
            new PlayerMoveIntent(
                RandomFloat(random), RandomFloat(random), RandomFloat(random),
                random.Next(2) == 0, random.Next(2) == 0, random.Next(2) == 0),
            new PlayerLookIntent(RandomFloat(random), RandomFloat(random), random.Next(2) == 0 ? null : RandomVector(random)),
            random.Next(2) == 0);
    }

    private static ActorSnapshot[] RandomActors(Random random, int count, bool roundTrippable = false)
    {
        ActorSnapshot[] actors = new ActorSnapshot[count];
        for (int i = 0; i < count; i++)
        {
            actors[i] = ActorWithMask(random.Next(64), random.Next(), random, roundTrippable);
        }

        return actors;
    }

    // Each of the six low mask bits makes one nullable property present.
    private static ActorSnapshot ActorWithMask(int mask, int seed, Random? random = null, bool roundTrippable = false)
    {
        random ??= new Random(seed);
        int RandomInt() => random.Next(5) == 0 ? (random.Next(2) == 0 ? int.MinValue : int.MaxValue) : random.Next(-1000, 1000);

        return new ActorSnapshot(
            RandomInt(),
            (CreatureKind)random.Next(0, 4),
            (mask & 1) != 0 ? RandomString(random, allowNull: false, roundTrippable: roundTrippable) : null,
            RandomVector(random),
            RandomVector(random),
            RandomInt(),
            RandomInt(),
            random.Next(2) == 0,
            (mask & 2) != 0 ? (PlayerInteractionMode)random.Next(0, 3) : null,
            (mask & 4) != 0 ? new PlayerCombatStatsSnapshot(RandomInt(), RandomFloat(random), RandomInt()) : null,
            (mask & 8) != 0 ? RandomString(random, allowNull: false, roundTrippable: roundTrippable) : null,
            (mask & 16) != 0 ? new PlayerProgressionSnapshot(RandomInt(), RandomInt(), RandomInt()) : null,
            (mask & 32) != 0 ? (EnemyBehaviorState)random.Next(0, 5) : null);
    }

    private static ServerSnapshotEnvelope SnapshotWith(IReadOnlyList<ActorSnapshot> actors)
    {
        return new ServerSnapshotEnvelope(
            11,
            new Dictionary<string, ulong> { ["session-a"] = 3 },
            actors,
            PlayerInteractionMode.Combat,
            null,
            [],
            [],
            WorldChecksum: 5,
            WorldChecksumSequence: 6);
    }

    // The same 24-actor mix as TransportJsonCodecDecodeTests: every third actor is a player with
    // every optional property, the rest are wolves.
    private static ServerSnapshotEnvelope TwentyFourActorSnapshot()
    {
        ActorSnapshot[] actors = new ActorSnapshot[24];
        Dictionary<string, ulong> sequences = new();
        for (int index = 0; index < actors.Length; index++)
        {
            bool isPlayer = index % 3 == 0;
            actors[index] = new ActorSnapshot(
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
            if (actors[index].SessionId is string sessionId)
            {
                sequences[sessionId] = (ulong)(100 + index);
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
}
