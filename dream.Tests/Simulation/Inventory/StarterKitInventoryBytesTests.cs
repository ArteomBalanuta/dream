using System.Text;
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

// What the inventory a joining player receives (JoinAcceptedMessage.Inventory) costs for the starter kit.
// At wave H C0 the whole join-accepted frame for the kit measured 3,566 B and the wave H gate was 4,099 B.
// Classic HUD iteration 2 (protocol v13) sends the snapshot compactly (PlayerInventorySnapshotJsonConverter):
// the item text travels once, in the client's catalog, not once per copy. The inventory must get smaller than
// the v12 shape of the same kit, and its bytes are pinned.
public sealed class StarterKitInventoryBytesTests(ITestOutputHelper output)
{
    private static int Bytes(PlayerInventorySnapshot inventory, JsonSerializerOptions options)
        => JsonSerializer.SerializeToUtf8Bytes(inventory, options).Length;

    private static PlayerInventorySnapshot Starter()
    {
        EquipmentTestWorld world = new();
        using PersistenceWriteQueue writes = PersistenceWriteQueue.Inline();
        PlayerInventoryService inventory = new(world, writes, new ObjectIdAllocator());
        Assert.True(inventory.Ensure("Alice"));
        return inventory.BuildSnapshot("Alice");
    }

    // The kit as the v12 server laid it out: sixteen slots in a row, the kit in slots 0 to 4, the hammer also
    // worn (the alias), and every item with its full text.
    private static PlayerInventorySnapshot AsV12(PlayerInventorySnapshot snapshot)
    {
        string[] kinds = ToolCatalog.StarterItemKinds.ToArray();
        InventorySlotSnapshot[] slots = new InventorySlotSnapshot[16];
        for (int i = 0; i < slots.Length; i++)
        {
            slots[i] = i < kinds.Length
                ? new InventorySlotSnapshot(i, InventoryWireSamples.Item(kinds[i], i + 1), IsEquipped: i == 0)
                : new InventorySlotSnapshot(i, null);
        }

        return snapshot with
        {
            Columns = 4,
            Rows = 4,
            Slots = slots,
            EquippedSlot = new InventorySlotSnapshot(EquipmentSlots.FirstSlotIndex, InventoryWireSamples.Item(kinds[0], 1), true),
            Equipment = null,
        };
    }

    [Fact]
    public void TheCompactStarterKit_IsSmallerThanTheV12Shape_AndItsBytesArePinned()
    {
        PlayerInventorySnapshot snapshot = Starter();

        int compact = Bytes(snapshot, ProtocolWireOptions.V3);
        int v12 = Bytes(AsV12(snapshot), ProtocolWireOptions.V3Reflection);
        string json = Encoding.UTF8.GetString(JsonSerializer.SerializeToUtf8Bytes(snapshot, ProtocolWireOptions.V3));

        output.WriteLine($"starter kit inventory: {v12} B as v12, {compact} B compact ({compact - v12:+0;-0} B)");
        output.WriteLine(json);
        Assert.True(compact < v12, $"{compact} B is not smaller than the v12 shape's {v12} B");
        Assert.True(compact * 3 < v12, $"{compact} B is not under a third of {v12} B");

        // The kit in its first fits: the hammer worn in the main hand and the torch in the off hand,
        // the blade, maul and lantern beside each other, then the four potions.
        Assert.Equal(
            """{"playerName":"Alice","columns":8,"rows":8,"bag":[[2,2,"tool.field-blade",0,[]],[4,3,"tool.stone-maul",0,[]],[6,4,"tool.survey-lantern",0,[]],[22,6,"consumable.weak-heal",0,[]],[23,7,"consumable.minor-heal",0,[]],[24,8,"potion.health",0,[]],[25,9,"potion.mana",0,[]]],"worn":[[100,1,"tool.builder-hammer",0,[]],[101,5,"tool.torch",0,[]]]}""",
            json);
    }
}
