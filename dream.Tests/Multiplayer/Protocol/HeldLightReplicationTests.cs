using System.Buffers;
using System.Text;
using System.Text.Json;
using OpenTK.Mathematics;
using Xunit;
using Dream.Client.Presentation;
using Dream.Client.Replication;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Transport;
using Dream.Server;
using Dream.Simulation.Combat;
using Dream.Simulation.Enemies;
using Dream.Simulation.Inventory;
using Dream.Simulation.Players;
using Dream.Simulation.World;
using static Dream.Tests.Server.ServerConnectionTestFixtures;

namespace Dream.Tests.Multiplayer.Protocol;

// T2f, protocol v6: a player actor's held light (ActorSnapshot.HeldLight) on the wire. In a
// delta-coded frame it is the Equipment group (mask bit 256), which Full does not imply: a Full
// record carries it only when the light is not None, a delta record only when it changed. In the
// legacy full actor list it is "heldLight", left out when None. These tests pin both shapes, that
// the hand-written converters agree with the reflection contract, and that a torch survives
// keyframes, deltas, per-connection rebased deltas and the client's ActorStateCache all the way
// to RemotePlayerPresentationState.
public sealed class HeldLightReplicationTests
{
    private static readonly JsonSerializerOptions Reference = ProtocolWireOptions.V3;
    private static readonly TimeSpan FlushDeadline = TimeSpan.FromSeconds(30);

    [Fact]
    public void FullRecord_CarriesEquipment_OnlyWhenALightIsHeld()
    {
        ActorSnapshot dark = Player(7, "session-a", Vector3.Zero);
        ActorSnapshot lit = dark with { HeldLight = HeldLightKind.Torch };

        Assert.Equal(ActorFieldGroups.All, ActorWireRecord.Full(dark).Groups);
        Assert.Equal(ActorFieldGroups.All | ActorFieldGroups.Equipment, ActorWireRecord.Full(lit).Groups);
        Assert.Equal(255, (int)ActorFieldGroups.All);
        Assert.Equal(256, (int)ActorFieldGroups.Equipment);
        Assert.Equal(0, (int)(ActorFieldGroups.All & ActorFieldGroups.Equipment));

        string darkJson = WriteRecord(ActorWireRecord.Full(dark));
        string litJson = WriteRecord(ActorWireRecord.Full(lit));
        Assert.StartsWith("[7,255,", darkJson, StringComparison.Ordinal);
        Assert.StartsWith("[7,511,", litJson, StringComparison.Ordinal);
        // The held light is the last field, after starvedTicks.
        Assert.Equal("[7,511," + darkJson["[7,255,".Length..^1] + ",1]", litJson);
    }

    [Fact]
    public void DeltaRecord_CarriesEquipment_OnlyWhenTheLightChanged()
    {
        ActorSnapshot dark = Player(7, "session-a", Vector3.Zero);
        ActorSnapshot torch = dark with { HeldLight = HeldLightKind.Torch };
        ActorSnapshot lantern = dark with { HeldLight = HeldLightKind.Lantern };

        Assert.Equal(ActorFieldGroups.None, ActorWireRecord.Diff(dark, dark).Groups);
        Assert.Equal(ActorFieldGroups.None, ActorWireRecord.Diff(torch, torch).Groups);
        Assert.Equal("[7,256,1]", WriteRecord(ActorWireRecord.Diff(torch, dark)));
        Assert.Equal("[7,256,2]", WriteRecord(ActorWireRecord.Diff(lantern, torch)));
        Assert.Equal("[7,256,0]", WriteRecord(ActorWireRecord.Diff(dark, lantern)));
        // A move and a light switch together.
        Assert.Equal("[7,258,4096,0,0,1]", WriteRecord(ActorWireRecord.Diff(torch with { Position = Vector3.UnitX }, dark)));
    }

