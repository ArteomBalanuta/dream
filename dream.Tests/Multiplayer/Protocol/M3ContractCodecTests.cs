using System.Text;
using System.Text.Json;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Combat;
using Dream.Simulation.Inventory;
using Dream.Simulation.World;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Multiplayer.Protocol;

// M3 contract (protocol v16): the ground-items message, the four intents, the vendor result and the
// inventory gold field round trip, the server-only kind is refused from a client, and a snapshot of a
// player without gold has the bytes it had before.
public sealed class M3ContractCodecTests
{
    private static readonly TransportJsonCodec Codec = new();

    private static byte[] Encode<TMessage>(TMessage message, ulong sequence = 1) where TMessage : IProtocolMessage =>
        Codec.Encode(new TransportEnvelope(TransportEnvelope.CurrentProtocolVersion, message.MessageKind, sequence), message);

    [Fact]
    public void TheProtocolVersion_IsSeventeen_AndTheNpcIdBlockSitsInsideTheEnemyRange()
    {
        Assert.Equal(21, TransportEnvelope.CurrentProtocolVersion);
        Assert.Equal(3, (int)CreatureKind.Npc);
        Assert.Equal((1 << 30) - 1024, ReplicatedActorIdRanges.FirstNpcId);
        Assert.True(ReplicatedActorIdRanges.IsEnemyId(ReplicatedActorIdRanges.FirstNpcId));
        Assert.True(ReplicatedActorIdRanges.FirstNpcId + 1023 <= ReplicatedActorIdRanges.LastEnemyId);
    }

    [Fact]
    public void ServerGroundItems_RoundTrips_AndIsRefusedFromAClient()
    {
        ServerGroundItems original = new(
            Reset: true,
            [
                new GroundItemSnapshot(900, "gear.iron-blade", ItemRarity.Rare, new Vector3(1.5f, 2f, -3.25f), 0, 0),
                new GroundItemSnapshot(901, "tool.torch", ItemRarity.Common, new Vector3(4f, 5f, 6f), 7, 1200UL)
            ],
            [12L, 13L]);

        byte[] bytes = Encode(original);

        ServerGroundItems decoded = Assert.IsType<ServerGroundItems>(Codec.Decode(bytes).Message);
        Assert.True(decoded.Reset);
        Assert.Equal(original.Upserts, decoded.Upserts);
        Assert.Equal(original.RemovedIds, decoded.RemovedIds);
        Assert.Equal("server.ground-items", Codec.Decode(bytes).Envelope.MessageKind);
        Assert.True(TransportJsonCodec.IsServerOnlyKind(ServerGroundItems.Kind));
        Assert.Throws<InvalidOperationException>(() => Codec.DecodeFromClient(bytes));
    }

    [Fact]
    public void AnEmptyServerGroundItems_RoundTrips()
    {
        ServerGroundItems original = new(false, [], []);

        ServerGroundItems decoded = Assert.IsType<ServerGroundItems>(Codec.Decode(Encode(original)).Message);

        Assert.False(decoded.Reset);
        Assert.Empty(decoded.Upserts);
        Assert.Empty(decoded.RemovedIds);
    }

    public static TheoryData<IPlayerActionPayload, string> Intents => new()
    {
        { new GroundPickupIntent(900), "ground.pickup" },
        { new NpcInteractIntent(ReplicatedActorIdRanges.FirstNpcId), "npc.interact" },
        { new VendorBuyIntent(ReplicatedActorIdRanges.FirstNpcId, "tool.torch"), "vendor.buy" },
        { new VendorSellIntent(ReplicatedActorIdRanges.FirstNpcId, 3, 901), "vendor.sell" }
    };

    [Theory]
    [MemberData(nameof(Intents))]
    public void TheNewIntents_RideThePlayerActionFrame_FromAClient(IPlayerActionPayload payload, string discriminator)
    {
        PlayerActionMessage original = new(new PlayerSessionId("session-a"), 5, payload);

        byte[] bytes = Encode(original);

        Assert.Contains($"\"$type\":\"{discriminator}\"", Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
        DecodedProtocolMessage decoded = Codec.DecodeFromClient(bytes);
        Assert.Equal("player.action", decoded.Envelope.MessageKind);
        PlayerActionMessage message = Assert.IsType<PlayerActionMessage>(decoded.Message);
        Assert.Equal(payload, message.Payload);
    }

    [Fact]
    public void VendorOpenedResult_RoundTripsInAnActionResult()
    {
        VendorOpenedResult payload = new(
            ReplicatedActorIdRanges.FirstNpcId,
            "Mara the Trader",
            [new VendorStockEntry("tool.torch", 12), new VendorStockEntry("gear.leather-cap", 40)]);
        ServerActionMessage original = new(new AuthoritativeActionResult("s", 9, 100, Accepted: true, Payload: payload));

        byte[] bytes = Encode(original);

        Assert.Contains("\"$type\":\"vendor.opened\"", Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
        ServerActionMessage decoded = Assert.IsType<ServerActionMessage>(Codec.Decode(bytes).Message);
        VendorOpenedResult back = Assert.IsType<VendorOpenedResult>(decoded.Result.Payload);
        Assert.Equal(payload.NpcActorId, back.NpcActorId);
        Assert.Equal(payload.DisplayName, back.DisplayName);
        Assert.Equal(payload.Stock, back.Stock);
    }

    private static string InventoryJson(PlayerInventorySnapshot snapshot)
    {
        ServerActionMessage message = new(new AuthoritativeActionResult("s", 1, 1, Accepted: true, Payload: new InventoryChangedResult(snapshot)));
        return Encoding.UTF8.GetString(Encode(message));
    }

    [Fact]
    public void InventoryGold_IsWrittenOnlyAboveZero_AndRoundTrips()
    {
        PlayerInventorySnapshot none = PlayerInventorySnapshot.Empty("p");
        PlayerInventorySnapshot rich = none with { Gold = 1_234_567_890L };

        Assert.DoesNotContain("gold", InventoryJson(none), StringComparison.Ordinal);
        Assert.Contains("\"gold\":1234567890", InventoryJson(rich), StringComparison.Ordinal);

        ServerActionMessage decoded = Assert.IsType<ServerActionMessage>(Codec.Decode(Encode(
            new ServerActionMessage(new AuthoritativeActionResult("s", 1, 1, Accepted: true, Payload: new InventoryChangedResult(rich))))).Message);
        Assert.Equal(1_234_567_890L, Assert.IsType<InventoryChangedResult>(decoded.Result.Payload).Inventory.Gold);
    }

    [Fact]
    public void ANegativeGoldOnTheWire_ReadsAsZero_AndAGarbledOneIsAnError()
    {
        string json = InventoryJson(PlayerInventorySnapshot.Empty("p") with { Gold = 5 });
        byte[] negative = Encoding.UTF8.GetBytes(json.Replace("\"gold\":5", "\"gold\":-40", StringComparison.Ordinal));
        byte[] garbled = Encoding.UTF8.GetBytes(json.Replace("\"gold\":5", "\"gold\":\"lots\"", StringComparison.Ordinal));

        ServerActionMessage decoded = Assert.IsType<ServerActionMessage>(Codec.Decode(negative).Message);
        Assert.Equal(0L, Assert.IsType<InventoryChangedResult>(decoded.Result.Payload).Inventory.Gold);
        Assert.ThrowsAny<JsonException>(() => Codec.Decode(garbled));
    }
}
