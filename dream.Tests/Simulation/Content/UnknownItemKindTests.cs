using Dream.Simulation.Inventory;
using Dream.Simulation.Persistence;
using Dream.Simulation.World;
using Xunit;

namespace Dream.Tests.Simulation.Content;

// F2: a retired item_kind on an equipped item must not throw on the snapshot paths, and its row stays.
public sealed class UnknownItemKindTests
{
    private const string RetiredKind = "tool.retired-sword";

    [Fact]
    public void EquippedItemWithARetiredKind_DoesNotThrow_AndKeepsItsRow()
    {
        DateTime now = DateTime.UtcNow;
        PersistedItemInstance retired = new(
            7, RetiredKind, "Old Sword", "held.tool.old", "Zed", WorldPosition: null, State: "Inventory", StackCount: 1, now, now);
        PlayerInventoryService inventory = new(worldPersistence: null, writes: null, objectIds: new ObjectIdAllocator());
        inventory.ApplyLoaded("Zed", new PersistedPlayerInventory(
            [
                new PersistedInventorySlot("Zed", 0, 7, now),
                new PersistedInventorySlot("Zed", InventoryConstants.EquippedSlotIndex, 7, now)
            ],
            [retired]));

        // The paths CaptureActorSnapshots and the join/inventory snapshot take.
        Assert.Equal(HeldLightKind.None, inventory.ResolveEquippedHeldLight("Zed"));
        Assert.Equal(default, inventory.ResolveEquippedToolModifier("Zed"));
        PlayerInventorySnapshot snapshot = inventory.BuildSnapshot("Zed");

        InventoryItemSnapshot equipped = snapshot.EquippedSlot!.Value.Item!.Value;
        Assert.Equal(7, equipped.ItemInstanceId);
        Assert.Equal(RetiredKind, equipped.ItemKind);
        Assert.Equal("Old Sword", equipped.DisplayName);
        Assert.Equal("held.tool.old", equipped.VisualId);
        Assert.Equal(ToolCatalog.UnknownItemDescription, equipped.Description);
        Assert.Empty(equipped.DetailLines);
        Assert.Equal(equipped, snapshot.Slots[0].Item!.Value);

        // The row is still loaded, and the slots still point at it.
        Assert.Equal(1, inventory.LoadedItemInstanceCount);
        Assert.Equal(7, inventory.LoadedSlotsByPlayer["Zed"][InventoryConstants.EquippedSlotIndex]);
    }

    [Fact]
    public void ToolCatalog_TryResolve_IsFalseForARetiredKind_WhileResolveStillThrows()
    {
        Assert.False(ToolCatalog.TryResolve(RetiredKind, out _));
        Assert.Throws<KeyNotFoundException>(() => ToolCatalog.Resolve(RetiredKind));
    }
}
