using System.Reflection;
using Dream.Client.Scenes;
using Dream.Multiplayer.Protocol;
using Dream.Rendering.Hud;
using Dream.Server;
using Dream.Server.Persistence;
using Dream.Simulation.Inventory;
using Dream.Simulation.Persistence;
using Dream.Simulation.Stats;
using Dream.Simulation.World;
using Dream.Tests.Server.Persistence.Migrations;
using Dream.Tests.Support;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Dream.Tests.Server.Persistence;

// Wave H glue: H1's move, unequip and destroy cases on the production repository
// (ServerPersistenceBundle.World, the facade the runtime writes through), which InventoryActionTests
// runs on the EquipmentTestWorld stand-in. Every case reads the rows back from a reopened bundle with a
// new service, as a restart does, and no queued write may have failed (the queue only counts those).
// Classic HUD iteration 2: the bag is a footprint board and a worn item has no bag row, so the rows are
// the guarded deletes and upserts the footprint rules queue. A new player's kit: the hammer in the main
// hand, the blade at anchor 2, the maul at 4, the lantern at 6 and the torch at 7.
public sealed class EquipmentSqliteActionTests
{
    private const string Player = "Alice";

    private static MigrationTestDatabase Migrated()
    {
        MigrationTestDatabase db = new();
        db.Schema.EnsureSchema();
        SqliteConnection.ClearAllPools();
        db.ExecuteScript("INSERT INTO player_profiles (player_name, display_name) VALUES ('Alice', 'Alice'), ('Bob', 'Bob');");
        return db;
    }

    // One server run: the bundle holds the world's lease, so two runs never overlap.
    private sealed class Run : IDisposable
    {
        private readonly ServerPersistenceBundle _bundle;
        private readonly PersistenceWriteQueue _writes = PersistenceWriteQueue.Inline();

        public Run(MigrationTestDatabase db)
        {
            _bundle = ServerBootstrap.CreatePersistenceBundle(db.DatabasePath, MigrationTestDatabase.InitScriptPath);
            Inventory = new PlayerInventoryService(_bundle.World, _writes, new ObjectIdAllocator(_bundle.World.LoadNextObjectId()));
        }

        public PlayerInventoryService Inventory { get; }

        public IWorldPersistenceRepository World => _bundle.World;

        public Dictionary<int, long> Rows(string player = Player) =>
            World.LoadPlayerInventory(player).Slots.ToDictionary(slot => slot.SlotIndex, slot => slot.ItemInstanceId);

        public bool HasItem(long id) => World.LoadItemInstances().Any(item => item.ItemInstanceId == id);

        public void Dispose()
        {
            Assert.True(_writes.Flush(TimeSpan.FromSeconds(30)));
            Assert.Equal(0, _writes.FailedWrites);
            _writes.Dispose();
            _bundle.Dispose();
            SqliteConnection.ClearAllPools();
        }
    }

    private static void Give(IWorldPersistenceRepository world, string player, int slot, long id, string kind, ItemRoll? roll = null)
    {
        world.SaveItemInstance(EquipmentTestWorld.MakeItem(id, player, kind, roll));
        world.SaveInventorySlot(new PersistedInventorySlot(player, slot, id, DateTime.UtcNow));
    }

    private static long MainHandId(PlayerInventoryService inventory) =>
        inventory.BuildSnapshot(Player).EquippedSlot!.Value.Item?.ItemInstanceId ?? 0;

    [Fact]
    public void TheProductionFacade_ImplementsEveryRepositoryMember_NoInterfaceDefaultStandsIn()
    {
        // The repository interfaces have no default bodies (a missing forward does not compile), but a
        // default member added later would silently drop the write on the facade. This catches it.
        foreach (Type contract in typeof(IWorldPersistenceRepository).GetInterfaces().Append(typeof(IWorldPersistenceRepository)))
        {
            InterfaceMapping map = typeof(SqliteWorldPersistenceRepository).GetInterfaceMap(contract);
            foreach (MethodInfo target in map.TargetMethods)
            {
                Assert.True(
                    target.DeclaringType == typeof(SqliteWorldPersistenceRepository),
                    $"{contract.Name}.{target.Name} falls through to {target.DeclaringType?.Name}");
            }
        }
    }

