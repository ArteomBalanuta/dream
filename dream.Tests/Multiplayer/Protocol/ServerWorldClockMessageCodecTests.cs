using System.Text;
using Xunit;
using Dream.Multiplayer.Protocol;
using Dream.Simulation.World;

namespace Dream.Tests.Multiplayer.Protocol;

// Protocol v23: the server.world-clock message.
public class ServerWorldClockMessageCodecTests
{
    private static readonly WorldClockAnchor Anchor = new(ServerTick: 1234, WorldTicks: 98765, TicksPerDay: 115200, StartOffsetTicks: 38400);

    private const string Golden =
        """{"protocolVersion":23,"messageKind":"server.world-clock","sequence":1,"payload":{"anchor":{"serverTick":1234,"worldTicks":98765,"ticksPerDay":115200,"startOffsetTicks":38400}}}""";

    [Fact]
    public void Encodes_TheGoldenJson_ByteExactly()
    {
        ServerWorldClockMessage message = new(Anchor);

        byte[] bytes = new TransportJsonCodec().Encode(
            new TransportEnvelope(TransportEnvelope.CurrentProtocolVersion, message.MessageKind, 1),
            message);

        Assert.Equal(23, TransportEnvelope.CurrentProtocolVersion);
        Assert.Equal(Golden, Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public void RoundTrips_ThroughTheCodec()
    {
        ServerWorldClockMessage decoded = Assert.IsType<ServerWorldClockMessage>(
            new TransportJsonCodec().Decode(Encoding.UTF8.GetBytes(Golden)).Message);

        Assert.Equal(Anchor, decoded.Anchor);
    }

    [Fact]
    public void IsServerOnly_AndAClientFrameOfItIsRefused()
    {
        Assert.True(TransportJsonCodec.IsServerOnlyKind(ServerWorldClockMessage.Kind));
        Assert.ThrowsAny<Exception>(() => new TransportJsonCodec().DecodeFromClient(Encoding.UTF8.GetBytes(Golden)));
    }
}
