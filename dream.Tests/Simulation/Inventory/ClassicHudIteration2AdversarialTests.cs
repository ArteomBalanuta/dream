using Xunit;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Determinism;
using Dream.Simulation.Inventory;
using Dream.Simulation.Persistence;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.Stats;
using Dream.Tests.Simulation.Runtime;
using Dream.Tests.Support;

namespace Dream.Tests.Simulation.Inventory;

// Classic HUD iteration 2 verification pass: adversarial play on the footprint board and the twelve worn
// slots, on the service (the stand-in world) and on in-memory runtimes. Each case ends with the same
// invariants: nothing lost, nothing duplicated, the board consistent.
public sealed class ClassicHudIteration2AdversarialTests
{
    private const string Player = "Alice";

    private static PlayerInventoryService Create(EquipmentTestWorld world, out PersistenceWriteQueue writes)
        => EquipmentSlotTests.Create(world, out writes);

    private static readonly string[] Gear =
    [
        "gear.iron-blade", "gear.ward-lantern", "gear.leather-cap", "gear.leather-vest", "gear.iron-gauntlets",
        "gear.iron-boots", "gear.plated-leggings", "gear.ward-pendant", "gear.iron-band", "gear.iron-band",
        "gear.travel-cloak", "gear.ember-wisp",
    ];

    private static List<long> AllIds(PlayerInventorySnapshot s)
        => [.. s.AllItems().Select(i => i.ItemInstanceId).Order()];

    // Destroys the four spare kit items so there is room, keeping the worn hammer.
    private static void ClearKit(PlayerInventoryService inventory)
    {
        foreach (string kind in new[] { "tool.field-blade", "tool.stone-maul", "tool.survey-lantern", "tool.torch" })
        {
            PlayerInventorySnapshot s = inventory.BuildSnapshot(Player);
            int anchor = s.AnchorOf(kind);
            Assert.Equal(InventoryEquipOutcome.Equipped, inventory.TryDestroy(Player, anchor, s.IdAt(anchor)));
        }
    }

    [Fact]
    public void EquipAllTwelve_ThenUnequipIntoANearlyFullBag_AcceptsOnlyWhatFits_AndALostNothing()
    {
        EquipmentTestWorld world = new();
        PlayerInventoryService inventory = Create(world, out PersistenceWriteQueue writes);
        using (writes)
        {
            Assert.True(inventory.Ensure(Player));
            ClearKit(inventory);
            for (int i = 0; i < Gear.Length; i++)
            {
                Assert.Equal(InventoryEquipOutcome.Equipped, inventory.TryGrantRolled(Player, Gear[i], ItemRarity.Common, new ItemRollSeed(1, 2, 3, 4, i)));
            }

            foreach (string kind in Gear)
            {
                Assert.Equal(InventoryEquipOutcome.Equipped, inventory.TryEquip(Player, inventory.AnchorOf(Player, kind)));
            }

            // The hammer the main hand held went back to the bag when the blade took its place: 13 items in all.
            PlayerInventorySnapshot allWorn = inventory.BuildSnapshot(Player);
            Assert.Equal(12, AllItems12(allWorn));
            Assert.Equal(13, AllIds(allWorn).Count);

            // Room for exactly one more cell.
            inventory.FillBagLeaving(Player, freeCells: 1);
            List<long> before = AllIds(inventory.BuildSnapshot(Player));
            int accepted = 0;
            int rejected = 0;
            for (int slot = EquipmentSlots.FirstSlotIndex; slot <= EquipmentSlots.LastSlotIndex; slot++)
            {
                string layoutBefore = inventory.BuildSnapshot(Player).Layout();
                InventoryEquipOutcome outcome = inventory.TryUnequip(Player, slot);
                PlayerInventorySnapshot after = inventory.BuildSnapshot(Player);
                if (outcome == InventoryEquipOutcome.Equipped)
                {
                    accepted++;
                }
                else
                {
                    rejected++;
                    Assert.Equal(InventoryEquipOutcome.BackpackFull, outcome);
                    Assert.Equal(layoutBefore, after.Layout());
                }

                Assert.True(after.BoardIsConsistent());
                Assert.Equal(before, AllIds(after));
            }

            Assert.Equal(1, accepted);
            Assert.Equal(11, rejected);
            Assert.Equal(0, inventory.BuildSnapshot(Player).FreeCells());
        }
    }

