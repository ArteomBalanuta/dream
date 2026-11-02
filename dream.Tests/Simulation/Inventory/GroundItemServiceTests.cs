using OpenTK.Mathematics;
using Xunit;
using Dream.Multiplayer.Protocol;
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
}
