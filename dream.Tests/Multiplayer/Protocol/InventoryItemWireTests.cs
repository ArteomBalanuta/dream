using System.Text;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Inventory;
using Dream.Simulation.Stats;
using Xunit;

namespace Dream.Tests.Multiplayer.Protocol;

// Wave H C0 (protocol v10): the new intents, and the item fields that are left off the wire at their
// defaults so a plain item costs nothing extra.
public sealed class InventoryItemWireTests
{
    private static string EncodeAction(IPlayerActionPayload payload)
    {
        TransportJsonCodec codec = new();
        PlayerActionMessage message = new(new PlayerSessionId("s"), 1, payload);
        return Encoding.UTF8.GetString(codec.Encode(
            new TransportEnvelope(TransportEnvelope.CurrentProtocolVersion, message.MessageKind, 1), message));
    }

    private static T RoundTrip<T>(T payload)
        where T : IPlayerActionPayload
    {
        TransportJsonCodec codec = new();
        PlayerActionMessage message = new(new PlayerSessionId("s"), 1, payload);
        DecodedProtocolMessage decoded = codec.Decode(codec.Encode(
            new TransportEnvelope(TransportEnvelope.CurrentProtocolVersion, message.MessageKind, 1), message));
        return Assert.IsType<T>(Assert.IsType<PlayerActionMessage>(decoded.Message).Payload);
    }

    [Fact]
    public void Equip_WithATarget_WritesIt_AndWithoutOneLeavesItOff()
    {
        Assert.Contains("\"payload\":{\"$type\":\"inventory.equip\",\"slotIndex\":4,\"targetSlotIndex\":101}", EncodeAction(new InventoryEquipIntent(4, 101)));
        string plain = EncodeAction(new InventoryEquipIntent(4));
        Assert.Contains("\"$type\":\"inventory.equip\",\"slotIndex\":4}", plain);
        Assert.DoesNotContain("targetSlotIndex", plain);
        Assert.Equal(new InventoryEquipIntent(4, 101), RoundTrip(new InventoryEquipIntent(4, 101)));
        Assert.Equal(new InventoryEquipIntent(4, null), RoundTrip(new InventoryEquipIntent(4)));
    }

    [Fact]
    public void UnequipMoveAndDestroy_RoundTrip_UnderTheirNames()
    {
        Assert.Contains("\"$type\":\"inventory.unequip\",\"slotIndex\":101", EncodeAction(new InventoryUnequipIntent(101)));
        Assert.Contains("\"$type\":\"inventory.move\",\"from\":2,\"to\":7", EncodeAction(new InventoryMoveIntent(2, 7)));
        Assert.Contains("\"$type\":\"inventory.destroy\",\"slotIndex\":3,\"itemInstanceId\":9000000000", EncodeAction(new InventoryDestroyIntent(3, 9_000_000_000L)));
        Assert.Equal(new InventoryUnequipIntent(101), RoundTrip(new InventoryUnequipIntent(101)));
        Assert.Equal(new InventoryMoveIntent(2, 7), RoundTrip(new InventoryMoveIntent(2, 7)));
        Assert.Equal(new InventoryDestroyIntent(3, 9_000_000_000L), RoundTrip(new InventoryDestroyIntent(3, 9_000_000_000L)));
    }

    [Fact]
    public void ThePlayerMaySendTheNewIntents()
    {
        Assert.True(IsSendable(new InventoryUnequipIntent(100)));
        Assert.True(IsSendable(new InventoryMoveIntent(0, 1)));
        Assert.True(IsSendable(new InventoryDestroyIntent(0, 1)));
        Assert.True(IsSendable(new InventoryEquipIntent(0, 101)));

        static bool IsSendable(IPlayerActionPayload payload)
        {
            System.Reflection.MethodInfo method = typeof(PlayerActionHandler).GetMethod(
                "IsClientSendable",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
            return (bool)method.Invoke(null, [payload])!;
        }
    }

    [Fact]
    public void APlainItem_AddsNoBytesForTheNewFields()
    {
        TransportJsonCodec codec = new();
        InventoryItemSnapshot plain = new(1, "tool.torch", "Torch", "held.tool.torch", 1, "d", ["l"]);
        string json = EncodeResult(codec, plain);

        Assert.DoesNotContain("modifiers", json);
        Assert.DoesNotContain("heldLight", json);
        Assert.DoesNotContain("equipSlots", json);
        Assert.DoesNotContain("rarity", json);
        Assert.DoesNotContain("isRolled", json);
        Assert.DoesNotContain("\"equipment\"", json);
    }

    [Fact]
    public void ARolledItem_WritesItsFieldsByTheirPlannedNames()
    {
        TransportJsonCodec codec = new();
        InventoryItemSnapshot rolled = new(
            1, "gear.iron-blade", "Iron Blade", "held.tool.blade", 1, "d", ["l"],
            Modifiers: [new StatModifier(StatId.Strength, StatOp.Flat, 4f)],
            HeldLight: HeldLightKind.Torch,
            EquipSlots: 3,
            Rarity: ItemRarity.Rare,
            IsRolled: true);
        string json = EncodeResult(codec, rolled);

        Assert.Contains("\"modifiers\":[{\"stat\":0,\"op\":0,\"value\":4}]", json);
        Assert.Contains("\"heldLight\":1", json);
        Assert.Contains("\"equipSlots\":3", json);
        Assert.Contains("\"rarity\":2", json);
        Assert.Contains("\"isRolled\":true", json);

        DecodedProtocolMessage decoded = codec.Decode(Encoding.UTF8.GetBytes(json));
        InventoryItemSnapshot back = Assert.IsType<InventoryChangedResult>(
            Assert.IsType<ServerActionMessage>(decoded.Message).Result.Payload).Inventory.Slots[0].Item!.Value;
        Assert.Equal(rolled.ItemInstanceId, back.ItemInstanceId);
        Assert.Equal(rolled.Modifiers, back.Modifiers);
        Assert.Equal(HeldLightKind.Torch, back.HeldLight);
        Assert.Equal(3, back.EquipSlots);
        Assert.Equal(ItemRarity.Rare, back.Rarity);
        Assert.True(back.IsRolled);
    }

    [Fact]
    public void TheStatIdNumbers_AreAWireContract()
    {
        Assert.Equal(0, (int)StatId.Strength);
        Assert.Equal(4, (int)StatId.AttackSpeed);
        Assert.Equal(2, (int)StatId.Vitality);
        Assert.Equal(6, (int)StatId.Armor);
    }

    private static string EncodeResult(TransportJsonCodec codec, InventoryItemSnapshot item)
    {
        PlayerInventorySnapshot inventory = new(
            "p", 4, 4, [new InventorySlotSnapshot(0, item)]);
        ServerActionMessage message = new(new AuthoritativeActionResult("s", 1, 1, Accepted: true, Payload: new InventoryChangedResult(inventory)));
        return Encoding.UTF8.GetString(codec.Encode(
            new TransportEnvelope(TransportEnvelope.CurrentProtocolVersion, message.MessageKind, 1), message));
    }
}
