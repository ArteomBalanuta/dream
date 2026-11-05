using OpenTK.Mathematics;
using Xunit;
using Dream.Multiplayer.Protocol;
using Dream.Server.Persistence;
using Dream.Simulation.World;
using Dream.Simulation.Inventory;
using Dream.Simulation.Persistence;
using Dream.Simulation.Runtime;
using Dream.Tests.Support;

namespace Dream.Tests.Simulation.Inventory;

// A player's drop at the ground cap evicts only an item nobody else has claimed, and is refused (NoRoom,
// nothing changed) when every item is claimed by someone else. Loot drops are never refused: they prefer an
// unclaimed item and fall back to the oldest overall only when everything is claimed.
public sealed class GroundItemCapDropTests
{
    private const string Alice = "Alice";
    private const string Bob = "Bob";
    private const string Kind = "gear.iron-band";
    private const ulong Start = 1000;

    private static readonly Vector3 Spot = new(10f, 1.5f, 10f);

    private static PlayerInventoryService Service()
    {
        PlayerInventoryService service = new(worldPersistence: null, writes: null, objectIds: new ObjectIdAllocator());
        Assert.True(service.Ensure(Alice));
        Assert.True(service.Ensure(Bob));
        return service;
    }

    private static void Loot(PlayerInventoryService service, int index, string? claimant)
        => Assert.True(service.TryDropRolled(Kind, ItemRarity.Common, new ItemRollSeed(1, 2, 3, 4, index), Spot, claimant, claimant == null ? 0 : 7, Start));

    private static InventorySlotSnapshot Granted(PlayerInventoryService service)
    {
        Assert.Equal(InventoryEquipOutcome.Equipped, service.TryGrantRolled(Alice, Kind, ItemRarity.Common, new ItemRollSeed(7, 7, 7, 7, 0)));
        return service.BuildSnapshot(Alice).Slots.Last(slot => slot.Item?.ItemKind == Kind);
    }

    private static List<long> Ids(PlayerInventoryService service)
    {
        List<GroundItemSnapshot> all = [];
        service.CaptureGroundItems(all);
        return all.Select(item => item.ItemInstanceId).ToList();
    }

    [Fact]
    public void AtTheCap_APlayerDropEvictsTheOldestUnclaimedItem_NotTheOldest()
    {
        PlayerInventoryService service = Service();
        for (int i = 0; i < PlayerInventoryService.MaxGroundItems; i++)
        {
            Loot(service, i, i == 5 ? null : Bob);
        }

        List<long> ids = Ids(service);
        long unclaimed = ids[5];
        long oldest = ids[0];
        InventorySlotSnapshot slot = Granted(service);
        long id = slot.Item!.Value.ItemInstanceId;
        service.DrainGroundDeltas([], []);

        Assert.Equal(InventoryEquipOutcome.Equipped, service.TryDropToGround(Alice, slot.SlotIndex, id, Spot, 9, Start));

        Assert.Equal(PlayerInventoryService.MaxGroundItems, service.GroundItemCount);
        Assert.False(service.IsOnGround(unclaimed));
        Assert.True(service.IsOnGround(oldest));
        Assert.True(service.IsOnGround(id));
    }

    [Fact]
    public void AtTheCap_APlayerDropEvictsTheirOwnClaimedItem()
    {
        PlayerInventoryService service = Service();
        Loot(service, 0, Bob);
        Loot(service, 1, Alice);
        for (int i = 2; i < PlayerInventoryService.MaxGroundItems; i++)
        {
            Loot(service, i, Bob);
        }

        long own = Ids(service)[1];
        InventorySlotSnapshot slot = Granted(service);

        Assert.Equal(InventoryEquipOutcome.Equipped, service.TryDropToGround(Alice, slot.SlotIndex, slot.Item!.Value.ItemInstanceId, Spot, 9, Start));

        Assert.False(service.IsOnGround(own));
    }

    [Fact]
    public void AtTheCap_WhenEveryItemIsClaimedBySomeoneElse_TheDropIsRefusedAndNothingChanges()
    {
        PlayerInventoryService service = Service();
        for (int i = 0; i < PlayerInventoryService.MaxGroundItems; i++)
        {
            Loot(service, i, Bob);
        }

        List<long> before = Ids(service);
        InventorySlotSnapshot slot = Granted(service);
        long id = slot.Item!.Value.ItemInstanceId;
        service.DrainGroundDeltas([], []);

        Assert.Equal(InventoryEquipOutcome.NoRoom, service.TryDropToGround(Alice, slot.SlotIndex, id, Spot, 9, Start));

        Assert.Equal(before, Ids(service));
        Assert.False(service.IsOnGround(id));
        Assert.Contains(service.BuildSnapshot(Alice).AllItems(), item => item.ItemInstanceId == id);
        Assert.False(service.HasPendingGroundDeltas);
    }