    [Fact]
    public void Frames_RoundTrip_ThroughTheCodec_AndReEncodeByteForByte()
    {
        ActorSnapshot[] first =
        [
            Player(1, "session-a", Vector3.Zero) with { HeldLight = HeldLightKind.Torch },
            Player(2, "session-b", Vector3.UnitZ),
            Wolf(50)
        ];
        ActorSnapshot[] second =
        [
            first[0] with { HeldLight = HeldLightKind.None },
            first[1] with { HeldLight = HeldLightKind.Lantern, Position = new Vector3(0f, 0f, 2f) },
            first[2]
        ];
        ActorSnapshot[] third =
        [
            second[0],
            second[1],
            second[2],
            Player(3, "session-c", Vector3.UnitY) with { HeldLight = HeldLightKind.Torch }
        ];

        ActorDeltaChain chain = new();
        TransportJsonCodec codec = new();
        ActorStateCache cache = new();
        ulong tick = 2;
        foreach (ActorSnapshot[] actors in new[] { first, second, third })
        {
            ServerSnapshotMessage sent = new(Envelope(tick, actors) with { ActorDelta = chain.Next(tick, actors) });
            byte[] payload = EncodePayload(codec, sent);
            ServerSnapshotMessage decoded = DecodePayload(codec, payload);
            Assert.Equal(payload, EncodePayload(codec, decoded));
            ServerSnapshotMessage shown = cache.Apply(decoded);
            Assert.Equal(actors.Select(Quantized), shown.Snapshot.Actors);
            tick += 2;
        }

        Assert.Equal(1, cache.KeyframesApplied);
        Assert.Equal(2, cache.DeltaFramesApplied);
        Assert.Equal(0, cache.ChainBreaks);
    }

    // The keyframe's actors are materialized straight from the records by the decoder: a Full
    // record without Equipment is None, one with it carries the light.
    [Fact]
    public void Keyframe_MaterializesTheHeldLight()
    {
        ActorSnapshot[] actors =
        [
            Player(1, "session-a", Vector3.Zero) with { HeldLight = HeldLightKind.Lantern },
            Player(2, "session-b", Vector3.Zero)
        ];
        TransportJsonCodec codec = new();
        ServerSnapshotMessage sent = new(Envelope(2, actors) with { ActorDelta = ActorDeltaContext.CreateKeyframe(1) });
        ServerSnapshotMessage decoded = DecodePayload(codec, EncodePayload(codec, sent));

        ActorWireFrame frame = decoded.Snapshot.ActorFrame!;
        Assert.True(frame.IsKeyframe);
        Assert.Equal(ActorFieldGroups.All | ActorFieldGroups.Equipment, frame.Records[0].Groups);
        Assert.Equal(ActorFieldGroups.All, frame.Records[1].Groups);
        Assert.Equal(HeldLightKind.Lantern, decoded.Snapshot.Actors[0].HeldLight);
        Assert.Equal(HeldLightKind.None, decoded.Snapshot.Actors[1].HeldLight);
        Assert.Equal(actors.Select(Quantized), new ActorStateCache().Apply(decoded).Snapshot.Actors);
    }

    // The legacy full actor list (a resync, an envelope outside the chain) and the standalone frame
    // converter write what the reflection contract writes, held light present or not.
    [Fact]
    public void HandWrittenConverters_MatchTheReflectionContract()
    {
        ActorSnapshot[] actors =
        [
            Player(1, "session-a", new Vector3(1.5f, 2f, -3.25f)) with { HeldLight = HeldLightKind.Torch, StarvedTicks = 2 },
            Player(2, "session-b", Vector3.Zero) with { HeldLight = HeldLightKind.Lantern },
            Player(3, "session-c", Vector3.Zero),
            Wolf(50)
        ];
        ServerSnapshotMessage message = new(Envelope(9, actors));

        byte[] expected = JsonSerializer.SerializeToUtf8Bytes(message, Reference);
        ArrayBufferWriter<byte> payload = new();
        new TransportJsonCodec().EncodePayload(message, payload);
        string json = Encoding.UTF8.GetString(payload.WrittenSpan);
        Assert.Equal(Encoding.UTF8.GetString(expected), json);
        Assert.Contains("\"starvedTicks\":2,\"heldLight\":1}", json, StringComparison.Ordinal);
        Assert.Contains("\"heldLight\":2}", json, StringComparison.Ordinal);
        Assert.Equal(2, CountOccurrences(json, "heldLight"));

        ServerSnapshotMessage decoded = DecodePayload(new TransportJsonCodec(), payload.WrittenSpan.ToArray());
        Assert.Equal(actors, decoded.Snapshot.Actors);
        ServerSnapshotMessage reflected = JsonSerializer.Deserialize<ServerSnapshotMessage>(expected, Reference)!;
        Assert.Equal(actors, reflected.Snapshot.Actors);

        // The frame converter plugged into the reflection serializer writes the codec's frame.
        ActorDeltaChain chain = new();
        ServerSnapshotMessage framed = new(Envelope(9, actors) with { ActorDelta = chain.Next(9, actors) });
        ServerSnapshotMessage framedDecoded = DecodePayload(new TransportJsonCodec(), EncodePayload(new TransportJsonCodec(), framed));
        JsonSerializerOptions frameOptions = new(Reference);
        frameOptions.Converters.Add(new ActorWireFrameJsonConverter());
        string frameJson = JsonSerializer.Serialize(framedDecoded.Snapshot.ActorFrame!, frameOptions);
        Assert.Contains("[1,511,", frameJson, StringComparison.Ordinal);
        Assert.Contains("[3,255,", frameJson, StringComparison.Ordinal);
        ActorWireFrame reread = JsonSerializer.Deserialize<ActorWireFrame>(frameJson, frameOptions)!;
        Assert.Equal(framedDecoded.Snapshot.ActorFrame!.Records, reread.Records);
    }

