using System.Text;
using Xunit;
using Dream.Multiplayer.Protocol;
using Dream.Server;
using Dream.Simulation.Inventory;
using Dream.Simulation.Stats;
using static Dream.Tests.Server.ServerConnectionTestFixtures;

namespace Dream.Tests.Multiplayer.Protocol;

// Wave I C0a (protocol v11): server.inventory-push is a server-only message that round trips, is
// refused from a client without decoding its payload, routes to a client handler, and reaches the
// wire through a real ServerConnection (an unhandled type would fault its writer).
public sealed class InventoryPushCodecTests
{
    private static readonly TransportJsonCodec Codec = new();

    private static InventoryPushMessage Push(bool withInventory = true)
    {
        PlayerInventorySnapshot? inventory = null;
        if (withInventory)
        {
            InventoryItemSnapshot rolled = new(
                77, "gear.iron-blade", "Iron Blade", "held.tool.blade", 1, "A blade.", ["Role: Combat"],
                Modifiers: [new StatModifier(StatId.Strength, StatOp.Flat, 6f), new StatModifier(StatId.AttackSpeed, StatOp.Flat, 1.75f)],
                EquipSlots: 1, Rarity: ItemRarity.Rare, IsRolled: true);
            PlayerInventorySnapshot empty = PlayerInventorySnapshot.Empty("bob");
            InventorySlotSnapshot[] slots = [.. empty.Slots];
            slots[0] = new InventorySlotSnapshot(0, rolled);
            inventory = empty with { Slots = slots };
        }

        return new InventoryPushMessage(
            inventory,
            [new LootNotice("Iron Blade", ItemRarity.Rare, Lost: false), new LootNotice("Leather Cap", ItemRarity.Common, Lost: true)]);
    }

    private static byte[] Encode(InventoryPushMessage message, ulong sequence = 1) =>
        Codec.Encode(new TransportEnvelope(TransportEnvelope.CurrentProtocolVersion, message.MessageKind, sequence), message);

    [Fact]
    public void ThePush_RoundTrips_WithItsInventoryAndNotices()
    {
        InventoryPushMessage original = Push();

        DecodedProtocolMessage decoded = Codec.Decode(Encode(original, 5));

        InventoryPushMessage push = Assert.IsType<InventoryPushMessage>(decoded.Message);
        Assert.Equal("server.inventory-push", decoded.Envelope.MessageKind);
        Assert.Equal(5UL, decoded.Envelope.Sequence);
        Assert.Equal(original.Notices, push.Notices);
        Assert.NotNull(push.Inventory);
        InventoryItemSnapshot item = push.Inventory.Value.Slots[0].Item!.Value;
        Assert.Equal("Iron Blade", item.DisplayName);
        Assert.Equal(ItemRarity.Rare, item.Rarity);
        Assert.True(item.IsRolled);
        Assert.Equal(original.Inventory!.Value.Slots[0].Item!.Value.Modifiers, item.Modifiers);
        Assert.Equal(original.Inventory.Value.Slots.Count, push.Inventory.Value.Slots.Count);
    }

    [Fact]
    public void ALostOnlyPush_HasNoInventory_AndStaysSmall()
    {
        InventoryPushMessage lost = new(null, [new LootNotice("Leather Cap", ItemRarity.Uncommon, Lost: true)]);

        byte[] bytes = Encode(lost);

        string json = Encoding.UTF8.GetString(bytes);
        Assert.DoesNotContain("\"inventory\":", json, StringComparison.Ordinal);
        Assert.True(bytes.Length <= 256, $"{bytes.Length} B");
        InventoryPushMessage decoded = Assert.IsType<InventoryPushMessage>(Codec.Decode(bytes).Message);
        Assert.Null(decoded.Inventory);
        Assert.Equal(lost.Notices, decoded.Notices);
    }

    [Fact]
    public void AClient_SendingThePushKind_IsRefusedFromTheEnvelope_WithoutDecodingThePayload()
    {
        Assert.True(TransportJsonCodec.IsServerOnlyKind(InventoryPushMessage.Kind));
        // A payload that would not decode as a push proves it was never read.
        string json = "{\"protocolVersion\":" + TransportEnvelope.CurrentProtocolVersion + ",\"messageKind\":\"server.inventory-push\",\"sequence\":1,\"payload\":{\"notices\":\"not a list\"}}";

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => Codec.DecodeFromClient(Encoding.UTF8.GetBytes(json)));

        Assert.Contains("server-only message kind", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheClientSession_RoutesThePushToItsHandler()
    {
        ClientProtocolSession session = ClientProtocolSession.CreateDefault();
        InventoryPushMessage push = Push();

        session.ApplyServerMessage(push);

        Assert.Same(push, session.Context.LastInventoryPush);
    }

    [Fact]
    public async Task ThePush_ReachesTheWire_ThroughARealServerConnection()
    {
        GatedTransport transport = new("push");
        Dream.Server.DedicatedServerHost host = CreateHost(new MutatingAuthority());
        await using ServerConnection connection = new(transport, host, new TransportJsonCodec(), new LengthPrefixedMessageFramer(), WithoutLiveness);
        InventoryPushMessage push = Push();

        connection.Enqueue(push);
        connection.Enqueue(new ServerHelloMessage("after", TransportEnvelope.CurrentProtocolVersion, 1));
        Assert.True(await connection.FlushOutboundAsync(Deadline));

        Assert.False(connection.IsFaulted);
        IReadOnlyList<IProtocolMessage> wire = Decode(transport.Frames);
        Assert.Equal(2, wire.Count);
        InventoryPushMessage received = Assert.IsType<InventoryPushMessage>(wire[0]);
        Assert.Equal(push.Notices, received.Notices);
        Assert.Equal("Iron Blade", received.Inventory!.Value.Slots[0].Item!.Value.DisplayName);
        Assert.IsType<ServerHelloMessage>(wire[1]);
    }
}
