using System.Text;
using Xunit;
using Dream.Multiplayer.Protocol;
using Dream.Simulation.Combat;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;

namespace Dream.Tests.Multiplayer.Protocol;

// Persistence wave B3, protocol v7: the world descriptor rides on join-accepted, and snapshots are
// unchanged apart from the version digit of their envelope.
public class JoinAcceptedWorldDescriptorCodecTests
{
    private const string DescriptorJson =
        """{"id":1,"name":"default","width":56,"height":8,"depth":56,"mapSource":"generated","generatorKey":"default","generatorVersion":1}""";

    private static readonly TransportJsonCodec Codec = new();

    [Fact]
    public void JoinAccepted_WritesTheDescriptor_AsItsLastField_ByteExact()
    {
        JoinAcceptedMessage join = new(
            "session-a", 101, 1234, InitialWorld: null, World: WorldDescriptorWire.From(WorldDescriptor.Default));

        string json = Encode(join, 1);

        Assert.Equal(
            "{\"protocolVersion\":10,\"messageKind\":\"server.join-accepted\",\"sequence\":1,\"payload\":" +
            "{\"sessionId\":\"session-a\",\"actorId\":101,\"currentServerTick\":1234,\"world\":" + DescriptorJson + "}}",
            json);
    }

    [Fact]
    public void JoinAccepted_RoundTripsTheDescriptor_AndWithoutOneLeavesTheFieldOut()
    {
        WorldDescriptor small = WorldDescriptor.ForFloorSize(20, new WorldId(4), "small");
        JoinAcceptedMessage join = new("session-a", 101, 9, InitialWorld: null, World: WorldDescriptorWire.From(small));

        JoinAcceptedMessage decoded = Assert.IsType<JoinAcceptedMessage>(Codec.Decode(Codec.Encode(Envelope(join, 1), join)).Message);
        Assert.Equal(small, decoded.World!.ToDescriptor());

        JoinAcceptedMessage bare = new("session-a", 101, 9, InitialWorld: null);
        Assert.DoesNotContain("\"world\"", Encode(bare, 1), StringComparison.Ordinal);
        Assert.Null(Assert.IsType<JoinAcceptedMessage>(Codec.Decode(Codec.Encode(Envelope(bare, 1), bare)).Message).World);
    }

    [Fact]
    public void ChunkedJoin_KeepsTheDescriptor_OnTheCarrierAndAfterReassembly()
    {
        WorldDescriptorWire wire = WorldDescriptorWire.From(WorldDescriptor.Default);
        using RuntimeAuthoritativeSessionHost server = RuntimeAuthoritativeSessionHost.CreateDefault();
        JoinAcceptedMessage join = server.JoinSession("session-a", "Player A") with { World = wire };
        Assert.True(WorldTransfer.TryGetWorld(join, out WorldSnapshot world));

        JoinAcceptedMessage carrier = (JoinAcceptedMessage)WorldTransfer.ToCarrier(join, transferId: 7);
        byte[] carrierBytes = Codec.Encode(Envelope(carrier, 1), carrier);
        Assert.Contains(DescriptorJson, Encoding.UTF8.GetString(carrierBytes), StringComparison.Ordinal);

        JoinAcceptedMessage decodedCarrier = Assert.IsType<JoinAcceptedMessage>(Codec.Decode(carrierBytes).Message);
        Assert.Equal(wire, decodedCarrier.World);
        JoinAcceptedMessage reassembled = Assert.IsType<JoinAcceptedMessage>(WorldTransfer.WithWorld(decodedCarrier, world));
        Assert.Equal(wire, reassembled.World);
        Assert.NotNull(reassembled.InitialWorld);
    }

    [Fact]
    public void AV6Peer_IsRefused_ByDecodeAndEncode()
    {
        JoinAcceptedMessage join = new("session-a", 101, 1234, InitialWorld: null);
        string v6 = Encode(join, 1).Replace("\"protocolVersion\":10", "\"protocolVersion\":6", StringComparison.Ordinal);

        InvalidOperationException decodeError = Assert.Throws<InvalidOperationException>(() => Codec.Decode(Encoding.UTF8.GetBytes(v6)));
        Assert.Equal("Unsupported protocol version 6. Supported version: 10.", decodeError.Message);
        Assert.Throws<InvalidOperationException>(() => Codec.Encode(new TransportEnvelope(6, join.MessageKind, 1), join));

        Assert.Equal(
            "Server uses protocol v6; this client uses v10.",
            ProtocolVersionMismatchException.FromServer(6, decodeError).Message);
        Assert.Equal(
            "Client uses protocol v6; this server uses v10.",
            ProtocolVersionMismatchException.FromClient(6, decodeError).Message);
    }

    // The snapshot payload does not change in v7: pinned byte for byte, with only the version digit
    // of the envelope differing from what a v6 server wrote.
    [Fact]
    public void SnapshotBytes_AreIdentical_ApartFromTheVersionDigit()
    {
        ServerSnapshotMessage snapshot = new(new ServerSnapshotEnvelope(
            5,
            new Dictionary<string, ulong> { ["session-a"] = 3 },
            Array.Empty<ActorSnapshot>(),
            PlayerInteractionMode.Building,
            null,
            Array.Empty<WorldMutationDelta>(),
            Array.Empty<DamageEventDelta>()));

        string json = Encode(snapshot, 9);

        Assert.StartsWith("{\"protocolVersion\":10,", json, StringComparison.Ordinal);
        Assert.Equal(SnapshotGoldenV6.Replace("\"protocolVersion\":6", "\"protocolVersion\":10", StringComparison.Ordinal), json);
    }

    // What the v6 codec wrote for this snapshot: the payload code is untouched by v7.
    private const string SnapshotGoldenV6 = """{"protocolVersion":6,"messageKind":"server.snapshot","sequence":9,"payload":{"snapshot":{"serverTick":5,"lastAcceptedCommandSequences":{"session-a":3},"actors":[],"interactionMode":0,"worldMutations":[],"damageEvents":[],"worldChecksumSequence":0}}}""";

    private static TransportEnvelope Envelope<T>(T message, ulong sequence) where T : IServerToClientMessage =>
        new(TransportEnvelope.CurrentProtocolVersion, message.MessageKind, sequence);

    private static string Encode<T>(T message, ulong sequence) where T : IServerToClientMessage =>
        Encoding.UTF8.GetString(Codec.Encode(Envelope(message, sequence), message));
}