    // With no light anywhere the bytes are exactly what protocol v5 wrote: no heldLight property,
    // no Equipment bit, every Full record still 255.
    [Fact]
    public void NoLightHeld_WritesNoTraceOfTheField()
    {
        ActorSnapshot[] actors = [Player(1, "session-a", Vector3.Zero), Player(2, "session-b", Vector3.UnitX), Wolf(50)];
        TransportJsonCodec codec = new();
        string legacy = Encoding.UTF8.GetString(EncodePayload(codec, new ServerSnapshotMessage(Envelope(2, actors))));
        Assert.DoesNotContain("heldLight", legacy, StringComparison.Ordinal);

        ActorDeltaChain chain = new();
        string keyframe = Encoding.UTF8.GetString(EncodePayload(codec, new ServerSnapshotMessage(Envelope(2, actors) with { ActorDelta = chain.Next(2, actors) })));
        Assert.DoesNotContain(",511,", keyframe, StringComparison.Ordinal);
        Assert.Equal(3, CountOccurrences(keyframe, ",255,"));
    }

    [Theory]
    // A mask bit past Equipment.
    [InlineData("{\"seq\":1,\"actors\":[[1,767,0,null,null,0,0,0,0,0,0,0,0,1,null,null,null,null,0,1,0]]}")]
    // A Full record must still carry every group below Equipment.
    [InlineData("{\"seq\":1,\"actors\":[[1,257,0,null,null,1]]}")]
    // A held light outside the byte range.
    [InlineData("{\"seq\":2,\"base\":1,\"actors\":[[1,256,256]]}")]
    [InlineData("{\"seq\":2,\"base\":1,\"actors\":[[1,256,-1]]}")]
    // The Equipment group without its field.
    [InlineData("{\"seq\":2,\"base\":1,\"actors\":[[1,256]]}")]
    public void MangledEquipmentRecords_FailWithJsonException(string frame)
    {
        string payload = $"{{\"snapshot\":{{\"serverTick\":1,\"actorFrame\":{frame},\"interactionMode\":0}}}}";
        byte[] bytes = Encoding.UTF8.GetBytes(payload);
        Assert.ThrowsAny<JsonException>(() => DecodePayload(new TransportJsonCodec(), bytes));
    }

    [Fact]
    public void LegacyActorList_HeldLightOutOfRange_FailsWithJsonException()
    {
        byte[] bytes = Encoding.UTF8.GetBytes(
            "{\"snapshot\":{\"serverTick\":1,\"actors\":[{\"actorId\":1,\"kind\":0,\"position\":{\"X\":0,\"Y\":0,\"Z\":0},\"facing\":{\"X\":0,\"Y\":0,\"Z\":1},\"currentHealth\":1,\"maxHealth\":1,\"isAlive\":true,\"heldLight\":300}],\"interactionMode\":0}}");
        Assert.ThrowsAny<JsonException>(() => DecodePayload(new TransportJsonCodec(), bytes));
    }

