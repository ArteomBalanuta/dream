using System.Buffers;
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
using Dream.Simulation.World.Geometry;

namespace Dream.Tests.Multiplayer.Protocol;

public class ProtocolV2SlimmingTests(ITestOutputHelper output)
{
    [Fact]
    public void CurrentProtocolVersion_IsSix_AndServerHelloAdvertisesIt()
    {
        ServerRoutingContext context = new("connection-a");

        new ClientHelloHandler().Handle(context, new ClientHelloMessage("client-a"));

        Assert.Equal(6, TransportEnvelope.CurrentProtocolVersion);
        ServerHelloMessage hello = Assert.IsType<ServerHelloMessage>(Assert.Single(context.OutboundMessages));
        Assert.Equal(TransportEnvelope.CurrentProtocolVersion, hello.ProtocolVersion);
    }

    [Fact]
    public void V1Payloads_CarryDerivedProperties_AndV2PayloadsDoNot()
    {
        ServerSnapshotMessage snapshot = new(CreateFixtureSnapshot());
        JoinAcceptedMessage join = CreateFixtureJoin();

        string v1Snapshot = JsonSerializer.Serialize(snapshot, ProtocolWireOptions.V1);
        string v1Join = JsonSerializer.Serialize(join, ProtocolWireOptions.V1);
        string v2Snapshot = EncodePayloadJson(snapshot);
        string v2Join = EncodePayloadJson(join);
        output.WriteLine($"v1 snapshot payload starts: {v1Snapshot[..Math.Min(160, v1Snapshot.Length)]}");
        output.WriteLine($"v1 join payload ends: {v1Join[Math.Max(0, v1Join.Length - 160)..]}");

        // Protocol v1 leaked them: [JsonIgnore] on IProtocolMessage.MessageKind does not reach the
        // records' own MessageKind, and the Effective* getters were serialized as properties.
        Assert.Contains("\"messageKind\":\"server.snapshot\"", v1Snapshot, StringComparison.Ordinal);
        Assert.Contains("\"messageKind\":\"server.join-accepted\"", v1Join, StringComparison.Ordinal);
        Assert.Contains("\"effectiveRoster\":", v1Join, StringComparison.Ordinal);
        Assert.Contains("\"effectiveInitialActors\":", v1Join, StringComparison.Ordinal);

        foreach (string json in new[] { v2Snapshot, v2Join })
        {
            Assert.DoesNotContain("messageKind", json, StringComparison.Ordinal);
            Assert.DoesNotContain("effectiveRoster", json, StringComparison.Ordinal);
            Assert.DoesNotContain("effectiveInitialActors", json, StringComparison.Ordinal);
            Assert.DoesNotContain(":null", json, StringComparison.Ordinal);
        }

        Assert.Contains("\"roster\":[", v2Join, StringComparison.Ordinal);
        Assert.Contains("\"initialActors\":[", v2Join, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryMessageType_DropsMessageKindFromItsPayload()
    {
        foreach (IProtocolMessage message in TransportJsonCodecEncodeTests.GoldenMessages())
        {
            Assert.DoesNotContain("messageKind", EncodePayloadJson(message), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void V2_ShrinksSnapshotByAtLeastFifteenPercent_AndJoinLosesItsDuplicates()
    {
        ServerSnapshotMessage snapshot = new(CreateFixtureSnapshot());
        JoinAcceptedMessage join = CreateFixtureJoin();

        int v1Snapshot = JsonSerializer.SerializeToUtf8Bytes(snapshot, ProtocolWireOptions.V1).Length;
        int v2Snapshot = EncodePayload(snapshot).Length;
        int v1Join = JsonSerializer.SerializeToUtf8Bytes(join, ProtocolWireOptions.V1).Length;
        int v2Join = EncodePayload(join).Length;

        // The duplicated lists alone, as v1 wrote them a second time.
        int rosterBytes = JsonSerializer.SerializeToUtf8Bytes(join.EffectiveRoster, ProtocolWireOptions.V1).Length;
        int actorBytes = JsonSerializer.SerializeToUtf8Bytes(join.EffectiveInitialActors, ProtocolWireOptions.V1).Length;

        output.WriteLine(
            $"Snapshot (4 players, 20 enemies, 4 mutations): v1 {v1Snapshot} B, v2 {v2Snapshot} B " +
            $"({100.0 * (v1Snapshot - v2Snapshot) / v1Snapshot:F1}% smaller).");
        output.WriteLine(
            $"JoinAccepted (4 roster entries, 24 initial actors): v1 {v1Join} B, v2 {v2Join} B " +
            $"({100.0 * (v1Join - v2Join) / v1Join:F1}% smaller); v1 duplicates were {rosterBytes + actorBytes} B.");

        Assert.True(v2Snapshot * 100 <= v1Snapshot * 85, $"v2 snapshot {v2Snapshot} B is not 15% below v1 {v1Snapshot} B.");
        Assert.True(v2Join <= v1Join - rosterBytes - actorBytes, $"v2 join {v2Join} B still carries the duplicated lists (v1 {v1Join} B).");
    }

    [Theory]
    [MemberData(nameof(TransportJsonCodecEncodeTests.GoldenMessages), MemberType = typeof(TransportJsonCodecEncodeTests))]
    public void V2_RoundTripsEveryMessageToAnEqualRecord(IProtocolMessage message)
    {
        TransportJsonCodec codec = new();
        TransportEnvelope envelope = new(TransportEnvelope.CurrentProtocolVersion, message.MessageKind, 77);

        DecodedProtocolMessage decoded = codec.Decode(Encode(codec, envelope, message));

        Assert.Equal(envelope, decoded.Envelope);
        Assert.Equal(message.GetType(), decoded.Message.GetType());
        AssertSameRecord(message, decoded.Message);
    }

    [Fact]
    public void V2_RoundTripsFixtureSnapshotAndJoin()
    {
        TransportJsonCodec codec = new();
        foreach (IProtocolMessage message in new IProtocolMessage[] { new ServerSnapshotMessage(CreateFixtureSnapshot()), CreateFixtureJoin() })
        {
            TransportEnvelope envelope = new(TransportEnvelope.CurrentProtocolVersion, message.MessageKind, 5);

            AssertSameRecord(message, codec.Decode(Encode(codec, envelope, message)).Message);
        }
    }

    // A v1-shaped payload (explicit nulls, messageKind, effective lists) in a v2 envelope decodes
    // to the same record as the slim v2 payload: a missing property equals an explicit null, and
    // the dropped properties were never read back.
    [Theory]
    [MemberData(nameof(TransportJsonCodecEncodeTests.GoldenMessages), MemberType = typeof(TransportJsonCodecEncodeTests))]
    public void MissingProperty_DecodesLikeExplicitNull(IProtocolMessage message)
    {
        TransportJsonCodec codec = new();
        string v1Payload = JsonSerializer.Serialize(message, message.GetType(), ProtocolWireOptions.V1WithV3World);
        string json = $"{{\"protocolVersion\":6,\"messageKind\":\"{message.MessageKind}\",\"sequence\":1,\"payload\":{v1Payload}}}";

        IProtocolMessage fromExplicitNulls = codec.Decode(Encoding.UTF8.GetBytes(json)).Message;
        IProtocolMessage fromSlim = codec.Decode(Encode(codec, new TransportEnvelope(TransportEnvelope.CurrentProtocolVersion, message.MessageKind, 1), message)).Message;

        AssertSameRecord(fromExplicitNulls, fromSlim);
        AssertSameRecord(message, fromSlim);
    }

    [Fact]
    public void Decode_HandWrittenV2Snapshot_WithOptionalFieldsOmitted()
    {
        DecodedProtocolMessage decoded = DecodeJson(
            """
            {"protocolVersion":6,"messageKind":"server.snapshot","sequence":9,"payload":{"snapshot":{
              "serverTick":120,
              "lastAcceptedCommandSequences":{"session-a":4},
              "actors":[
                {"actorId":2000,"kind":1,"position":{"x":1,"y":2,"z":3},"facing":{"x":0,"y":0,"z":-1},
                 "currentHealth":30,"maxHealth":40,"isAlive":true,"displayName":"Wolf","enemyBehaviorState":2},
                {"actorId":100,"kind":0,"sessionId":"session-a","position":{"x":0,"y":0,"z":0},"facing":{"x":1,"y":0,"z":0},
                 "currentHealth":90,"maxHealth":100,"isAlive":true,"interactionMode":1}
              ],
              "interactionMode":1,
              "worldMutations":[{"kind":1,"cellPosition":{"x":4,"y":0,"z":4},"sequence":3}],
              "damageEvents":[],
              "worldChecksumSequence":0
            }}}
            """);

        ServerSnapshotEnvelope snapshot = Assert.IsType<ServerSnapshotMessage>(decoded.Message).Snapshot;
        Assert.Equal(new TransportEnvelope(TransportEnvelope.CurrentProtocolVersion, "server.snapshot", 9), decoded.Envelope);
        Assert.Null(snapshot.World);
        Assert.Null(snapshot.WorldChecksum);
        Assert.Equal((ulong)4, snapshot.LastAcceptedCommandSequences["session-a"]);
        Assert.Empty(snapshot.DamageEvents);

        Assert.Equal(
            new ActorSnapshot(
                2000,
                CreatureKind.Enemy,
                null,
                new Vector3(1f, 2f, 3f),
                -Vector3.UnitZ,
                30,
                40,
                true,
                InteractionMode: null,
                CombatStats: null,
                DisplayName: "Wolf",
                Progression: null,
                EnemyBehaviorState: EnemyBehaviorState.Attacking),
            snapshot.Actors[0]);
        Assert.Equal(
            new ActorSnapshot(100, CreatureKind.Player, "session-a", Vector3.Zero, Vector3.UnitX, 90, 100, true, PlayerInteractionMode.Combat),
            snapshot.Actors[1]);
        Assert.Equal(
            new WorldMutationDelta(WorldMutationKind.CubeDestroyed, new Vector3(4f, 0f, 4f), Sequence: 3),
            Assert.Single(snapshot.WorldMutations));
    }

    [Fact]
    public void Decode_HandWrittenV2JoinAndAction_WithOptionalFieldsOmitted()
    {
        JoinAcceptedMessage join = Assert.IsType<JoinAcceptedMessage>(DecodeJson(
            """{"protocolVersion":6,"messageKind":"server.join-accepted","sequence":1,"payload":{"sessionId":"session-a","actorId":101,"currentServerTick":22}}""").Message);

        Assert.Equal(new JoinAcceptedMessage("session-a", 101, 22, InitialWorld: null), join);
        Assert.Empty(join.EffectiveRoster);
        Assert.Empty(join.EffectiveInitialActors);

        ServerActionMessage action = Assert.IsType<ServerActionMessage>(DecodeJson(
            """
            {"protocolVersion":6,"messageKind":"server.action","sequence":2,"payload":{"result":{
              "sessionId":"session-a","actionSequence":5,"serverTick":6,"accepted":true,
              "payload":{"$type":"combat.resolution","damageApplied":3,"targetDied":false,"currentHealth":7,"maxHealth":10},
              "correction":{"correctedFacing":{"x":0,"y":1,"z":0}}}}}
            """).Message);

        Assert.Equal(
            new ServerActionMessage(new AuthoritativeActionResult(
                "session-a",
                5,
                6,
                true,
                new CombatResolutionResult(null, null, 3, false, 7, 10),
                new ClientCorrectionDelta(null, Vector3.UnitY),
                Rejection: null)),
            action);

        PlayerCommandMessage command = Assert.IsType<PlayerCommandMessage>(DecodeJson(
            """
            {"protocolVersion":6,"messageKind":"player.command","sequence":3,"payload":{"commandFrame":{
              "sessionId":{"value":"session-a"},"sequence":8,"requestedMode":{},
              "moveIntent":{"forwardAxis":1,"strafeAxis":0},"lookIntent":{"yawDelta":0,"pitchDelta":0},
              "primaryUsePressed":false}}}
            """).Message);

        Assert.Equal(
            new PlayerCommandMessage(new PlayerCommandFrame(
                new PlayerSessionId("session-a"),
                8,
                PlayerModeSwitchIntent.None,
                new PlayerMoveIntent(1f, 0f),
                PlayerLookIntent.None,
                PrimaryUsePressed: false)),
            command);
    }

    [Fact]
    public void Decode_RejectsV1Envelope_WithVersionMismatchError()
    {
        ClientHelloMessage message = new("client-a");
        byte[] v1 = JsonSerializer.SerializeToUtf8Bytes(
            new { protocolVersion = 1, messageKind = message.MessageKind, sequence = 1UL, payload = message },
            ProtocolWireOptions.V1);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => new TransportJsonCodec().Decode(v1));

        Assert.Equal("Unsupported protocol version 1. Supported version: 6.", ex.Message);
    }

    [Fact]
    public void Encode_RejectsV1Envelope()
    {
        ClientHelloMessage message = new("client-a");

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => new TransportJsonCodec().Encode(new TransportEnvelope(1, message.MessageKind, 1), message));

        Assert.Equal("Cannot encode protocol version 1. Supported version: 6.", ex.Message);
    }

    [Fact]
    public void EmptyCollections_StayOnTheWireAsEmpty()
    {
        TransportJsonCodec codec = new();
        ServerSnapshotMessage snapshot = new(new ServerSnapshotEnvelope(
            1,
            new Dictionary<string, ulong>(),
            [],
            PlayerInteractionMode.Combat,
            new WorldSnapshot(2, 2, 2, [], FracturedCubeCells: []),
            [],
            []));
        JoinAcceptedMessage join = new("session-a", 1, 2, InitialWorld: null, Roster: [], InitialActors: []);

        string snapshotJson = EncodePayloadJson(snapshot);
        string joinJson = EncodePayloadJson(join);

        Assert.Contains("\"lastAcceptedCommandSequences\":{}", snapshotJson, StringComparison.Ordinal);
        Assert.Contains("\"actors\":[]", snapshotJson, StringComparison.Ordinal);
        Assert.Contains("\"worldMutations\":[]", snapshotJson, StringComparison.Ordinal);
        Assert.Contains("\"damageEvents\":[]", snapshotJson, StringComparison.Ordinal);
        // Protocol v3: an empty world is its dimensions alone; empty sections are left out.
        Assert.Contains("\"world\":{\"w\":2,\"h\":2,\"d\":2}", snapshotJson, StringComparison.Ordinal);
        Assert.Contains("\"roster\":[]", joinJson, StringComparison.Ordinal);
        Assert.Contains("\"initialActors\":[]", joinJson, StringComparison.Ordinal);

        ServerSnapshotEnvelope decodedSnapshot = Assert.IsType<ServerSnapshotMessage>(
            codec.Decode(Encode(codec, new TransportEnvelope(TransportEnvelope.CurrentProtocolVersion, snapshot.MessageKind, 1), snapshot)).Message).Snapshot;
        Assert.NotNull(decodedSnapshot.LastAcceptedCommandSequences);
        Assert.Empty(decodedSnapshot.LastAcceptedCommandSequences);
        Assert.NotNull(decodedSnapshot.Actors);
        Assert.Empty(decodedSnapshot.Actors);
        Assert.NotNull(decodedSnapshot.WorldMutations);
        Assert.NotNull(decodedSnapshot.DamageEvents);
        Assert.True(decodedSnapshot.World.HasValue);
        WorldSnapshot world = decodedSnapshot.World.Value;
        // A decoded v3 world always has every list, empty when the section was left out.
        Assert.Empty(world.PlacedCubeCells);
        Assert.Empty(world.FracturedCubeCells!);
        Assert.Empty(world.AuthoredSubcellCells!);
        Assert.Empty(world.PlacedCubes!);
        Assert.Empty(world.FracturedCubes!);

        JoinAcceptedMessage decodedJoin = Assert.IsType<JoinAcceptedMessage>(
            codec.Decode(Encode(codec, new TransportEnvelope(TransportEnvelope.CurrentProtocolVersion, join.MessageKind, 1), join)).Message);
        Assert.NotNull(decodedJoin.Roster);
        Assert.Empty(decodedJoin.Roster);
        Assert.NotNull(decodedJoin.InitialActors);
        Assert.Empty(decodedJoin.InitialActors);
    }

    // Records holding lists compare those lists by reference, so equality is checked on the v1
    // serialization, which writes every property including nulls.
    private static void AssertSameRecord(IProtocolMessage expected, IProtocolMessage actual)
    {
        Assert.Equal(expected.GetType(), actual.GetType());
        Assert.Equal(
            JsonSerializer.Serialize(expected, expected.GetType(), ProtocolWireOptions.V1),
            JsonSerializer.Serialize(actual, actual.GetType(), ProtocolWireOptions.V1));
        if (expected is ClientHelloMessage or ClientJoinRequestMessage or ClientWorldResyncRequestMessage
            or ServerHelloMessage or JoinRejectedMessage or PlayerCommandMessage or PlayerActionMessage
            or ServerActionMessage { Result.Payload: not InventoryChangedResult })
        {
            Assert.Equal(expected, actual);
        }
    }

    private static DecodedProtocolMessage DecodeJson(string json)
    {
        return new TransportJsonCodec().Decode(Encoding.UTF8.GetBytes(json));
    }

    private static string EncodePayloadJson(IProtocolMessage message)
    {
        return Encoding.UTF8.GetString(EncodePayload(message));
    }

    private static byte[] EncodePayload(IProtocolMessage message)
    {
        TransportJsonCodec codec = new();
        ArrayBufferWriter<byte> buffer = new();
        switch (message)
        {
            case ClientHelloMessage typed: codec.EncodePayload(typed, buffer); break;
            case ClientJoinRequestMessage typed: codec.EncodePayload(typed, buffer); break;
            case PlayerCommandMessage typed: codec.EncodePayload(typed, buffer); break;
            case PlayerActionMessage typed: codec.EncodePayload(typed, buffer); break;
            case ClientWorldResyncRequestMessage typed: codec.EncodePayload(typed, buffer); break;
            case ServerHelloMessage typed: codec.EncodePayload(typed, buffer); break;
            case JoinAcceptedMessage typed: codec.EncodePayload(typed, buffer); break;
            case JoinRejectedMessage typed: codec.EncodePayload(typed, buffer); break;
            case ServerSnapshotMessage typed: codec.EncodePayload(typed, buffer); break;
            case ServerActionMessage typed: codec.EncodePayload(typed, buffer); break;
            default: throw new InvalidOperationException($"No encoder case for {message.GetType().Name}.");
        }

        return buffer.WrittenSpan.ToArray();
    }

    private static byte[] Encode(TransportJsonCodec codec, TransportEnvelope envelope, IProtocolMessage message)
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

    private static ActorSnapshot[] CreateFixtureActors()
    {
        ActorSnapshot[] actors = new ActorSnapshot[24];
        for (int i = 0; i < 4; i++)
        {
            actors[i] = new ActorSnapshot(
                100 + i,
                CreatureKind.Player,
                $"session-{i}",
                new Vector3(i * 2.5f, 1.75f, -i * 1.5f),
                new Vector3(0.6f, 0f, -0.8f),
                95 - i,
                100,
                true,
                PlayerInteractionMode.Combat,
                new PlayerCombatStatsSnapshot(12 + i, 1.5f, 10),
                $"Player {i}",
                new PlayerProgressionSnapshot(3, 120 + i, 400),
                EnemyBehaviorState: null);
        }

        for (int i = 0; i < 20; i++)
        {
            actors[4 + i] = new ActorSnapshot(
                2000 + i,
                CreatureKind.Enemy,
                null,
                new Vector3(10.125f + i, 1.5f, -4.375f - i * 0.5f),
                new Vector3(-0.70710677f, 0f, 0.70710677f),
                40 - i % 7,
                40,
                true,
                InteractionMode: null,
                CombatStats: null,
                DisplayName: "Wolf",
                Progression: null,
                EnemyBehaviorState: i % 2 == 0 ? EnemyBehaviorState.Chasing : EnemyBehaviorState.Idle);
        }

        return actors;
    }

    private static ServerSnapshotEnvelope CreateFixtureSnapshot()
    {
        Dictionary<string, ulong> sequences = new();
        for (int i = 0; i < 4; i++)
        {
            sequences[$"session-{i}"] = (ulong)(1000 + i);
        }

        return new ServerSnapshotEnvelope(
            48213,
            sequences,
            CreateFixtureActors(),
            PlayerInteractionMode.Combat,
            null,
            [
                new WorldMutationDelta(WorldMutationKind.CubePlaced, new Vector3(3f, 1f, 4f), MaterialId: GeometryMaterialId.Stone, Sequence: 801, ActorId: 101),
                new WorldMutationDelta(WorldMutationKind.CubeDestroyed, new Vector3(5f, 0f, 2f), Sequence: 802),
                WorldMutationDelta.Moved(new Vector3(6f, 2f, 6f), new Vector3(6.5f, 1.5f, 6.5f)) with { Sequence = 803 },
                WorldMutationDelta.Fractured(
                    new Vector3(7f, 0f, 7f),
                    GeometryMaterialId.Dirt,
                    [new GeometryFracturePiece(new Vector3(7.25f, 0.25f, 7.25f), 0.5f)]) with { Sequence = 804 }
            ],
            [new DamageEventDelta(2003, CreatureKind.Enemy, 7, false, 33, 40)]);
    }

    private static JoinAcceptedMessage CreateFixtureJoin()
    {
        return new JoinAcceptedMessage(
            "session-0",
            100,
            48213,
            // Protocol v3 carries one consistent world: the placed cubes with their cells.
            InitialWorld: new WorldSnapshot(
                16,
                8,
                16,
                [Vector3.Zero, Vector3.UnitX],
                FracturedCubeCells: [],
                AuthoredSubcellCells: [],
                PlacedCubes: [new PlacedCubeSnapshot(Vector3.Zero, new Vector3(0.5f)), new PlacedCubeSnapshot(Vector3.UnitX, new Vector3(1.5f, 0.5f, 0.5f))],
                FracturedCubes: [],
                MutationSequence: 804),
            Roster:
            [
                new PlayerRosterEntry("session-0", 100, "Player 0"),
                new PlayerRosterEntry("session-1", 101, "Player 1"),
                new PlayerRosterEntry("session-2", 102, "Player 2"),
                new PlayerRosterEntry("session-3", 103, "Player 3")
            ],
            Inventory: PlayerInventorySnapshot.Empty("Player 0"),
            InitialActors: CreateFixtureActors(),
            InitialWorldMutationSequence: 804);
    }
}
