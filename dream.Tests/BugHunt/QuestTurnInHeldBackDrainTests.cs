using Dream.Simulation.Content;
using Dream.Simulation.Inventory;
using Dream.Simulation.Persistence;
using Dream.Simulation.World;
using Dream.Tests.Support;
using Xunit;

namespace Dream.Tests.BugHunt;

// BH-QUEST-6: "Held-back rows wait for room and drain, in order, after every accepted move, destroy, equip
// or unequip" (PlayerInventoryService's own rule). A Collect turn-in frees the anchors the collected items
// sat on, and it was the one path that freed room without draining: the held-back row stayed invisible
// until some other action happened to drain it. Driven on the in-memory world repository, so the whole
// test is memory and the inline write queue - no database, no flush.
public sealed class QuestTurnInHeldBackDrainTests
{
    private const string Player = "Alice";
    private const string Torch = "tool.torch";
    private const string Band = "gear.iron-band";

    private const long HeldBackItemId = 999;

    private static PlayerInventoryService CreateService(EquipmentTestWorld world, out PersistenceWriteQueue writes)
    {
        writes = PersistenceWriteQueue.Inline();
        return new PlayerInventoryService(world, writes, new ObjectIdAllocator());
    }

    private static QuestDefinition TorchSupply() => QuestBugHunt.Shipped("torch-supply");

    // A bag with no free cell at all: two torches (1x2, so anchors 0 and 1 cover cells 0,1,8,9) and a
    // 1x1 band on every other cell. withHeldBack adds one band in the held-back range, which cannot
    // drain while the bag is full.
    private static EquipmentTestWorld FullBag(bool withHeldBack)
    {
        EquipmentTestWorld world = new();
        long nextId = 10;
        world.PutItem(EquipmentTestWorld.MakeItem(nextId, Player, Torch));
        world.PutSlot(Player, 0, nextId++);
        world.PutItem(EquipmentTestWorld.MakeItem(nextId, Player, Torch));
        world.PutSlot(Player, 1, nextId++);
        for (int anchor = 2; anchor < InventoryConstants.BackpackSlotCount; anchor++)
        {
            if (anchor is 8 or 9)
            {
                continue;
            }

            world.PutItem(EquipmentTestWorld.MakeItem(nextId, Player, Band));
            world.PutSlot(Player, anchor, nextId++);
        }

        if (withHeldBack)
        {
            world.PutItem(EquipmentTestWorld.MakeItem(HeldBackItemId, Player, Band));
            world.PutSlot(Player, InventoryConstants.HeldBackFirstIndex, HeldBackItemId);
        }

        world.PutGrant(Player, ToolCatalog.CurrentStarterKitVersion);
        return world;
    }

    private static int CountHeldBack(Dictionary<int, long> rows)
    {
        int count = 0;
        foreach (int index in rows.Keys)
        {
            if (InventoryConstants.IsHeldBack(index))
            {
                count++;
            }
        }

        return count;
    }

    [Fact]
    public void ACollectTurnIn_DrainsTheHeldBackRowsItMadeRoomFor()
    {
        EquipmentTestWorld world = FullBag(withHeldBack: true);
        PlayerInventoryService inventory = CreateService(world, out PersistenceWriteQueue writes);
        using (writes)
        {
            Assert.True(inventory.Ensure(Player));

            // The bag was full on load, so the repair's own drain could not place it.
            Assert.Equal(1, CountHeldBack(world.SlotsOf(Player)));
            Assert.Equal(2, inventory.CountBagItems(Player, Torch));

            QuestDefinition quest = TorchSupply();
            Assert.Equal(InventoryEquipOutcome.Equipped, inventory.TryAcceptQuest(Player, quest));
            Assert.Equal(InventoryEquipOutcome.Equipped, inventory.TryTurnInQuest(Player, quest, out _));
        }

        // The turn-in freed the two torch anchors, so the held-back row drained into the bag: no row is
        // left in the held-back range and the item is on an anchor the player can see.
        Dictionary<int, long> rows = world.SlotsOf(Player);
        Assert.Equal(0, CountHeldBack(rows));
        int anchor = Assert.Single(rows, row => row.Value == HeldBackItemId).Key;
        Assert.InRange(anchor, 0, InventoryConstants.BackpackSlotCount - 1);
        Assert.Equal(0, inventory.CountBagItems(Player, Torch));
    }

    // Control: the drain is the only thing added. A turn-in with nothing held back behaves exactly as
    // before - the torches are consumed, the gold is paid, and no slot row appears out of nowhere.
    [Fact]
    public void ACollectTurnIn_WithNothingHeldBack_IsUnchanged()
    {
        EquipmentTestWorld world = FullBag(withHeldBack: false);
        PlayerInventoryService inventory = CreateService(world, out PersistenceWriteQueue writes);
        using (writes)
        {
            Assert.True(inventory.Ensure(Player));
            QuestDefinition quest = TorchSupply();
            Assert.Equal(InventoryEquipOutcome.Equipped, inventory.TryAcceptQuest(Player, quest));
            Assert.Equal(InventoryEquipOutcome.Equipped, inventory.TryTurnInQuest(Player, quest, out long xp));
            Assert.Equal(quest.Rewards.Xp, xp);
            Assert.Equal(quest.Rewards.Gold, inventory.GoldOf(Player));
        }

        Dictionary<int, long> rows = world.SlotsOf(Player);
        Assert.Equal(0, CountHeldBack(rows));
        Assert.Equal(InventoryConstants.BackpackSlotCount - 4, rows.Count);
        Assert.Equal(0, inventory.CountBagItems(Player, Torch));
    }

    // Control: a drain only places what fits. With the freed cells taken by more held-back rows than
    // there is room for, the rest stay held back rather than being lost or double-placed.
    [Fact]
    public void ADrainThatRunsOutOfRoom_KeepsTheRestHeldBack()
    {
        EquipmentTestWorld world = FullBag(withHeldBack: true);
        long nextId = 2000;
        for (int index = InventoryConstants.HeldBackFirstIndex + 1; index <= InventoryConstants.HeldBackLastIndex; index++)
        {
            world.PutItem(EquipmentTestWorld.MakeItem(nextId, Player, Band));
            world.PutSlot(Player, index, nextId++);
        }

        PlayerInventoryService inventory = CreateService(world, out PersistenceWriteQueue writes);
        using (writes)
        {
            Assert.True(inventory.Ensure(Player));
            Assert.Equal(InventoryConstants.HeldBackCount, CountHeldBack(world.SlotsOf(Player)));

            QuestDefinition quest = TorchSupply();
            Assert.Equal(InventoryEquipOutcome.Equipped, inventory.TryAcceptQuest(Player, quest));
            Assert.Equal(InventoryEquipOutcome.Equipped, inventory.TryTurnInQuest(Player, quest, out _));
        }

        // The two torches freed four cells, so four of the sixteen held-back bands placed.
        Dictionary<int, long> rows = world.SlotsOf(Player);
        Assert.Equal(InventoryConstants.HeldBackCount - 4, CountHeldBack(rows));
        Assert.Equal(rows.Count, rows.Values.Distinct().Count());
    }
}