    // A frame lost on the way: the cache cannot vouch for a light switched in the lost frame, so
    // the actor is withheld until the next keyframe, which brings the light back.
    [Fact]
    public void ChainBreak_WithholdsTheActor_UntilTheKeyframeBringsTheLightBack()
    {
        ActorDeltaChain chain = new();
        TransportJsonCodec codec = new();
        ActorStateCache cache = new();
        ActorSnapshot dark = Player(1, "session-a", Vector3.Zero);
        ActorSnapshot lit = dark with { HeldLight = HeldLightKind.Torch };

        cache.Apply(DecodePayload(codec, EncodePayload(codec, new ServerSnapshotMessage(Envelope(2, [dark]) with { ActorDelta = chain.Next(2, [dark]) }))));
        // Lost: the frame that switched the torch on.
        chain.Next(4, [lit]);
        ServerSnapshotMessage afterLoss = cache.Apply(DecodePayload(codec, EncodePayload(codec, new ServerSnapshotMessage(Envelope(6, [lit]) with { ActorDelta = chain.Next(6, [lit]) }))));
        Assert.Empty(afterLoss.Snapshot.Actors);
        Assert.Equal(1, cache.ChainBreaks);

        ulong keyframeTick = 2 + ActorDeltaContext.KeyframeIntervalTicks;
        ServerSnapshotMessage recovered = cache.Apply(DecodePayload(codec, EncodePayload(codec, new ServerSnapshotMessage(Envelope(keyframeTick, [lit]) with { ActorDelta = chain.Next(keyframeTick, [lit]) }))));
        Assert.True(recovered.Snapshot.ActorFrame!.IsKeyframe);
        Assert.Equal(HeldLightKind.Torch, Assert.Single(recovered.Snapshot.Actors).HeldLight);
    }

    // Over a real ServerConnection whose writer stalls, so broadcasts merge and the connection
    // re-diffs each merged frame against the frame its client holds (RebaseActorFrame): a light
    // switched on and off again inside the merged span, or switched in a skipped frame, still
    // reaches the client's presenter as the server's latest state.
    [Fact]
    public async Task MergedAndRebasedDeltas_DeliverTheLatestLight_ToTheRemotePlayerPresentation()
    {
        TransportJsonCodec codec = new();
        LengthPrefixedMessageFramer framer = new();
        DedicatedServerHost host = CreateHost(new MutatingAuthority());
        GatedTransport transport = new("gated");
        await using ServerConnection connection = new(transport, host, codec, framer, WithoutLiveness);
        host.HandleClientMessage(transport.ConnectionId, new ClientJoinRequestMessage("session-b", "observer"));

        ActorDeltaChain chain = new();
        Dictionary<ulong, ActorSnapshot[]> serverActors = [];
        HeldLightKind[] lights = [HeldLightKind.None, HeldLightKind.Torch, HeldLightKind.Lantern];
        for (ulong tick = 2; tick <= 200; tick += 2)
        {
            if (tick % 16 == 0)
            {
                transport.Close();
            }

            ActorSnapshot[] actors =
            [
                Player(1, "session-a", new Vector3(tick * 0.01f, 0f, 0f)) with { HeldLight = lights[(int)(tick / 6 % 3)] },
                Player(2, "session-b", Vector3.Zero) with { HeldLight = tick % 32 < 16 ? HeldLightKind.Torch : HeldLightKind.None },
                Wolf(50)
            ];
            ActorDeltaContext delta = chain.Next(tick, actors);
            serverActors[delta.Sequence] = actors;
            ServerSnapshotMessage message = new(Envelope(tick, actors) with { ActorDelta = delta });
            connection.EnqueueSnapshot(message, new SharedEncodedPayload(codec, message));
            if (tick % 16 == 10 || tick == 2)
            {
                transport.Open();
                Assert.True(await connection.FlushOutboundAsync(FlushDeadline));
            }
        }

        transport.Open();
        Assert.True(await connection.FlushOutboundAsync(FlushDeadline));
        Assert.True(connection.ActorDeltasRebased > 0, $"{connection.ActorDeltasRebased} rebased deltas");

        ActorStateCache cache = new();
        ClientScenePresenter presenter = new("session-b");
        int torchesSeen = 0;
        int statesSeen = 0;
        foreach (byte[] frame in transport.Frames)
        {
            Assert.True(framer.TryDeframe(frame, out byte[]? payload, out _));
            if (codec.Decode(payload!).Message is not ServerSnapshotMessage decoded)
            {
                continue;
            }

            ServerSnapshotMessage shown = cache.Apply(decoded);
            ActorSnapshot[] expected = serverActors[shown.Snapshot.ActorFrame!.Sequence];
            Assert.Equal(expected.Select(Quantized), shown.Snapshot.Actors);
            presenter.ApplySnapshot(shown.Snapshot);
            IReadOnlyList<RemotePlayerPresentationState> remotes = presenter.BuildRemotePlayerStates(shown.Snapshot.ServerTick);
            if (remotes.Count == 0)
            {
                // The interpolation buffer may need a second sample before it shows anyone.
                Assert.Equal(0, statesSeen);
                continue;
            }

            statesSeen++;
            RemotePlayerPresentationState remote = Assert.Single(remotes);
            Assert.Equal(1, remote.ActorId);
            Assert.Equal(expected[0].HeldLight, remote.HeldLight);
            if (remote.HeldLight == HeldLightKind.Torch)
            {
                torchesSeen++;
            }
        }

        Assert.True(statesSeen > 10, $"{statesSeen} presented snapshots");
        Assert.True(torchesSeen > 0);
        Assert.Equal(0, cache.ChainBreaks);
        Assert.Equal(0, cache.StaleActorsWithheld);
    }