    [Fact]
    public void MovingAnItem_ChangesItsAnchor_AndTheRowsSurviveARestart()
    {
        using MigrationTestDatabase db = Migrated();
        long bladeId, hammerId;
        using (Run run = new(db))
        {
            Assert.True(run.Inventory.Ensure(Player));
            bladeId = run.Inventory.BuildSnapshot(Player).IdAt(2);
            hammerId = MainHandId(run.Inventory);

            // The blade is a 2x3: rows 3-5 of columns 0-1 are free.
            Assert.Equal(InventoryEquipOutcome.Equipped, run.Inventory.TryMove(Player, 2, 24));

            Dictionary<int, long> rows = run.Rows();
            Assert.False(rows.ContainsKey(2));
            Assert.Equal(bladeId, rows[24]);
            Assert.Equal(hammerId, rows[100]);
        }

        using (Run restarted = new(db))
        {
            Assert.True(restarted.Inventory.Ensure(Player));
            Assert.Equal(hammerId, MainHandId(restarted.Inventory));
            Assert.Equal(bladeId, restarted.Inventory.BuildSnapshot(Player).IdAt(24));
            Assert.Null(restarted.Inventory.BuildSnapshot(Player).ItemAt(2));
        }
    }

    [Fact]
    public void SwappingTwoBagItems_SurvivesARestart()
    {
        using MigrationTestDatabase db = Migrated();
        long blade, maul, hammer;
        using (Run run = new(db))
        {
            Assert.True(run.Inventory.Ensure(Player));
            PlayerInventorySnapshot before = run.Inventory.BuildSnapshot(Player);
            blade = before.IdAt(2);
            maul = before.IdAt(4);
            hammer = MainHandId(run.Inventory);

            Assert.Equal(InventoryEquipOutcome.Equipped, run.Inventory.TryMove(Player, 2, 4));
        }

        using Run restarted = new(db);
        Dictionary<int, long> rows = restarted.Rows();
        Assert.Equal(maul, rows[2]);
        Assert.Equal(blade, rows[4]);
        Assert.Equal(hammer, rows[100]);
    }

    [Fact]
    public void AnItemMovedBetweenEquipmentSlots_LeavesOneRow_AfterARestart()
    {
        using MigrationTestDatabase db = Migrated();
        long torch;
        using (Run run = new(db))
        {
            Assert.True(run.Inventory.Ensure(Player));
            Assert.Equal(InventoryEquipOutcome.Equipped, run.Inventory.TryEquip(Player, 7));
            torch = MainHandId(run.Inventory);

            // The worn torch moves to the off hand by naming its worn slot as the source.
            Assert.Equal(InventoryEquipOutcome.Equipped, run.Inventory.TryEquip(Player, 100, 101));
        }

        using Run restarted = new(db);
        Dictionary<int, long> rows = restarted.Rows();
        Assert.False(rows.ContainsKey(100));
        Assert.Equal(torch, rows[101]);
    }

    [Fact]
    public void Unequip_PutsTheItemInTheBag_AndTheEmptyHandSurvivesARestart()
    {
        using MigrationTestDatabase db = Migrated();
        long hammer;
        using (Run run = new(db))
        {
            Assert.True(run.Inventory.Ensure(Player));
            hammer = MainHandId(run.Inventory);

            Assert.Equal(InventoryEquipOutcome.Equipped, run.Inventory.TryUnequip(Player, 100));

            Dictionary<int, long> rows = run.Rows();
            Assert.False(rows.ContainsKey(100));
            Assert.Equal(hammer, rows[0]);
        }

        using Run restarted = new(db);
        Assert.True(restarted.Inventory.Ensure(Player));
        Assert.Equal(0, MainHandId(restarted.Inventory));
        Assert.Equal(hammer, restarted.Inventory.BuildSnapshot(Player).Slots[0].Item!.Value.ItemInstanceId);
        Assert.False(restarted.Rows().ContainsKey(100));
    }