    private static int AllItems12(PlayerInventorySnapshot s)
        => (s.EquippedSlot?.Item != null ? 1 : 0) + (s.Equipment ?? []).Count(slot => slot.Item != null);

    [Fact]
    public void ASwapThatLeavesNoRoomForTheDisplacedItem_IsRefused_AndChangesNothing_AndASpearThatFreesRoomIsAccepted()
    {
        EquipmentTestWorld world = new();
        PlayerInventoryService inventory = Create(world, out PersistenceWriteQueue writes);
        using (writes)
        {
            Assert.True(inventory.Ensure(Player));
            // Bag full to the last cell; the kit torch (1x2) is in it, the hammer (2x3) is worn.
            inventory.FillBagLeaving(Player, freeCells: 0);
            string before = inventory.BuildSnapshot(Player).Layout();
            InventoryEquipOutcome torch = inventory.TryEquip(Player, inventory.AnchorOf(Player, "tool.torch"), EquipmentSlots.FirstSlotIndex);
            Assert.NotEqual(InventoryEquipOutcome.Equipped, torch);
            Assert.Equal(before, inventory.BuildSnapshot(Player).Layout());
        }

        // A 2x4 spear frees eight cells, room enough for the hammer.
        EquipmentTestWorld world2 = new();
        PlayerInventoryService inventory2 = Create(world2, out PersistenceWriteQueue writes2);
        using (writes2)
        {
            Assert.True(inventory2.Ensure(Player));
            Assert.Equal(InventoryEquipOutcome.Equipped, inventory2.TryGrantRolled(Player, "gear.hunting-spear", ItemRarity.Common, new ItemRollSeed(1, 1, 1, 1, 1)));
            inventory2.FillBagLeaving(Player, freeCells: 0);
            List<long> ids = AllIds(inventory2.BuildSnapshot(Player));
            Assert.Equal(InventoryEquipOutcome.Equipped, inventory2.TryEquip(Player, inventory2.AnchorOf(Player, "gear.hunting-spear")));
            PlayerInventorySnapshot after = inventory2.BuildSnapshot(Player);
            Assert.True(after.BoardIsConsistent());
            Assert.Equal(ids, AllIds(after));
            Assert.Equal("gear.hunting-spear", after.EquippedSlot!.Value.Item!.Value.ItemKind);
            Assert.Equal(0, after.HeldBackItems().Count());
        }
    }

