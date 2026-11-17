using Xunit;
using Dream.Multiplayer.Diagnostics;
using Dream.Simulation.Inventory;
using Dream.Simulation.Persistence;
using Dream.Simulation.World;
using Dream.Tests.Support;

namespace Dream.Tests.BugHunt;

// BUGHUNTER-ITEM wave 2: the two confirmed findings of the items-as-durable-value sweep.
// BH-ITEM-1 the layout repair parking an overflow row at an index the service treats as inert.
public sealed class ItemLayoutAndStarterKitTests
{
    private const string Player = "Alice";

    // BH-ITEM-1. An inventory whose held-back rows (200..215) and whole parked range (300..19999) are
    // taken, plus one bag row that does not place on the board. The repair has no live row left for the
    // lifted item: it keeps the row (at the lowest free index of the inert range, never a negative one)
    // and must say so, instead of counting it as an ordinary held-back row and losing the item silently.
    [Fact]
    public void WhenTheParkedRangeIsFull_AStrandedOverflowRowIsReported_AndItsItemKeepsExactlyOneRow()
    {
        EquipmentTestWorld world = new();

        // Nine blades (2x3) piled on anchors 0..8: eight place on the 8x8 board and the ninth fits
        // nowhere, so the repair has to find it a row.
        for (int anchor = 0; anchor < 9; anchor++)
        {
            world.PutItem(EquipmentTestWorld.MakeItem(10 + anchor, Player, "gear.iron-blade"));
            world.PutSlot(Player, anchor, 10 + anchor);
        }

        long nextId = 100;
        for (int row = InventoryConstants.HeldBackFirstIndex; row <= InventoryConstants.HeldBackLastIndex; row++)
        {
            world.PutItem(EquipmentTestWorld.MakeItem(nextId, Player, "gear.iron-band"));
            world.PutSlot(Player, row, nextId++);
        }

        for (int row = InventoryConstants.ParkedFirstIndex; row < InventoryConstants.UnknownRowsFirstIndex; row++)
        {
            world.PutItem(EquipmentTestWorld.MakeItem(nextId, Player, "gear.iron-band"));
            world.PutSlot(Player, row, nextId++);
        }

        world.PutGrant(Player, ToolCatalog.CurrentStarterKitVersion);

        List<(string Category, string Message)> lines = [];
        PlayerInventoryService inventory = CreateService(world, out PersistenceWriteQueue writes);
        using (writes)
        using (NetworkDiagnosticsLog.CaptureOnCurrentThread((category, message) => lines.Add((category, message))))
        {
            Assert.True(inventory.Ensure(Player));
        }

        Dictionary<int, long> rows = world.SlotsOf(Player);
        const int TotalRows = 9 + InventoryConstants.HeldBackCount
            + (InventoryConstants.UnknownRowsFirstIndex - InventoryConstants.ParkedFirstIndex);

        // Nothing is destroyed or aliased: every item still has exactly one row, and no row is written
        // at a negative index (the schema's CHECK (slot_index >= 0) would reject it).
        Assert.Equal(TotalRows, rows.Count);
        Assert.Equal(TotalRows, rows.Values.Distinct().Count());
        Assert.DoesNotContain(rows.Keys, row => row < 0);

        // The stranding is accounted for: the one row that landed in the inert range is named with its
        // item, and the repair's own diagnostic counts it as stranded, not as an ordinary held-back row.
        long strandedId = Assert.Single(rows, row => row.Key >= InventoryConstants.UnknownRowsFirstIndex).Value;
        (string Category, string Message) stranded = Assert.Single(
            lines,
            line => line.Category == "server.inventory.layout-row-stranded");
        Assert.Contains($"item={strandedId}", stranded.Message, StringComparison.Ordinal);
        Assert.Contains($"slot={InventoryConstants.UnknownRowsFirstIndex}", stranded.Message, StringComparison.Ordinal);
        Assert.Contains(
            lines,
            line => line.Category == "server.inventory.layout-repaired"
                && line.Message.Contains("stranded=1", StringComparison.Ordinal));
    }

    // BH-ITEM-1 control: with room in the parked range the repair is unchanged - the overflow row is
    // parked below the inert range and nothing is reported as stranded.
    [Fact]
    public void WhenTheParkedRangeHasRoom_TheOverflowRowIsParkedBelowTheInertRange_AndNothingIsStranded()
    {
        EquipmentTestWorld world = new();
        for (int i = 0; i < 40; i++)
        {
            world.PutItem(EquipmentTestWorld.MakeItem(100 + i, Player, "gear.iron-blade"));
            world.PutSlot(Player, i, 100 + i);
        }

        world.PutGrant(Player, ToolCatalog.CurrentStarterKitVersion);

        List<(string Category, string Message)> lines = [];
        PlayerInventoryService inventory = CreateService(world, out PersistenceWriteQueue writes);
        using (writes)
        using (NetworkDiagnosticsLog.CaptureOnCurrentThread((category, message) => lines.Add((category, message))))
        {
            Assert.True(inventory.Ensure(Player));
        }

        Dictionary<int, long> rows = world.SlotsOf(Player);
        Assert.Equal(40, rows.Count);
        Assert.Equal(40, rows.Values.Distinct().Count());
        Assert.DoesNotContain(rows.Keys, row => row >= InventoryConstants.UnknownRowsFirstIndex);
        Assert.DoesNotContain(lines, line => line.Category == "server.inventory.layout-row-stranded");
    }

    private static PlayerInventoryService CreateService(EquipmentTestWorld world, out PersistenceWriteQueue writes)
    {
        writes = PersistenceWriteQueue.Inline();
        return new PlayerInventoryService(world, writes, new ObjectIdAllocator());
    }
}
