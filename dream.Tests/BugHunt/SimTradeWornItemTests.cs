using Dream.Simulation.Inventory;
using Dream.Simulation.Persistence;
using Dream.Simulation.World;
using Xunit;

namespace Dream.Tests.BugHunt;

// BH-SIM-1: a trade offer must refuse an item that also sits in an equipment slot (a hand-edited
// bag/worn alias, the state PersistedPlayerInventory and CopyEquipment both say they tolerate).
// The commit takes the bag anchor off only, so without the check the receiver gets the item while
// the giver keeps wearing it - two characters hold one instance.
public sealed class SimTradeWornItemTests
{
    private const string Alice = "Alice";
    private const string Bob = "Bob";

    private static PersistedItemInstance Item(long itemInstanceId, string owner)
    {
        ToolDefinition definition = ToolCatalog.Resolve(ToolCatalog.StarterItemKinds[0]);
        return new PersistedItemInstance(
            itemInstanceId,
            definition.ItemKind,
            definition.DisplayName,
            definition.VisualId,
            owner,
            WorldPosition: null,
            State: "Inventory",
            StackCount: 1,
            CreatedUtc: DateTime.UnixEpoch,
            UpdatedUtc: DateTime.UnixEpoch);
    }

    // An inventory with one item id at bag anchor 0 and at the main-hand slot (100).
    private static PlayerInventoryService WithAliasedRow(long itemId)
    {
        PlayerInventoryService inventory = new(worldPersistence: null, writes: null, objectIds: new ObjectIdAllocator());
        inventory.ApplyLoaded(Alice, new PersistedPlayerInventory(
            [
                new PersistedInventorySlot(Alice, 0, itemId, DateTime.UnixEpoch),
                new PersistedInventorySlot(Alice, InventoryConstants.EquippedSlotIndex, itemId, DateTime.UnixEpoch),
            ],
            [Item(itemId, Alice)],
            ToolCatalog.CurrentStarterKitVersion));
        inventory.ApplyLoaded(Bob, new PersistedPlayerInventory([], [], ToolCatalog.CurrentStarterKitVersion));
        Assert.True(inventory.Ensure(Alice));
        Assert.True(inventory.Ensure(Bob));
        return inventory;
    }

    [Fact]
    public void ATradeOffer_OfAnItemThatIsAlsoWorn_IsRefused()
    {
        const long ItemId = 4242;
        PlayerInventoryService inventory = WithAliasedRow(ItemId);

        Assert.Equal(InventoryEquipOutcome.ItemEquipped, inventory.ValidateTradeOffer(Alice, [ItemId], 0));
    }

    [Fact]
    public void ExecutingATradeOfAWornItem_IsRefused_SoNoSecondCopyIsCreated()
    {
        const long ItemId = 4242;
        PlayerInventoryService inventory = WithAliasedRow(ItemId);

        Assert.Equal(
            InventoryEquipOutcome.ItemEquipped,
            inventory.TryExecuteTrade(Alice, [ItemId], 0, Bob, [], 0));

        // Nothing moved: Alice still holds both rows and Bob holds none.
        Assert.Equal(ItemId, inventory.LoadedSlotsByPlayer[Alice][0]);
        Assert.Equal(ItemId, inventory.LoadedSlotsByPlayer[Alice][InventoryConstants.EquippedSlotIndex]);
        Assert.DoesNotContain(ItemId, inventory.LoadedSlotsByPlayer[Bob].Values);
    }
}
