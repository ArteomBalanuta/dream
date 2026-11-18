using System.Text;
using Xunit;
using Dream.Multiplayer.Protocol;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;

namespace Dream.Tests.Multiplayer.Protocol;

// T1b, protocol v5: the world clock anchor rides on join-accepted, and only there.
public class WorldClockAnchorProtocolTests
{
    private static readonly WorldClockAnchor Anchor = new(ServerTick: 1234, WorldTicks: 98765, TicksPerDay: 115200, StartOffsetTicks: 38400);

    [Fact]
    public void JoinAccepted_WritesTheAnchor_AsItsLastField_WithTheContractShape()
    {
        JoinAcceptedMessage join = new("session-a", 101, 1234, InitialWorld: null, WorldClock: Anchor);

        string json = EncodeToJson(join);

        Assert.Equal(
            """{"protocolVersion":22,"messageKind":"server.join-accepted","sequence":1,"payload":{"sessionId":"session-a","actorId":101,"currentServerTick":1234,"worldClock":{"serverTick":1234,"worldTicks":98765,"ticksPerDay":115200,"startOffsetTicks":38400}}}""",
            json);
    }

    [Fact]
    public void JoinAccepted_WithoutAnAnchor_LeavesTheFieldOut()
    {
        JoinAcceptedMessage join = new("session-a", 101, 1234, InitialWorld: null);

        string json = EncodeToJson(join);

        Assert.DoesNotContain("worldClock", json, StringComparison.Ordinal);
        Assert.Null(Assert.IsType<JoinAcceptedMessage>(Decode(json)).WorldClock);
    }

    [Fact]
    public void JoinAccepted_Anchor_RoundTripsThroughTheCodec_WithExtremeValues()
    {
        WorldClockAnchor extreme = new(ulong.MaxValue, long.MaxValue, 24, long.MinValue);
        JoinAcceptedMessage join = new("session-a", 101, ulong.MaxValue, InitialWorld: null, WorldClock: extreme);

        JoinAcceptedMessage decoded = Assert.IsType<JoinAcceptedMessage>(Decode(EncodeToJson(join)));

        Assert.Equal(extreme, decoded.WorldClock);
    }

    // A chunked join: the carrier keeps the anchor (WorldTransfer.ToCarrier uses `with`), and the
    // reassembled message the client applies has it next to the world.
    [Fact]
    public void ChunkedJoin_KeepsTheAnchor_OnTheCarrierAndAfterReassembly()
    {
        using RuntimeAuthoritativeSessionHost server = RuntimeAuthoritativeSessionHost.CreateDefault();
        JoinAcceptedMessage join = server.JoinSession("session-a", "Player A");
        Assert.NotNull(join.InitialWorld);
        WorldClockAnchor expected = server.Runtime.WorldClock.CaptureAnchor(server.Runtime.CurrentServerTick);
        Assert.Equal(expected, join.WorldClock);

        TransportJsonCodec codec = new();
        Assert.True(WorldTransfer.TryGetWorld(join, out WorldSnapshot world));
        JoinAcceptedMessage carrier = (JoinAcceptedMessage)WorldTransfer.ToCarrier(join, transferId: 7);
        byte[] carrierBytes = codec.Encode(new TransportEnvelope(TransportEnvelope.CurrentProtocolVersion, carrier.MessageKind, 1), carrier);
        Assert.Contains("\"worldClock\":{", Encoding.UTF8.GetString(carrierBytes), StringComparison.Ordinal);

        JoinAcceptedMessage decodedCarrier = Assert.IsType<JoinAcceptedMessage>(codec.Decode(carrierBytes).Message);
        Assert.Equal(7, decodedCarrier.WorldTransferId);
        Assert.Equal(expected, decodedCarrier.WorldClock);

        JoinAcceptedMessage reassembled = Assert.IsType<JoinAcceptedMessage>(WorldTransfer.WithWorld(decodedCarrier, world));
        Assert.Null(reassembled.WorldTransferId);
        Assert.NotNull(reassembled.InitialWorld);
        Assert.Equal(expected, reassembled.WorldClock);
    }

    // A real join through the server, sent as protocol v5 frames (carrier plus world chunks) and
    // reassembled the way the client does.
    [Fact]
    public void ServerJoin_OverTheWire_DeliversTheAnchorToTheReassembledJoin()
    {
        using RuntimeAuthoritativeSessionHost server = RuntimeAuthoritativeSessionHost.CreateDefault();
        JoinAcceptedMessage join = server.JoinSession("session-a", "Player A");
        Assert.True(WorldTransfer.TryGetWorld(join, out WorldSnapshot world));

        TransportJsonCodec codec = new();
        JoinAcceptedMessage carrier = (JoinAcceptedMessage)WorldTransfer.ToCarrier(join, transferId: 3);
        JoinAcceptedMessage decodedCarrier = (JoinAcceptedMessage)codec.Decode(
            codec.Encode(new TransportEnvelope(TransportEnvelope.CurrentProtocolVersion, carrier.MessageKind, 1), carrier)).Message;
        JoinAcceptedMessage delivered = Assert.IsType<JoinAcceptedMessage>(WorldTransfer.WithWorld(decodedCarrier, world));

        Assert.Equal(join.WorldClock, delivered.WorldClock);
        Assert.Equal(server.Runtime.WorldClock.Settings.TicksPerDay, delivered.WorldClock!.Value.TicksPerDay);
        Assert.Equal(server.Runtime.WorldClock.Settings.StartOffsetTicks, delivered.WorldClock.Value.StartOffsetTicks);
        Assert.Equal(join.CurrentServerTick, delivered.WorldClock.Value.ServerTick);
    }

    // No snapshot carries the clock: the anchor is a join-accepted field only.
    [Fact]
    public void OnlyJoinAccepted_HasAWorldClockField()
    {
        Type[] messageTypes = typeof(JoinAcceptedMessage).Assembly.GetTypes()
            .Where(type => typeof(IServerToClientMessage).IsAssignableFrom(type) && !type.IsAbstract && !type.IsInterface)
            .ToArray();
        Assert.NotEmpty(messageTypes);

        Type[] withClock = messageTypes
            .Where(type => type.GetProperties().Any(property =>
                property.PropertyType == typeof(WorldClockAnchor) || property.PropertyType == typeof(WorldClockAnchor?)))
            .ToArray();

        Assert.Equal([typeof(JoinAcceptedMessage)], withClock);
        Assert.DoesNotContain(
            typeof(ServerSnapshotEnvelope).GetProperties(),
            property => property.PropertyType == typeof(WorldClockAnchor) || property.PropertyType == typeof(WorldClockAnchor?));
    }

    private static string EncodeToJson(JoinAcceptedMessage message)
    {
        byte[] bytes = new TransportJsonCodec().Encode(
            new TransportEnvelope(TransportEnvelope.CurrentProtocolVersion, message.MessageKind, 1),
            message);
        return Encoding.UTF8.GetString(bytes);
    }

    private static IProtocolMessage Decode(string json) => new TransportJsonCodec().Decode(Encoding.UTF8.GetBytes(json)).Message;
}