    [Theory]
    [InlineData(0)]   // top-left
    [InlineData(6)]   // top-right: columns 6 and 7
    [InlineData(32)]  // bottom-left: rows 4 to 7
    [InlineData(38)]  // bottom-right
    public void ASpear_2x4_FitsAtEachEdgeOfTheBoard_AndOneCellFurtherIsRefused(int edgeAnchor)
    {
        EquipmentTestWorld world = new();
        PlayerInventoryService inventory = Create(world, out PersistenceWriteQueue writes);
        using (writes)
        {
            Assert.True(inventory.Ensure(Player));
            ClearKit(inventory);
            Assert.Equal(InventoryEquipOutcome.Equipped, inventory.TryGrantRolled(Player, "gear.hunting-spear", ItemRarity.Common, new ItemRollSeed(1, 1, 1, 1, 1)));
            long spear = inventory.BuildSnapshot(Player).IdAt(inventory.AnchorOf(Player, "gear.hunting-spear"));
            int from = inventory.AnchorOf(Player, "gear.hunting-spear");
            if (from != edgeAnchor)
            {
                Assert.Equal(InventoryEquipOutcome.Equipped, inventory.TryMove(Player, from, edgeAnchor));
            }

            PlayerInventorySnapshot s = inventory.BuildSnapshot(Player);
            Assert.Equal(spear, s.IdAt(edgeAnchor));
            Assert.True(s.BoardIsConsistent());
            Assert.Equal(8, s.OccupiedCells());

            // One column right of the right edge, or one row below the bottom edge, runs off the board.
            int beyond = edgeAnchor is 6 or 38 ? edgeAnchor + 1 : edgeAnchor + 8;
            if (edgeAnchor == 0 || edgeAnchor == 32)
            {
                beyond = edgeAnchor == 0 ? 40 : 40;
            }

            string layout = s.Layout();
            InventoryEquipOutcome outcome = inventory.TryMove(Player, edgeAnchor, beyond);
            if (edgeAnchor is 6 or 38)
            {
                Assert.Equal(InventoryEquipOutcome.NoRoom, outcome);
                Assert.Equal(layout, inventory.BuildSnapshot(Player).Layout());
            }
            else
            {
                // Rows 5 and below cannot hold a 4-tall item: anchor 40 is row 5.
                Assert.Equal(InventoryEquipOutcome.NoRoom, outcome);
                Assert.Equal(layout, inventory.BuildSnapshot(Player).Layout());
            }

            Assert.Equal(InventoryEquipOutcome.NoRoom, inventory.TryMove(Player, edgeAnchor, 63));
            Assert.Equal(layout, inventory.BuildSnapshot(Player).Layout());
        }
    }

    [Fact]
    public void RingSwap_WithTheSameKindInBothWells_ExchangesTheTwoItems_AndTheBagAndStatsDoNotMove()
    {
        EquipmentTestWorld world = new();
        PlayerInventoryService inventory = Create(world, out PersistenceWriteQueue writes);
        using (writes)
        {
            Assert.True(inventory.Ensure(Player));
            // Two bands with different rolls, worn left then right.
            Assert.Equal(InventoryEquipOutcome.Equipped, inventory.TryGrantRolled(Player, "gear.iron-band", ItemRarity.Rare, new ItemRollSeed(1, 2, 3, 4, 10)));
            Assert.Equal(InventoryEquipOutcome.Equipped, inventory.TryGrantRolled(Player, "gear.iron-band", ItemRarity.Common, new ItemRollSeed(9, 8, 7, 6, 11)));
            int left = EquipmentSlots.ToSlotIndex(EquipmentSlot.RingLeft);
            int right = EquipmentSlots.ToSlotIndex(EquipmentSlot.RingRight);
            Assert.Equal(InventoryEquipOutcome.Equipped, inventory.TryEquip(Player, inventory.AnchorOf(Player, "gear.iron-band")));
            Assert.Equal(InventoryEquipOutcome.Equipped, inventory.TryEquip(Player, inventory.AnchorOf(Player, "gear.iron-band")));
            PlayerInventorySnapshot s = inventory.BuildSnapshot(Player);
            long a = s.WornId(left);
            long b = s.WornId(right);
            Assert.NotEqual(0, a);
            Assert.NotEqual(0, b);
            Assert.NotEqual(a, b);
            ulong digest = inventory.ResolveModifierDigest(Player);
            string bag = string.Join(',', s.BagItems().Select(x => $"{x.SlotIndex}={x.Item!.Value.ItemInstanceId}"));

            Assert.Equal(InventoryEquipOutcome.Equipped, inventory.TryEquip(Player, left, right));
            PlayerInventorySnapshot swapped = inventory.BuildSnapshot(Player);
            Assert.Equal(b, swapped.WornId(left));
            Assert.Equal(a, swapped.WornId(right));
            Assert.Equal(bag, string.Join(',', swapped.BagItems().Select(x => $"{x.SlotIndex}={x.Item!.Value.ItemInstanceId}")));
            // The digest follows the item and its slot (ModifierDigest_FollowsTheItemTheSlotAndTheValue), so
            // exchanging two different rolls changes it; the totals of the modifiers do not change.
            Assert.NotEqual(digest, inventory.ResolveModifierDigest(Player));
            Assert.Equal(StatSum(s), StatSum(swapped));

            Assert.Equal(InventoryEquipOutcome.Equipped, inventory.TryEquip(Player, left, right));
            Assert.Equal(a, inventory.BuildSnapshot(Player).WornId(left));
            Assert.Equal(digest, inventory.ResolveModifierDigest(Player));
        }
    }