    private static ActorSnapshot Quantized(ActorSnapshot actor)
    {
        return actor with { Position = ActorQuantization.Round(actor.Position), Facing = ActorQuantization.Round(actor.Facing) };
    }

    private static ActorSnapshot Player(int id, string sessionId, Vector3 position)
    {
        return new ActorSnapshot(
            id,
            CreatureKind.Player,
            sessionId,
            position,
            -Vector3.UnitZ,
            100,
            100,
            true,
            PlayerInteractionMode.Building,
            new PlayerCombatStatsSnapshot(10, 2f, 10),
            $"name-{id}",
            new PlayerProgressionSnapshot(1, 0, 1000));
    }

    private static ActorSnapshot Wolf(int id)
    {
        return new ActorSnapshot(id, CreatureKind.Enemy, null, new Vector3(4f, 0f, 4f), Vector3.UnitX, 30, 30, true, DisplayName: "Wolf", EnemyBehaviorState: EnemyBehaviorState.Idle);
    }

    private static ServerSnapshotEnvelope Envelope(ulong tick, ActorSnapshot[] actors)
    {
        return new ServerSnapshotEnvelope(
            tick,
            new Dictionary<string, ulong>(),
            actors,
            PlayerInteractionMode.Building,
            null,
            Array.Empty<WorldMutationDelta>(),
            Array.Empty<DamageEventDelta>());
    }

    private static string WriteRecord(in ActorWireRecord record)
    {
        ArrayBufferWriter<byte> buffer = new();
        using (Utf8JsonWriter writer = new(buffer))
        {
            ActorFrameJson.WriteRecord(writer, record);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static byte[] EncodePayload(TransportJsonCodec codec, ServerSnapshotMessage message)
    {
        ArrayBufferWriter<byte> writer = new();
        codec.EncodePayload(message, writer);
        return writer.WrittenSpan.ToArray();
    }

    private static ServerSnapshotMessage DecodePayload(TransportJsonCodec codec, byte[] payload)
    {
        byte[] frame = Encoding.UTF8.GetBytes(
            "{\"protocolVersion\":" + TransportEnvelope.CurrentProtocolVersion + ",\"messageKind\":\"server.snapshot\",\"sequence\":1,\"payload\":" + Encoding.UTF8.GetString(payload) + "}");
        return Assert.IsType<ServerSnapshotMessage>(codec.Decode(frame).Message);
    }

    private static int CountOccurrences(string text, string value)
    {
        int count = 0;
        for (int index = text.IndexOf(value, StringComparison.Ordinal); index >= 0; index = text.IndexOf(value, index + 1, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
