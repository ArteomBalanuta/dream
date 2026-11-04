using System.Buffers;
using System.Text;
using Xunit;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Players;

namespace Dream.Tests.Multiplayer.Protocol;

// The sprint tier is a held wire field on PlayerMoveIntent. It is written on every command, and
// read as optional: the protocol is JSON, so a payload from a client that predates the field just
// omits the key and decodes as "not sprinting" with no version bump.
public class SprintHeldWireTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SprintHeld_RoundTripsThroughTheCodec(bool sprintHeld)
    {
        TransportJsonCodec codec = new();
        PlayerCommandFrame sent = new(
            new PlayerSessionId("s-1"),
            7,
            PlayerModeSwitchIntent.None,
            new PlayerMoveIntent(1f, 0.5f, 0f, false, false, false, SprintHeld: sprintHeld),
            PlayerLookIntent.None,
            PrimaryUsePressed: false);

        ArrayBufferWriter<byte> payload = new();
        codec.EncodePayload(new PlayerCommandMessage(sent), payload);
        string json = Encoding.UTF8.GetString(payload.WrittenSpan);

        // Written always, so a server can tell "not sprinting" from "did not say".
        Assert.Contains("\"sprintHeld\":" + (sprintHeld ? "true" : "false"), json);

        PlayerCommandMessage decoded = Assert.IsType<PlayerCommandMessage>(Decode(codec, json));
        Assert.Equal(sprintHeld, decoded.CommandFrame.MoveIntent.SprintHeld);
        Assert.Equal(sent, decoded.CommandFrame);
    }

    [Fact]
    public void MoveIntentWithoutTheSprintKey_ReadsAsNotSprinting()
    {
        TransportJsonCodec codec = new();

        PlayerCommandMessage decoded = Assert.IsType<PlayerCommandMessage>(Decode(
            codec,
            """
            {"commandFrame":{"sessionId":{"value":"s-1"},"sequence":3,"requestedMode":{},
             "moveIntent":{"forwardAxis":1,"strafeAxis":0,"verticalAxis":0,"jumpPressed":false,
                           "freezePressed":false,"toggleNoclipPressed":false},
             "lookIntent":{"yawDelta":0,"pitchDelta":0},"primaryUsePressed":false}}
            """));

        Assert.False(decoded.CommandFrame.MoveIntent.SprintHeld);
        // The rest of the intent still decodes, so the absent key is read as a default and not as
        // a malformed object.
        Assert.Equal(1f, decoded.CommandFrame.MoveIntent.ForwardAxis);
    }

    private static IProtocolMessage Decode(TransportJsonCodec codec, string payload)
    {
        string json = "{\"protocolVersion\":" + TransportEnvelope.CurrentProtocolVersion
            + ",\"messageKind\":\"player.command\",\"sequence\":1,\"payload\":" + payload + "}";
        return codec.Decode(Encoding.UTF8.GetBytes(json)).Message;
    }
}