    private static string StatSum(PlayerInventorySnapshot s)
        => string.Join(
            ",",
            s.AllItems().Where(i => i.Modifiers != null && !s.BagItems().Any(b => b.Item!.Value.ItemInstanceId == i.ItemInstanceId))
                .SelectMany(i => i.Modifiers!)
                .GroupBy(m => (m.Stat, m.Op))
                .OrderBy(g => g.Key.Stat).ThenBy(g => g.Key.Op)
                .Select(g => $"{g.Key.Stat}/{g.Key.Op}={g.Sum(m => m.Value)}"));

    // ---- held-back rows on the runtime ----

    private static readonly PlayerSessionId Seat = new("it2-seat");

    // Sixteen blades as migration 0010 leaves a full legacy backpack: eight placed, eight held back.
    private static WorldSessionRuntime RuntimeWithHeldBack(out string key, out ulong tick)
    {
        WorldSessionRuntime runtime = WaveIRuntimeKit.CreateRuntime();
        ConnectedPlayer player = runtime.AttachRemoteSession(Seat, "Seat");
        key = player.CharacterKey.Value;
        List<PersistedItemInstance> items = [];
        List<PersistedInventorySlot> slots = [];
        for (int i = 0; i < 16; i++)
        {
            long id = 500 + i;
            items.Add(EquipmentTestWorld.MakeItem(id, key, "gear.iron-blade"));
            slots.Add(new PersistedInventorySlot(key, ((i / 4) * 16) + ((i % 4) * 2), id, DateTime.UtcNow));
        }

        runtime.InventoryService.ApplyLoaded(key, new PersistedPlayerInventory(slots, items, ToolCatalog.CurrentStarterKitVersion));
        Assert.True(runtime.InventoryService.Ensure(key));
        tick = 0;
        WaveIRuntimeKit.Tick(runtime, ref tick, 3);
        return runtime;
    }

    private static int HeldBack(WorldSessionRuntime runtime, string key) => runtime.InventoryService.BuildSnapshot(key).HeldBackItems().Count();

    [Fact]
    public void HeldBackItems_DrainDuringAFight_AsRoomAppears_AndNothingIsLostOrDuplicated()
    {
        using WorldSessionRuntime runtime = RuntimeWithHeldBack(out string key, out ulong tick);
        PlayerInventoryService inventory = runtime.InventoryService;
        PlayerInventorySnapshot start = inventory.BuildSnapshot(key);
        Assert.Equal(8, start.HeldBackItems().Count());
        List<long> ids = AllIds(start);
        Assert.Equal(16, ids.Count);

        // A wolf at the player's feet, bites landing, while the player destroys blades one after another.
        WaveIRuntimeKit.EndProtection(runtime, Seat);
        IAuthoritativePlayerTarget target = WaveIRuntimeKit.Target(runtime, Seat.Value);
        int destroyed = 0;
        for (int round = 0; round < 6; round++)
        {
            Dream.Simulation.Enemies.EnemyActor wolf = runtime.Enemies.ActiveActors.First(e => e.IsActive);
            wolf.SetPosition(new OpenTK.Mathematics.Vector3(target.Position.X + 1f, target.Position.Y, target.Position.Z));
            WaveIRuntimeKit.Tick(runtime, ref tick, 20);
            target.Combatant.Health.SetAuthoritativeState(target.Combatant.Health.MaxHealth, target.Combatant.Health.MaxHealth);
            PlayerInventorySnapshot s = inventory.BuildSnapshot(key);
            int held = s.HeldBackItems().Count();
            InventorySlotSnapshot victim = s.BagItems().First(x => x.Item!.Value.ItemKind == "gear.iron-blade");
            Assert.Equal(InventoryEquipOutcome.Equipped, inventory.TryDestroy(key, victim.SlotIndex, victim.Item!.Value.ItemInstanceId));
            destroyed++;
            PlayerInventorySnapshot after = inventory.BuildSnapshot(key);
            Assert.True(after.BoardIsConsistent());
            Assert.Equal(Math.Max(0, held - 1), after.HeldBackItems().Count());
            ids.Remove(victim.Item.Value.ItemInstanceId);
            Assert.Equal(ids, AllIds(after));
            WaveIRuntimeKit.Tick(runtime, ref tick, 8); // refill the action budget
        }

        Assert.Equal(6, destroyed);
        Assert.Equal(2, HeldBack(runtime, key));
    }