    [Fact]
    public void Unequip_TakesTheFirstFitAroundTheItemsThere_AndARestartSeesIt()
    {
        using MigrationTestDatabase db = Migrated();
        using (Run setup = new(db))
        {
            Give(setup.World, Player, 100, 90, "tool.torch");
            Give(setup.World, Player, 0, 91, "tool.builder-hammer");
            setup.World.SaveStarterKitGrant(Player, ToolCatalog.CurrentStarterKitVersion);
        }

        using (Run run = new(db))
        {
            Assert.True(run.Inventory.Ensure(Player));
            Assert.Equal(InventoryEquipOutcome.Equipped, run.Inventory.TryUnequip(Player, 100));
        }

        using Run restarted = new(db);
        // The hammer is a 2x3 at anchor 0; the torch (1x2) goes to the first free cell, 2.
        Dictionary<int, long> rows = restarted.Rows();
        Assert.Equal(90L, rows[2]);
        Assert.Equal(91L, rows[0]);
        Assert.False(rows.ContainsKey(100));
        Assert.True(restarted.HasItem(90));
    }

    [Fact]
    public void Unequip_WithAFullBag_IsRejected_AndTheRowsDoNotChange()
    {
        using MigrationTestDatabase db = Migrated();
        using (Run setup = new(db))
        {
            Give(setup.World, Player, 100, 90, "tool.torch");
            // 64 rings fill every cell of the board.
            for (int slot = 0; slot < InventoryConstants.BackpackSlotCount; slot++)
            {
                Give(setup.World, Player, slot, 200 + slot, "gear.iron-band");
            }

            setup.World.SaveStarterKitGrant(Player, ToolCatalog.CurrentStarterKitVersion);
        }

        using (Run run = new(db))
        {
            Assert.True(run.Inventory.Ensure(Player));
            Dictionary<int, long> before = run.Rows();

            Assert.Equal(InventoryEquipOutcome.BackpackFull, run.Inventory.TryUnequip(Player, 100));

            Assert.Equal(before, run.Rows());
        }

        using Run restarted = new(db);
        Assert.Equal(90L, restarted.Rows()[100]);
    }

    [Fact]
    public void EquipOverAWornItem_SendsTheDisplacedItemToTheBlades_Anchor_AcrossARestart()
    {
        using MigrationTestDatabase db = Migrated();
        using (Run setup = new(db))
        {
            Give(setup.World, Player, 100, 90, "tool.torch");
            Give(setup.World, Player, 0, 91, "tool.field-blade");
            setup.World.SaveStarterKitGrant(Player, ToolCatalog.CurrentStarterKitVersion);
        }

        using (Run run = new(db))
        {
            Assert.Equal(InventoryEquipOutcome.Equipped, run.Inventory.TryEquip(Player, 0));
        }

        using Run restarted = new(db);
        Dictionary<int, long> rows = restarted.Rows();
        Assert.Equal(91L, rows[100]);
        // The torch it displaced took the blade's anchor, where its 1x2 fits.
        Assert.Equal(90L, rows[0]);
    }

    [Fact]
    public void Destroy_RemovesTheRowAndTheItem_AndNeitherARestartNorAKitTopUpReturnsIt()
    {
        using MigrationTestDatabase db = Migrated();
        long lantern;
        using (Run run = new(db))
        {
            Assert.True(run.Inventory.Ensure(Player));
            lantern = run.Inventory.BuildSnapshot(Player).IdAt(6);

            Assert.Equal(InventoryEquipOutcome.Equipped, run.Inventory.TryDestroy(Player, 6, lantern));

            Assert.False(run.Rows().ContainsKey(6));
            Assert.False(run.HasItem(lantern));
        }

        using Run restarted = new(db);
        Assert.True(restarted.Inventory.Ensure(Player));
        Assert.False(restarted.HasItem(lantern));
        Assert.DoesNotContain(
            restarted.Inventory.BuildSnapshot(Player).Slots,
            slot => slot.Item?.ItemKind == "tool.survey-lantern");
    }

