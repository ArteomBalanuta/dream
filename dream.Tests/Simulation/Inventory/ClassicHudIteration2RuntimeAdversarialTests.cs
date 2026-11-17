using Microsoft.Data.Sqlite;
using Xunit;
using Dream.Multiplayer.Sessions;
using Dream.Server;
using Dream.Server.Persistence;
using Dream.Simulation.Determinism;
using Dream.Simulation.Inventory;
using Dream.Simulation.Persistence;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Tests.Server.Persistence.Migrations;
using Dream.Tests.Simulation.Runtime;
using Dream.Tests.Support;

namespace Dream.Tests.Simulation.Inventory;

// Classic HUD iteration 2 verification pass, part 2: the cases that need a real database (a restart with
// all twelve slots worn), a kill that drops into a full bag, and two lockstep servers looting the new kinds.
public sealed class ClassicHudIteration2RuntimeAdversarialTests
{
    private const string Player = "Alice";

    private static readonly string[] Gear =
    [
        "gear.iron-blade", "gear.ward-lantern", "gear.leather-cap", "gear.leather-vest", "gear.iron-gauntlets",
        "gear.iron-boots", "gear.plated-leggings", "gear.ward-pendant", "gear.iron-band", "gear.iron-band",
        "gear.travel-cloak", "gear.ember-wisp",
    ];

    private static readonly string[] NewKinds = ["gear.hunting-spear", "gear.gossamer-wings", "gear.ember-wisp", "gear.travel-cloak"];

    private static string Worn(PlayerInventorySnapshot s)
    {
        List<InventoryItemSnapshot> worn = [];
        List<string> parts = [];
        void Add(int slot, InventoryItemSnapshot? item)
        {
            if (item is { } i)
            {
                parts.Add($"{slot}:{i.ItemInstanceId}:{i.ItemKind}:" + string.Join('/', (i.Modifiers ?? []).Select(m => $"{m.Stat}{m.Op}{BitConverter.SingleToInt32Bits(m.Value)}")));
            }
        }

        Add(s.EquippedSlot!.Value.SlotIndex, s.EquippedSlot.Value.Item);
        foreach (InventorySlotSnapshot slot in s.Equipment ?? [])
        {
            Add(slot.SlotIndex, slot.Item);
        }

        _ = worn;
        return string.Join(';', parts.Order());
    }

    // Sum of every modifier of the worn items by stat and op, the number the stat sheet adds up.
    private static string Totals(PlayerInventorySnapshot s)
    {
        IEnumerable<InventoryItemSnapshot> worn = new[] { s.EquippedSlot!.Value.Item }
            .Concat((s.Equipment ?? []).Select(slot => slot.Item))
            .Where(i => i != null).Select(i => i!.Value);
        return string.Join(
            ',',
            worn.SelectMany(i => i.Modifiers ?? [])
                .GroupBy(m => (m.Stat, m.Op)).OrderBy(g => g.Key.Stat).ThenBy(g => g.Key.Op)
                .Select(g => $"{g.Key.Stat}/{g.Key.Op}={g.Sum(m => m.Value):R}"));
    }

    [Fact]
    public void AllTwelveWorn_KeepTheirItemsRollsAndStatTotals_AcrossARestartOnTheProductionRepository()
    {
        using MigrationTestDatabase db = new();
        db.Schema.EnsureSchema();
        SqlitePools.Release(db.DatabasePath);
        db.ExecuteScript("INSERT INTO player_profiles (player_name, display_name) VALUES ('Alice', 'Alice');");
        string worn;
        string totals;
        ulong digest;
        using (WaveIRuntimeKit.RealRun run = new(db))
        {
            run.Runtime.AttachRemoteSession(new PlayerSessionId("s1"), Player, new Dream.Simulation.Characters.CharacterKey(Player));
            run.Runtime.RestorePersistedPlayerState(new PlayerSessionId("s1"), Player, Player, account: null);
            PlayerInventoryService inventory = run.Runtime.InventoryService;
            Assert.True(inventory.Ensure(Player));
            foreach (string spare in new[] { "tool.field-blade", "tool.stone-maul" })
            {
                PlayerInventorySnapshot kit = inventory.BuildSnapshot(Player);
                int anchor = kit.AnchorOf(spare);
                Assert.Equal(InventoryEquipOutcome.Equipped, inventory.TryDestroy(Player, anchor, kit.IdAt(anchor)));
            }

            for (int i = 0; i < Gear.Length; i++)
            {
                Assert.Equal(InventoryEquipOutcome.Equipped, inventory.TryGrantRolled(Player, Gear[i], ItemRarity.Rare, new ItemRollSeed(3, 1, 4, 1, i)));
            }

            foreach (string kind in Gear)
            {
                Assert.Equal(InventoryEquipOutcome.Equipped, inventory.TryEquip(Player, inventory.AnchorOf(Player, kind)));
            }

            PlayerInventorySnapshot s = inventory.BuildSnapshot(Player);
            worn = Worn(s);
            totals = Totals(s);
            digest = inventory.ResolveModifierDigest(Player);
            Assert.Equal(12, worn.Split(';').Length);
            Assert.NotEqual(0UL, digest);
            run.Runtime.DetachRemoteSession(new PlayerSessionId("s1"));
        }

        SqlitePools.Release(db.DatabasePath);
        using WaveIRuntimeKit.RealRun restarted = new(db);
        restarted.Runtime.AttachRemoteSession(new PlayerSessionId("s2"), Player, new Dream.Simulation.Characters.CharacterKey(Player));
        restarted.Runtime.RestorePersistedPlayerState(new PlayerSessionId("s2"), Player, Player, account: null);
        Assert.True(restarted.Runtime.InventoryService.Ensure(Player));
        PlayerInventorySnapshot after = restarted.Runtime.InventoryService.BuildSnapshot(Player);
        Assert.Equal(worn, Worn(after));
        Assert.Equal(totals, Totals(after));
        Assert.Equal(digest, restarted.Runtime.InventoryService.ResolveModifierDigest(Player));
        Assert.True(after.BoardIsConsistent());
    }