    private static void FillAllClaimedByBob(PlayerInventoryService service)
    {
        for (int i = 0; i < PlayerInventoryService.MaxGroundItems; i++)
        {
            Loot(service, i, Bob);
        }
    }

    [Fact]
    public void AtTheCap_AllClaimedByOthers_TheRefusalSpendsNoToken()
    {
        PlayerInventoryService service = Service();
        FillAllClaimedByBob(service);
        InventorySlotSnapshot slot = Granted(service);
        int calls = 0;
        service.ActionGate = () => { calls++; return false; };

        Assert.Equal(InventoryEquipOutcome.NoRoom, service.TryDropToGround(Alice, slot.SlotIndex, slot.Item!.Value.ItemInstanceId, Spot, 9, Start));

        Assert.Equal(0, calls);
    }

    [Fact]
    public void AtTheCap_WithAnEvictableItem_AThrottledDropLeavesTheVictimOnTheGround()
    {
        PlayerInventoryService service = Service();
        for (int i = 0; i < PlayerInventoryService.MaxGroundItems; i++)
        {
            Loot(service, i, i == 5 ? null : Bob);
        }

        long victim = Ids(service)[5];
        InventorySlotSnapshot slot = Granted(service);
        int calls = 0;
        service.ActionGate = () => { calls++; return false; };

        Assert.Equal(InventoryEquipOutcome.Throttled, service.TryDropToGround(Alice, slot.SlotIndex, slot.Item!.Value.ItemInstanceId, Spot, 9, Start));

        Assert.Equal(1, calls);
        Assert.True(service.IsOnGround(victim));
        Assert.Equal(PlayerInventoryService.MaxGroundItems, service.GroundItemCount);
    }

    [Fact]
    public void AtTheCap_LootEvictsAnUnclaimedItemInPreferenceToAnOlderClaimedOne()
    {
        PlayerInventoryService service = Service();
        for (int i = 0; i < PlayerInventoryService.MaxGroundItems; i++)
        {
            Loot(service, i, i == 5 ? null : Bob);
        }

        List<long> ids = Ids(service);

        Loot(service, 999, Bob);

        Assert.Equal(PlayerInventoryService.MaxGroundItems, service.GroundItemCount);
        Assert.False(service.IsOnGround(ids[5]));
        Assert.True(service.IsOnGround(ids[0]));
    }

    [Fact]
    public void AtTheCap_LootStillLandsWhenEveryItemIsClaimed_EvictingTheOldest()
    {
        PlayerInventoryService service = Service();
        FillAllClaimedByBob(service);
        List<long> ids = Ids(service);

        Loot(service, 999, Alice);

        Assert.Equal(PlayerInventoryService.MaxGroundItems, service.GroundItemCount);
        Assert.False(service.IsOnGround(ids[0]));
        Assert.True(service.IsOnGround(ids[1]));
    }

    [Fact]
    public void ADropRefusedForNoRoom_SaysTheGroundIsFull_WhileOtherVerbsKeepTheFitText()
    {
        string drop = WorldSessionRuntime.InventoryRejectionReason(InventoryEquipOutcome.NoRoom, "drop")!;
        Assert.Contains("no room on the ground", drop);
        Assert.DoesNotContain("fit", drop);
        Assert.Equal("the item does not fit there", WorldSessionRuntime.InventoryRejectionReason(InventoryEquipOutcome.NoRoom, "move"));
        Assert.Equal("the item does not fit there", WorldSessionRuntime.InventoryRejectionReason(InventoryEquipOutcome.NoRoom, "pickup"));
    }

    [Fact]
    public void APickupThenADropOfTheSameItemInOneTick_IsReportedAsAnUpsertOnly()
    {
        PlayerInventoryService service = Service();
        InventorySlotSnapshot slot = Granted(service);
        long id = slot.Item!.Value.ItemInstanceId;
        Assert.Equal(InventoryEquipOutcome.Equipped, service.TryDropToGround(Alice, slot.SlotIndex, id, Spot, 9, Start));
        service.DrainGroundDeltas([], []);

        Assert.Equal(InventoryEquipOutcome.Equipped, service.TryPickUpGround(Alice, id, Spot, Start));
        InventorySlotSnapshot again = service.BuildSnapshot(Alice).Slots.First(s => s.Item?.ItemInstanceId == id);
        Assert.Equal(InventoryEquipOutcome.Equipped, service.TryDropToGround(Alice, again.SlotIndex, id, Spot, 9, Start));

        List<GroundItemSnapshot> upserts = [];
        List<long> removed = [];
        service.DrainGroundDeltas(upserts, removed);
        Assert.Contains(upserts, u => u.ItemInstanceId == id);
        Assert.DoesNotContain(id, removed);
    }
}
