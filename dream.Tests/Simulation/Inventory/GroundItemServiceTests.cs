using OpenTK.Mathematics;
using Xunit;
using Dream.Multiplayer.Protocol;
using Dream.Server.Persistence;
using Dream.Simulation.Inventory;
using Dream.Simulation.Persistence;
using Dream.Simulation.World;
using Dream.Tests.Support;

namespace Dream.Tests.Simulation.Inventory;

// M3 workstream A: PlayerInventoryService.Ground on its own (no persistence): the drop, the cap, the claim,
// the pickup's check order, the lapse, the despawn, the deltas and the restore.
public sealed class GroundItemServiceTests
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

    private static ItemRollSeed Seed(int index) => new(1, 2, 3, 4, index);

    private static bool Drop(PlayerInventoryService service, int index, string? claimant, ulong tick = Start, Vector3? at = null)
        => service.TryDropRolled(Kind, ItemRarity.Common, Seed(index), at ?? Spot, claimant, claimant == null ? 0 : 7, tick);

    private static List<GroundItemSnapshot> All(PlayerInventoryService service)
    {
        List<GroundItemSnapshot> all = [];
        service.CaptureGroundItems(all);
        return all;
    }

    [Fact]
    public void ADrop_IsOnTheGround_ClaimedByTheWinner_AndReportedOnceAsAnUpsert()
    {
        PlayerInventoryService service = Service();

        Assert.True(Drop(service, 0, Alice));

        GroundItemSnapshot item = Assert.Single(All(service));
        Assert.Equal(Kind, item.ItemKind);
        Assert.Equal(Spot, item.Position);
        Assert.Equal(7, item.ClaimantActorId);
        Assert.Equal(Start + PlayerInventoryService.GroundClaimTicks, item.ClaimExpiresTick);
        Assert.True(service.HasPendingGroundDeltas);
        List<GroundItemSnapshot> upserts = [];
        List<long> removed = [];
        service.DrainGroundDeltas(upserts, removed);
        Assert.Equal(item, Assert.Single(upserts));
        Assert.Empty(removed);
        Assert.False(service.HasPendingGroundDeltas);
    }

    [Fact]
    public void ADropWithNoClaimant_IsFreeForAll()
    {
        PlayerInventoryService service = Service();
        Assert.True(Drop(service, 0, claimant: null));

        GroundItemSnapshot item = Assert.Single(All(service));
        Assert.Equal(0, item.ClaimantActorId);
        Assert.Equal(0UL, item.ClaimExpiresTick);
        Assert.Equal(InventoryEquipOutcome.Equipped, service.TryPickUpGround(Bob, item.ItemInstanceId, Spot, Start));
    }

    [Fact]
    public void ADropOfAnUnknownKindOrANonFinitePosition_ChangesNothing()
    {
        PlayerInventoryService service = Service();
        long next = service.NextItemInstanceId;

        Assert.False(service.TryDropRolled("no.such-kind", ItemRarity.Common, Seed(0), Spot, Alice, 7, Start));
        Assert.False(service.TryDropRolled("tool.torch", ItemRarity.Common, Seed(0), Spot, Alice, 7, Start));
        Assert.False(Drop(service, 0, Alice, at: new Vector3(float.NaN, 0f, 0f)));

        Assert.Equal(0, service.GroundItemCount);
        Assert.Equal(next, service.NextItemInstanceId);
    }

    [Fact]
    public void PastTheCap_TheOldestIsDespawned_AndReportedRemoved()
    {
        PlayerInventoryService service = Service();
        for (int i = 0; i < PlayerInventoryService.MaxGroundItems; i++)
        {
            Assert.True(Drop(service, i, Alice));
        }

        long oldest = All(service)[0].ItemInstanceId;
        service.DrainGroundDeltas([], []);

        Assert.True(Drop(service, 999, Alice));

        Assert.Equal(PlayerInventoryService.MaxGroundItems, service.GroundItemCount);
        Assert.False(service.IsOnGround(oldest));
        List<long> removed = [];
        service.DrainGroundDeltas([], removed);
        Assert.Equal([oldest], removed);
    }

    [Fact]
    public void Pickup_ChecksInOrder_ExistsThenClaimThenRangeThenBagThenBudget()
    {
        PlayerInventoryService service = Service();
        Assert.True(Drop(service, 0, Alice));
        long id = All(service).Single().ItemInstanceId;

        Assert.Equal(InventoryEquipOutcome.ItemGone, service.TryPickUpGround(Alice, id + 100, Spot, Start));
        // Claim before range: a stranger far away is told it is not theirs.
        Assert.Equal(InventoryEquipOutcome.NotYours, service.TryPickUpGround(Bob, id, Spot + new Vector3(50f, 0f, 0f), Start));
        Assert.Equal(InventoryEquipOutcome.TooFar, service.TryPickUpGround(Alice, id, Spot + new Vector3(50f, 0f, 0f), Start));

        // A full bag is checked before the budget, and the budget is last: a refusal changes nothing.
        service.FillBagLeaving(Alice, freeCells: 0);
        int spends = 0;
        service.ActionGate = () => { spends++; return false; };
        Assert.Equal(InventoryEquipOutcome.BackpackFull, service.TryPickUpGround(Alice, id, Spot, Start));
        Assert.Equal(0, spends);
        Assert.True(service.IsOnGround(id));

        service.ActionGate = null;
        service.FillBagLeaving(Bob, freeCells: 4);
        service.ActionGate = () => { spends++; return false; };
        Assert.Equal(InventoryEquipOutcome.Throttled, service.TryPickUpGround(Bob, id, Spot, Start + PlayerInventoryService.GroundClaimTicks));
        Assert.Equal(1, spends);
        Assert.True(service.IsOnGround(id));
        Assert.Equal(4, service.BuildSnapshot(Bob).FreeCells());
    }

    [Fact]
    public void Pickup_MovesTheItemIntoTheBag_OnceAndOnlyOnce()
    {
        PlayerInventoryService service = Service();
        Assert.True(Drop(service, 0, Alice));
        long id = All(service).Single().ItemInstanceId;
        int free = service.BuildSnapshot(Alice).FreeCells();
        service.DrainGroundDeltas([], []);

        Assert.Equal(InventoryEquipOutcome.Equipped, service.TryPickUpGround(Alice, id, Spot, Start));
        Assert.Equal(InventoryEquipOutcome.ItemGone, service.TryPickUpGround(Alice, id, Spot, Start));
        Assert.Equal(InventoryEquipOutcome.ItemGone, service.TryPickUpGround(Bob, id, Spot, Start));

        PlayerInventorySnapshot bag = service.BuildSnapshot(Alice);
        Assert.Equal(free - 1, bag.FreeCells());
        Assert.Single(bag.AllItems(), item => item.ItemInstanceId == id);
        Assert.DoesNotContain(service.BuildSnapshot(Bob).AllItems(), item => item.ItemInstanceId == id);
        Assert.Equal(0, service.GroundItemCount);
        List<long> removed = [];
        service.DrainGroundDeltas([], removed);
        Assert.Equal([id], removed);
    }

    [Fact]
    public void TheClaim_LapsAfterThirtySeconds_AndIsReportedAsAnUpsertWithNoClaimant()
    {
        PlayerInventoryService service = Service();
        Assert.True(Drop(service, 0, Alice));
        long id = All(service).Single().ItemInstanceId;
        service.DrainGroundDeltas([], []);
        ulong lapse = Start + PlayerInventoryService.GroundClaimTicks;

        service.StepGroundItems(lapse - 1);
        Assert.False(service.HasPendingGroundDeltas);
        Assert.Equal(InventoryEquipOutcome.NotYours, service.TryPickUpGround(Bob, id, Spot, lapse - 1));

        service.StepGroundItems(lapse);

        List<GroundItemSnapshot> upserts = [];
        service.DrainGroundDeltas(upserts, []);
        GroundItemSnapshot item = Assert.Single(upserts);
        Assert.Equal(0, item.ClaimantActorId);
        Assert.Equal(0UL, item.ClaimExpiresTick);
        Assert.Equal(InventoryEquipOutcome.Equipped, service.TryPickUpGround(Bob, id, Spot, lapse));
    }

    [Fact]
    public void AnItem_DespawnsAfterFiveMinutes_AndCannotBePickedUpAfterwards()
    {
        PlayerInventoryService service = Service();
        Assert.True(Drop(service, 0, Alice));
        long id = All(service).Single().ItemInstanceId;
        service.DrainGroundDeltas([], []);
        ulong gone = Start + PlayerInventoryService.GroundDespawnTicks;

        service.StepGroundItems(gone - 1);
        Assert.True(service.IsOnGround(id));

        service.StepGroundItems(gone);

        Assert.False(service.IsOnGround(id));
        List<long> removed = [];
        service.DrainGroundDeltas([], removed);
        Assert.Equal([id], removed);
        Assert.Equal(InventoryEquipOutcome.ItemGone, service.TryPickUpGround(Alice, id, Spot, gone));
    }

    [Fact]
    public void Restore_PutsItemsBack_WithNoClaim_ReservesTheirIds_AndKeepsTheNewestWithinTheCap()
    {
        PlayerInventoryService source = Service();
        for (int i = 0; i < 3; i++)
        {
            Assert.True(Drop(source, i, Alice));
        }

        List<PersistedItemInstance> rows = [];
        PlayerInventoryService restored = new(worldPersistence: null, writes: null, objectIds: new ObjectIdAllocator());
        foreach (GroundItemSnapshot item in All(source))
        {
            rows.Add(new PersistedItemInstance(
                item.ItemInstanceId, item.ItemKind, "Band", "visual", null, item.Position, "Ground", 1, DateTime.UtcNow, DateTime.UtcNow));
        }

        // A row with no position, one with a hand-edited NaN and one in another state are not ground items.
        rows.Add(rows[0] with { ItemInstanceId = 90, WorldPosition = null });
        rows.Add(rows[0] with { ItemInstanceId = 91, WorldPosition = new Vector3(float.NaN, 0f, 0f) });
        rows.Add(rows[0] with { ItemInstanceId = 92, State = "Inventory" });

        restored.RestoreGroundItems(rows, Start);

        List<GroundItemSnapshot> back = All(restored);
        Assert.Equal(All(source).Select(item => item.ItemInstanceId), back.Select(item => item.ItemInstanceId));
        Assert.All(back, item => Assert.Equal(0, item.ClaimantActorId));
        Assert.True(restored.NextItemInstanceId > rows.Take(3).Max(row => row.ItemInstanceId));

        // The despawn timer restarted at the restore tick.
        restored.StepGroundItems(Start + PlayerInventoryService.GroundDespawnTicks - 1);
        Assert.Equal(3, restored.GroundItemCount);
        restored.StepGroundItems(Start + PlayerInventoryService.GroundDespawnTicks);
        Assert.Equal(0, restored.GroundItemCount);

        // Past the cap the newest 256 stay.
        PlayerInventoryService crowded = new(worldPersistence: null, writes: null, objectIds: new ObjectIdAllocator());
        List<PersistedItemInstance> many = [];
        for (int i = 1; i <= PlayerInventoryService.MaxGroundItems + 5; i++)
        {
            many.Add(rows[0] with { ItemInstanceId = i });
        }

        crowded.RestoreGroundItems(many, Start);
        Assert.Equal(PlayerInventoryService.MaxGroundItems, crowded.GroundItemCount);
        Assert.False(crowded.IsOnGround(5));
        Assert.True(crowded.IsOnGround(6));
    }

    // ---- the player's own drop (TryDropToGround) ----

    private static InventorySlotSnapshot Granted(PlayerInventoryService service, string player, int seed = 0)
    {
        Assert.Equal(InventoryEquipOutcome.Equipped, service.TryGrantRolled(player, Kind, ItemRarity.Common, new ItemRollSeed(7, 7, 7, 7, seed)));
        return service.BuildSnapshot(player).Slots.Last(slot => slot.Item?.ItemKind == Kind);
    }

    [Fact]
    public void APlayerDrop_LeavesTheBag_AppearsOnceAsAnUpsertAtTheirPosition_WithTheDroppersClaim()
    {
        PlayerInventoryService service = Service();
        InventorySlotSnapshot slot = Granted(service, Alice);
        long id = slot.Item!.Value.ItemInstanceId;
        int free = service.BuildSnapshot(Alice).FreeCells();
        service.DrainGroundDeltas([], []);

        Assert.Equal(InventoryEquipOutcome.Equipped, service.TryDropToGround(Alice, slot.SlotIndex, id, Spot, 7, Start));

        PlayerInventorySnapshot bag = service.BuildSnapshot(Alice);
        Assert.DoesNotContain(bag.AllItems(), item => item.ItemInstanceId == id);
        Assert.Equal(free + 1, bag.FreeCells());
        Assert.True(bag.BoardIsConsistent());
        GroundItemSnapshot ground = Assert.Single(All(service));
        Assert.Equal(id, ground.ItemInstanceId);
        Assert.Equal(Kind, ground.ItemKind);
        Assert.Equal(Spot, ground.Position);
        Assert.Equal(7, ground.ClaimantActorId);
        Assert.Equal(Start + PlayerInventoryService.GroundClaimTicks, ground.ClaimExpiresTick);
        List<GroundItemSnapshot> upserts = [];
        List<long> removed = [];
        service.DrainGroundDeltas(upserts, removed);
        Assert.Equal(ground, Assert.Single(upserts));
        Assert.Empty(removed);
        Assert.False(service.HasPendingGroundDeltas);
    }

    [Fact]
    public void ADrop_StoresTheGivenPosition_PerItem_NotTheOriginNorASharedDefault()
    {
        // The service takes the position the server read from the player's own authoritative
        // state; it must place the item there and nowhere else (never the origin, never Spot).
        // This pins only "the service uses the position it is given" -- that the position comes
        // from the player rather than the client is pinned end to end in GroundItemsLoopbackTests.
        PlayerInventoryService service = Service();
        Vector3 here = new(-37.25f, 4.5f, 912.75f);
        Vector3 there = new(481.5f, -2f, -63.125f);
        InventorySlotSnapshot first = Granted(service, Alice, seed: 0);
        InventorySlotSnapshot second = Granted(service, Alice, seed: 1);

        Assert.Equal(InventoryEquipOutcome.Equipped, service.TryDropToGround(Alice, first.SlotIndex, first.Item!.Value.ItemInstanceId, here, 7, Start));
        Assert.Equal(InventoryEquipOutcome.Equipped, service.TryDropToGround(Alice, second.SlotIndex, second.Item!.Value.ItemInstanceId, there, 7, Start + 100));

        Dictionary<long, GroundItemSnapshot> ground = All(service).ToDictionary(item => item.ItemInstanceId);
        Assert.Equal(2, ground.Count);
        Assert.Equal(here, ground[first.Item.Value.ItemInstanceId].Position);
        Assert.Equal(there, ground[second.Item.Value.ItemInstanceId].Position);
        Assert.All(ground.Values, item =>
        {
            Assert.NotEqual(Vector3.Zero, item.Position);
            Assert.NotEqual(Spot, item.Position);
        });
    }

    [Fact]
    public void TheDropper_PicksItStraightBackUp_AndAnotherPlayerIsRefusedWhileTheClaimHolds()
    {
        PlayerInventoryService service = Service();
        InventorySlotSnapshot slot = Granted(service, Alice);
        long id = slot.Item!.Value.ItemInstanceId;
        Assert.Equal(InventoryEquipOutcome.Equipped, service.TryDropToGround(Alice, slot.SlotIndex, id, Spot, 7, Start));

        Assert.Equal(InventoryEquipOutcome.NotYours, service.TryPickUpGround(Bob, id, Spot, Start + 1));
        Assert.True(service.IsOnGround(id));
        Assert.Equal(InventoryEquipOutcome.Equipped, service.TryPickUpGround(Alice, id, Spot, Start + 1));

        Assert.Single(service.BuildSnapshot(Alice).AllItems(), item => item.ItemInstanceId == id);
        Assert.Equal(0, service.GroundItemCount);
    }

    [Fact]
    public void ADropOnAFullGround_DespawnsTheOldest_InsteadOfFailing()
    {
        PlayerInventoryService service = Service();
        // Unclaimed loot, because a player drop now evicts only an item nobody else has claimed. This
        // test used to fill the ground with items claimed by Bob, which is the case that is refused with
        // NoRoom instead (GroundItemCapDropTests covers that, and the dropper's own claims staying
        // evictable). The eviction path this test pins is unchanged.
        for (int i = 0; i < PlayerInventoryService.MaxGroundItems; i++)
        {
            Assert.True(Drop(service, i, null));
        }

        long oldest = All(service)[0].ItemInstanceId;
        InventorySlotSnapshot slot = Granted(service, Alice);
        long id = slot.Item!.Value.ItemInstanceId;
        service.DrainGroundDeltas([], []);

        Assert.Equal(InventoryEquipOutcome.Equipped, service.TryDropToGround(Alice, slot.SlotIndex, id, Spot, 7, Start));

        Assert.Equal(PlayerInventoryService.MaxGroundItems, service.GroundItemCount);
        Assert.False(service.IsOnGround(oldest));
        Assert.True(service.IsOnGround(id));
        List<long> removed = [];
        service.DrainGroundDeltas([], removed);
        Assert.Equal([oldest], removed);
    }

    [Fact]
    public void ADropRejection_ChangesNothing_AndFollowsTheCheckOrder()
    {
        PlayerInventoryService service = Service();
        InventorySlotSnapshot slot = Granted(service, Alice);
        long id = slot.Item!.Value.ItemInstanceId;
        service.DrainGroundDeltas([], []);
        string layout = service.BuildSnapshot(Alice).Layout();
        long next = service.NextItemInstanceId;

        // An empty anchor (the last cell holds nothing), a mismatched id, a non-finite position.
        PlayerInventorySnapshot snapshot = service.BuildSnapshot(Alice);
        int empty = Enumerable.Range(0, 64).Last(cell => snapshot.ItemAt(cell) == null);
        Assert.Equal(InventoryEquipOutcome.SlotEmpty, service.TryDropToGround(Alice, empty, id, Spot, 7, Start));
        Assert.Equal(InventoryEquipOutcome.ItemMismatch, service.TryDropToGround(Alice, slot.SlotIndex, id + 1000, Spot, 7, Start));
        Assert.Equal(InventoryEquipOutcome.TooFar, service.TryDropToGround(Alice, slot.SlotIndex, id, new Vector3(float.NaN, 0f, 0f), 7, Start));
        Assert.Equal(InventoryEquipOutcome.TooFar, service.TryDropToGround(Alice, slot.SlotIndex, id, new Vector3(0f, float.PositiveInfinity, 0f), 7, Start));
        foreach (int bad in new[] { -1, 64, 100, 200, int.MaxValue })
        {
            Assert.Equal(InventoryEquipOutcome.SlotOutOfRange, service.TryDropToGround(Alice, bad, id, Spot, 7, Start));
        }

        // The budget is last: a refusal by it spends once and changes nothing.
        int spends = 0;
        service.ActionGate = () => { spends++; return false; };
        Assert.Equal(InventoryEquipOutcome.Throttled, service.TryDropToGround(Alice, slot.SlotIndex, id, Spot, 7, Start));
        Assert.Equal(1, spends);
        Assert.Equal(InventoryEquipOutcome.ItemMismatch, service.TryDropToGround(Alice, slot.SlotIndex, id + 1000, Spot, 7, Start));
        Assert.Equal(1, spends);

        Assert.Equal(layout, service.BuildSnapshot(Alice).Layout());
        Assert.Equal(0, service.GroundItemCount);
        Assert.False(service.HasPendingGroundDeltas);
        Assert.Equal(next, service.NextItemInstanceId);
    }

    [Fact]
    public void AWornItem_IsRefusedAsEquipped_WithNothingChanged()
    {
        EquipmentTestWorld world = new();
        world.PutItem(EquipmentTestWorld.MakeItem(50, Alice, "tool.field-blade"));
        world.PutSlot(Alice, 0, 50);
        world.PutSlot(Alice, 100, 50);
        world.PutGrant(Alice, ToolCatalog.CurrentStarterKitVersion);
        PlayerInventoryService service = EquipmentSlotTests.Create(world, out PersistenceWriteQueue writes);
        using (writes)
        {
            Assert.True(service.Ensure(Alice));
            Dictionary<int, long> before = world.SlotsOf(Alice);
            int writesBefore = world.SlotWrites;

            Assert.Equal(InventoryEquipOutcome.ItemEquipped, service.TryDropToGround(Alice, 0, 50, Spot, 7, Start));

            Assert.Equal(before, world.SlotsOf(Alice));
            Assert.Equal(writesBefore, world.SlotWrites);
            Assert.Equal(0, service.GroundItemCount);
            Assert.True(world.HasItem(50));
        }
    }
}
