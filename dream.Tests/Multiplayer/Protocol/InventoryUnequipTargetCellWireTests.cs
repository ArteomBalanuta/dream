using System.Text;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Xunit;

namespace Dream.Tests.Multiplayer.Protocol;

// Classic HUD iteration 2, C0a: InventoryUnequipIntent gains an optional TargetCell (the bag anchor to
// put the item on). With no cell the wire form must be byte-for-byte what it was before.
public sealed class InventoryUnequipTargetCellWireTests
{
    private static string Encode(IPlayerActionPayload payload)
    {
        TransportJsonCodec codec = new();
        PlayerActionMessage message = new(new PlayerSessionId("s"), 1, payload);
        return Encoding.UTF8.GetString(codec.Encode(
            new TransportEnvelope(TransportEnvelope.CurrentProtocolVersion, message.MessageKind, 1), message));
    }

    private static InventoryUnequipIntent RoundTrip(InventoryUnequipIntent payload)
    {
        TransportJsonCodec codec = new();
        PlayerActionMessage message = new(new PlayerSessionId("s"), 1, payload);
        DecodedProtocolMessage decoded = codec.Decode(codec.Encode(
            new TransportEnvelope(TransportEnvelope.CurrentProtocolVersion, message.MessageKind, 1), message));
        return Assert.IsType<InventoryUnequipIntent>(Assert.IsType<PlayerActionMessage>(decoded.Message).Payload);
    }

    [Fact]
    public void WithNoCell_TheFieldIsLeftOff_AndThePayloadEndsWhereItAlwaysDid()
    {
        string plain = Encode(new InventoryUnequipIntent(101));

        Assert.Contains("\"payload\":{\"$type\":\"inventory.unequip\",\"slotIndex\":101}", plain);
        Assert.DoesNotContain("targetCell", plain);
        Assert.Equal(plain, Encode(new InventoryUnequipIntent(101, null)));
    }

    [Fact]
    public void WithACell_ItIsWrittenAfterTheSlot()
    {
        Assert.Contains(
            "\"payload\":{\"$type\":\"inventory.unequip\",\"slotIndex\":104,\"targetCell\":0}",
            Encode(new InventoryUnequipIntent(104, 0)));
        Assert.Contains(
            "\"payload\":{\"$type\":\"inventory.unequip\",\"slotIndex\":104,\"targetCell\":63}",
            Encode(new InventoryUnequipIntent(104, 63)));
    }

    [Fact]
    public void BothShapes_RoundTrip_AndAnOldMessageDecodesWithNoCell()
    {
        Assert.Equal(new InventoryUnequipIntent(101), RoundTrip(new InventoryUnequipIntent(101)));
        Assert.Equal(new InventoryUnequipIntent(104, 0), RoundTrip(new InventoryUnequipIntent(104, 0)));
        Assert.Equal(new InventoryUnequipIntent(111, 63), RoundTrip(new InventoryUnequipIntent(111, 63)));
        Assert.Null(RoundTrip(new InventoryUnequipIntent(101)).TargetCell);
        Assert.Equal(0, RoundTrip(new InventoryUnequipIntent(104, 0)).TargetCell);
    }
}