    [Fact]
    public void ASpearDrop_IntoABagWithRoomButNo2x4Hole_IsLostWithANotice_AndChangesNothing()
    {
        PlayerSessionId killer = new("it2-killer");
        using WorldSessionRuntime runtime = WaveIRuntimeKit.CreateRuntime();
        runtime.LootContent = WaveIRuntimeKit.AlwaysDropCatalog(drops: 1, kinds: ["gear.hunting-spear"]);
        ConnectedPlayer player = runtime.AttachRemoteSession(killer, "Killer");
        string key = player.CharacterKey.Value;
        Assert.True(runtime.InventoryService.Ensure(key));
        WaveIRuntimeKit.Target(runtime, killer.Value).SetWorldPosition(WaveIRuntimeKit.FarStart);
        ulong tick = 0;
        WaveIRuntimeKit.Tick(runtime, ref tick, 3);
        WaveIRuntimeKit.EnterCombat(runtime, killer);
        // Seven free cells: room for seven rings and no 2x4 hole anywhere.
        runtime.InventoryService.FillBagLeaving(key, freeCells: 7);
        string layout = runtime.InventoryService.BuildSnapshot(key).Layout();

        Dream.Multiplayer.Protocol.AuthoritativeActionResult hit = WaveIRuntimeKit.HitWolf(runtime, killer, actionSequence: 1);

        Assert.True(Assert.IsType<Dream.Multiplayer.Protocol.CombatResolutionResult>(hit.Payload).TargetDied);
        Assert.Equal(layout, runtime.InventoryService.BuildSnapshot(key).Layout());
        List<SessionInventoryPush> pushes = [];
        runtime.DrainInventoryPushesInto(pushes);
        Dream.Multiplayer.Protocol.InventoryPushMessage push = Assert.Single(pushes).Message;
        Assert.Null(push.Inventory);
        Assert.True(Assert.Single(push.Notices).Lost);
    }

    // One server's run: three kills 120 ticks apart with two drops each of the new kinds, every drop equipped
    // when it lands. The checksum is read after every tick.
    private static (List<ulong> Checksums, string Final) Lockstep(ulong seed)
    {
        PlayerSessionId killer = new("it2-lockstep");
        using WorldSessionRuntime runtime = new(WorldDefaults.StartupFloorSize, WaveIRuntimeKit.FarStart, new SceneEventHub(), simulationSeed: seed);
        runtime.LootContent = WaveIRuntimeKit.AlwaysDropCatalog(drops: 2, common: 1, uncommon: 1, rare: 1, kinds: NewKinds);
        ConnectedPlayer player = runtime.AttachRemoteSession(killer, killer.Value);
        string key = player.CharacterKey.Value;
        Assert.True(runtime.InventoryService.Ensure(key));
        ulong tick = 0;
        List<ulong> sums = [];
        void Step(int n)
        {
            for (int i = 0; i < n; i++)
            {
                WaveIRuntimeKit.Tick(runtime, ref tick, 1);
                sums.Add(SimulationStateChecksum.Compute(runtime));
            }
        }

        Step(3);
        WaveIRuntimeKit.EnterCombat(runtime, killer);
        for (ulong sequence = 1; sequence <= 3; sequence++)
        {
            Step(120);
            Assert.True(WaveIRuntimeKit.HitWolf(runtime, killer, sequence).Accepted);
            Step(1);
            // Equip whatever dropped, to its first slot.
            PlayerInventorySnapshot s = runtime.InventoryService.BuildSnapshot(key);
            foreach (InventorySlotSnapshot slot in s.BagItems().Where(x => NewKinds.Contains(x.Item!.Value.ItemKind)).ToList())
            {
                runtime.InventoryService.TryEquip(key, slot.SlotIndex);
            }

            Step(1);
        }

        PlayerInventorySnapshot end = runtime.InventoryService.BuildSnapshot(key);
        Assert.True(end.BoardIsConsistent());
        return (sums, end.Layout() + "|" + Worn(end));
    }

    [Fact]
    public void TwoServers_LootingTheNewKinds_AndEquippingThem_KeepEqualChecksumsEveryTick_AndEqualInventories()
    {
        (List<ulong> a, string finalA) = Lockstep(seed: 77);
        (List<ulong> b, string finalB) = Lockstep(seed: 77);
        Assert.Equal(a.Count, b.Count);
        for (int i = 0; i < a.Count; i++)
        {
            Assert.True(a[i] == b[i], $"the servers diverged at tick {i + 1}");
        }

        Assert.Equal(finalA, finalB);
        Assert.Contains("gear.", finalA, StringComparison.Ordinal);
        // The loot changed the state: a different seed gives different items, so the check is not vacuous.
        (_, string other) = Lockstep(seed: 78);
        Assert.NotEqual(finalA, other);
    }
}