    [Fact]
    public void Destroy_OfASharedLegacyItem_LeavesTheOtherPlayersRowAndTheItem()
    {
        using MigrationTestDatabase db = Migrated();
        using (Run setup = new(db))
        {
            Give(setup.World, Player, 0, 90, "tool.torch");
            setup.World.SaveInventorySlot(new PersistedInventorySlot("Bob", 0, 90, DateTime.UtcNow));
            setup.World.SaveStarterKitGrant(Player, ToolCatalog.CurrentStarterKitVersion);
            setup.World.SaveStarterKitGrant("Bob", ToolCatalog.CurrentStarterKitVersion);
        }

        using (Run run = new(db))
        {
            Assert.True(run.Inventory.Ensure(Player));
            Assert.Equal(InventoryEquipOutcome.Equipped, run.Inventory.TryDestroy(Player, 0, 90));
        }

        using Run restarted = new(db);
        Assert.Empty(restarted.Rows());
        Assert.Equal(90L, restarted.Rows("Bob")[0]);
        Assert.True(restarted.HasItem(90));
    }

    // H3 assumes the server sends an inert item with no modifiers and no light: a retired kind, rolled
    // or not, and an item in a slot it may not occupy. The client and server loadouts are both empty.
    [Fact]
    public void InertItems_ReachTheClient_WithNoModifiersAndNoLight()
    {
        using MigrationTestDatabase db = Migrated();
        ItemRoll roll = new(ItemRarity.Rare, [new StatModifier(StatId.Strength, StatOp.Flat, 9f)]);
        using (Run setup = new(db))
        {
            setup.World.SaveItemInstance(EquipmentTestWorld.MakeItem(90, Player, "tool.torch", roll) with { ItemKind = "tool.retired" });
            setup.World.SaveInventorySlot(new PersistedInventorySlot(Player, 100, 90, DateTime.UtcNow));
            // A torch worn on the head, which its definition does not allow.
            Give(setup.World, Player, 102, 91, "tool.torch");
            setup.World.SaveStarterKitGrant(Player, ToolCatalog.CurrentStarterKitVersion);
        }

        using Run run = new(db);
        Assert.True(run.Inventory.Ensure(Player));
        PlayerInventorySnapshot snapshot = run.Inventory.BuildSnapshot(Player);

        InventoryItemSnapshot retired = snapshot.EquippedSlot!.Value.Item!.Value;
        Assert.Equal("tool.retired", retired.ItemKind);
        Assert.Null(retired.Modifiers);
        Assert.Equal(HeldLightKind.None, retired.HeldLight);
        InventoryItemSnapshot wrongSlot = Assert.Single(snapshot.Equipment!).Item!.Value;
        Assert.Null(wrongSlot.Modifiers);
        Assert.Equal(HeldLightKind.None, wrongSlot.HeldLight);

        EquipmentLoadout client = new();
        Assert.Equal(HeldLightKind.None, PlayScene.ReadEquipment(snapshot, client).HeldLight);
        EquipmentLoadout server = new();
        run.Inventory.CopyEquipment(Player, server);
        Assert.Equal(HeldLightKind.None, run.Inventory.ResolveEquippedHeldLight(Player));
        foreach (EquipmentSlot slot in Enum.GetValues<EquipmentSlot>())
        {
            Assert.True(client.Get(slot).IsEmpty);
            Assert.True(server.Get(slot).IsEmpty);
        }
    }