    [Fact]
    public void ADeathWithHeldBackItems_KeepsEveryItemAndEveryHeldBackRow_ThroughTheRespawn()
    {
        using WorldSessionRuntime runtime = RuntimeWithHeldBack(out string key, out ulong tick);
        string layout = runtime.InventoryService.BuildSnapshot(key).Layout();
        List<long> ids = AllIds(runtime.InventoryService.BuildSnapshot(key));
        WaveIRuntimeKit.EndProtection(runtime, Seat);
        WaveIRuntimeKit.Kill(runtime, Seat.Value);
        WaveIRuntimeKit.Tick(runtime, ref tick, 2);
        Assert.False(WaveIRuntimeKit.Target(runtime, Seat.Value).IsAlive);
        Assert.Equal(layout, runtime.InventoryService.BuildSnapshot(key).Layout());
        WaveIRuntimeKit.Tick(runtime, ref tick, PlayerRespawnRules.DelayTicks + 2);
        Assert.True(WaveIRuntimeKit.Target(runtime, Seat.Value).IsAlive);
        PlayerInventorySnapshot after = runtime.InventoryService.BuildSnapshot(key);
        Assert.Equal(layout, after.Layout());
        Assert.Equal(ids, AllIds(after));
        Assert.Equal(8, after.HeldBackItems().Count());
    }

    [Fact]
    public void LeavingAndRejoiningWithHeldBackItems_ReturnsTheSameInventory_OnTheWireToo()
    {
        using WorldSessionRuntime runtime = RuntimeWithHeldBack(out string key, out ulong tick);
        PlayerInventorySnapshot before = runtime.InventoryService.BuildSnapshot(key);
        runtime.DetachRemoteSession(Seat);
        WaveIRuntimeKit.Tick(runtime, ref tick, 2);
        ConnectedPlayer again = runtime.AttachRemoteSession(Seat, "Seat");
        Assert.Equal(key, again.CharacterKey.Value);
        PlayerInventorySnapshot after = runtime.InventoryService.BuildSnapshot(key);
        Assert.Equal(before.Layout(), after.Layout());

        // The join's inventory goes over the codec as a world transfer carries it, held-back rows included.
        Dream.Multiplayer.Protocol.TransportJsonCodec codec = new();
        byte[] frame = codec.Encode(
            new Dream.Multiplayer.Protocol.TransportEnvelope(Dream.Multiplayer.Protocol.TransportEnvelope.CurrentProtocolVersion, "server.join-accepted", 1),
            new Dream.Multiplayer.Protocol.JoinAcceptedMessage("s", 1, 2, null, Inventory: after));
        var decodedMessage = Assert.IsType<Dream.Multiplayer.Protocol.JoinAcceptedMessage>(codec.Decode(frame).Message);
        PlayerInventorySnapshot decoded = decodedMessage.Inventory!.Value;
        Assert.Equal(after.HeldBackItems().Select(s => s.Item!.Value.ItemInstanceId), decoded.HeldBackItems().Select(s => s.Item!.Value.ItemInstanceId));
        Assert.Equal(8, decoded.HeldBackItems().Count());
        Assert.Equal(after.BagItems().Select(s => (s.SlotIndex, s.Item!.Value.ItemInstanceId)), decoded.BagItems().Select(s => (s.SlotIndex, s.Item!.Value.ItemInstanceId)));
    }
}
