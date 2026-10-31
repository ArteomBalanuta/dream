using System.Text.Json;
using Xunit;
using Xunit.Abstractions;
using Dream.Multiplayer.Protocol;
using Dream.Simulation.Inventory;
using Dream.Simulation.Persistence;
using Dream.Simulation.World;
using Dream.Tests.Multiplayer.Protocol;
using Dream.Tests.Support;

namespace Dream.Tests.Simulation.Inventory;

// Wave H1: what the equipment fields add to the inventory a joining player receives
// (JoinAcceptedMessage.Inventory), for the starter kit. At C0 the join-accepted frame for the
// starter kit measured 3,566 B and the wave H gate is 4,099 B, so the new fields have 533 B.
public sealed class StarterKitInventoryBytesTests(ITestOutputHelper output)
{
    private const int GateHeadroomBytes = 4_099 - 3_566;

    private static int InventoryBytes(PlayerInventorySnapshot inventory)
        => JsonSerializer.SerializeToUtf8Bytes(inventory, ProtocolWireOptions.V3).Length;

    // The inventory as the C0 server sent it: none of the wave H item fields.
    private static PlayerInventorySnapshot AsAtC0(PlayerInventorySnapshot inventory)
    {
        static InventorySlotSnapshot Strip(InventorySlotSnapshot slot) => slot with
        {
            Item = slot.Item is { } item
                ? item with { Modifiers = null, HeldLight = HeldLightKind.None, EquipSlots = 0, Rarity = ItemRarity.Common, IsRolled = false }
                : null,
        };

        return inventory with
        {
            Slots = inventory.Slots.Select(Strip).ToArray(),
            EquippedSlot = inventory.EquippedSlot is { } equipped ? Strip(equipped) : null,
        };
    }

    [Fact]
    public void TheEquipmentFields_AddNoMoreThanTheGateAllows_ToTheStarterKitInventory()
    {
        EquipmentTestWorld world = new();
        using Dream.Simulation.Persistence.PersistenceWriteQueue writes = PersistenceWriteQueue.Inline();
        PlayerInventoryService inventory = new(world, writes, new ObjectIdAllocator());
        Assert.True(inventory.Ensure("Alice"));
        PlayerInventorySnapshot snapshot = inventory.BuildSnapshot("Alice");

        int now = InventoryBytes(snapshot);
        int atC0 = InventoryBytes(AsAtC0(snapshot));

        output.WriteLine($"starter kit inventory: {atC0} B at C0, {now} B now, +{now - atC0} B (headroom {GateHeadroomBytes} B)");
        Assert.True(now - atC0 <= GateHeadroomBytes, $"+{now - atC0} B exceeds the {GateHeadroomBytes} B headroom");
    }
}