    // What the HUD's equipment menu sends for an unequip is what TryUnequip takes, for each of the twelve
    // worn slots. (A worn item has no bag row, so the backpack menu has no Unequip entry any more.)
    [Fact]
    public void TheHudsUnequipIntent_FromTheEquipmentMenu_IsAcceptedForAllTwelveSlots()
    {
        using MigrationTestDatabase db = Migrated();
        using Run run = new(db);
        // One item per slot; the two bands fill the left ring slot and then the right one.
        string[] gear =
        [
            "gear.iron-blade", "gear.ward-lantern", "gear.leather-cap", "gear.leather-vest", "gear.iron-gauntlets",
            "gear.iron-boots", "gear.plated-leggings", "gear.ward-pendant", "gear.iron-band", "gear.iron-band",
            "gear.travel-cloak", "gear.ember-wisp",
        ];
        Assert.True(run.Inventory.Ensure(Player));
        // Room to pack twelve items back in whatever order the unequips come: the blade and the maul go.
        foreach (string spare in new[] { "tool.field-blade", "tool.stone-maul" })
        {
            PlayerInventorySnapshot kit = run.Inventory.BuildSnapshot(Player);
            Assert.Equal(InventoryEquipOutcome.Equipped, run.Inventory.TryDestroy(Player, kit.AnchorOf(spare), kit.ItemAt(kit.AnchorOf(spare))!.Value.ItemInstanceId));
        }

        for (int i = 0; i < gear.Length; i++)
        {
            ItemRollSeed seed = new(1, 2, 3, 4, i);
            Assert.Equal(InventoryEquipOutcome.Equipped, run.Inventory.TryGrantRolled(Player, gear[i], ItemRarity.Common, in seed));
        }

        void EquipAll()
        {
            // The first of each kind that is still in the bag, to its default slot (the second band
            // finds the right ring slot empty).
            foreach (string kind in gear)
            {
                int slot = run.Inventory.AnchorOf(Player, kind);
                Assert.Equal(InventoryEquipOutcome.Equipped, run.Inventory.TryEquip(Player, slot));
            }
        }

        InventoryEquipOutcome Send(InventoryHudAction action)
        {
            InventoryUnequipIntent intent = Assert.IsType<InventoryUnequipIntent>(PlayScene.ToInventoryIntent(in action));
            Assert.InRange(intent.SlotIndex, EquipmentSlots.FirstSlotIndex, EquipmentSlots.LastSlotIndex);
            return run.Inventory.TryUnequip(Player, intent.SlotIndex, intent.TargetCell);
        }

        EquipAll();
        Assert.Equal(gear.Length, run.Rows().Keys.Count(slot => slot >= EquipmentSlots.FirstSlotIndex));
        foreach (EquipmentSlot slot in Enum.GetValues<EquipmentSlot>())
        {
            Assert.Equal(InventoryEquipOutcome.Equipped, Send(CharacterPanelModel.EquipmentMenu(slot).Single().Action));
        }

        Assert.DoesNotContain(run.Rows().Keys, slot => slot >= EquipmentSlots.FirstSlotIndex);
        Assert.True(run.Inventory.BuildSnapshot(Player).BoardIsConsistent());
    }

    [Fact]
    public void AGrantedRolledItem_KeepsItsRoll_ThroughEquipUnequipAndRestart()
    {
        using MigrationTestDatabase db = Migrated();
        ItemRoll roll;
        long id;
        using (Run run = new(db))
        {
            Assert.True(run.Inventory.Ensure(Player));
            ItemRollSeed seed = new(1, 2, 3, 4, 5);
            Assert.Equal(InventoryEquipOutcome.Equipped, run.Inventory.TryGrantRolled(Player, "gear.iron-blade", ItemRarity.Rare, in seed));
            PersistedItemInstance item = run.World.LoadPlayerInventory(Player).Items.Single(i => i.ItemKind == "gear.iron-blade");
            id = item.ItemInstanceId;
            roll = item.Roll!;
            int slot = run.Rows().Single(row => row.Value == id).Key;
            Assert.Equal(InventoryEquipOutcome.Equipped, run.Inventory.TryEquip(Player, slot));
            Assert.Equal(InventoryEquipOutcome.Equipped, run.Inventory.TryUnequip(Player, 100));
        }

        using Run restarted = new(db);
        Assert.True(restarted.Inventory.Ensure(Player));
        PersistedItemInstance loaded = restarted.World.LoadPlayerInventory(Player).Items.Single(i => i.ItemInstanceId == id);
        Assert.Equal(ItemRollCodec.Serialize(roll), ItemRollCodec.Serialize(loaded.Roll!));
        Assert.NotEqual(id, MainHandId(restarted.Inventory));
    }
}
