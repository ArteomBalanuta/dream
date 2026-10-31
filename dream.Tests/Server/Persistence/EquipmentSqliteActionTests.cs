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

// Wave H glue: H1's move, unequip, destroy and legacy-rescue cases on the production repository
// (ServerPersistenceBundle.World, the facade the runtime writes through), which InventoryActionTests
// runs on the EquipmentTestWorld stand-in. Every case reads the rows back from a reopened bundle with a
// new service, as a restart does, and no queued write may have failed (the queue only counts those).
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
        // The wave H repository methods have do-nothing default bodies, for the test fakes. On the facade
        // one of those would silently drop the write.
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
    public void MovingAnEquippedItem_KeepsItEquipped_AndTheRowsSurviveARestart()
    {
        using MigrationTestDatabase db = Migrated();
        long bladeId;
        using (Run run = new(db))
        {
            Assert.Equal(InventoryEquipOutcome.Equipped, run.Inventory.TryEquip(Player, 1));
            bladeId = MainHandId(run.Inventory);

            Assert.Equal(InventoryEquipOutcome.Equipped, run.Inventory.TryMove(Player, 1, 9));

            Dictionary<int, long> rows = run.Rows();
            Assert.False(rows.ContainsKey(1));
            Assert.Equal(bladeId, rows[9]);
            Assert.Equal(bladeId, rows[100]);
        }

        using (Run restarted = new(db))
        {
            Assert.True(restarted.Inventory.Ensure(Player));
            Assert.Equal(bladeId, MainHandId(restarted.Inventory));
            Assert.Equal(bladeId, restarted.Inventory.BuildSnapshot(Player).Slots[9].Item!.Value.ItemInstanceId);
            Assert.Null(restarted.Inventory.BuildSnapshot(Player).Slots[1].Item);
        }
    }

    [Fact]
    public void SwappingTwoEquippedItems_SurvivesARestart()
    {
        using MigrationTestDatabase db = Migrated();
        long torch, lantern;
        using (Run run = new(db))
        {
            Assert.Equal(InventoryEquipOutcome.Equipped, run.Inventory.TryEquip(Player, 4));
            Assert.Equal(InventoryEquipOutcome.Equipped, run.Inventory.TryEquip(Player, 3, 101));
            PlayerInventorySnapshot before = run.Inventory.BuildSnapshot(Player);
            torch = before.Slots[4].Item!.Value.ItemInstanceId;
            lantern = before.Slots[3].Item!.Value.ItemInstanceId;

            Assert.Equal(InventoryEquipOutcome.Equipped, run.Inventory.TryMove(Player, 3, 4));
        }

        using Run restarted = new(db);
        Dictionary<int, long> rows = restarted.Rows();
        Assert.Equal(torch, rows[3]);
        Assert.Equal(lantern, rows[4]);
        Assert.Equal(torch, rows[100]);
        Assert.Equal(lantern, rows[101]);
    }

    [Fact]
    public void AnItemMovedBetweenEquipmentSlots_LeavesOneRow_AfterARestart()
    {
        using MigrationTestDatabase db = Migrated();
        long torch;
        using (Run run = new(db))
        {
            Assert.Equal(InventoryEquipOutcome.Equipped, run.Inventory.TryEquip(Player, 4));
            torch = MainHandId(run.Inventory);

            Assert.Equal(InventoryEquipOutcome.Equipped, run.Inventory.TryEquip(Player, 4, 101));
        }

        using Run restarted = new(db);
        Dictionary<int, long> rows = restarted.Rows();
        Assert.False(rows.ContainsKey(100));
        Assert.Equal(torch, rows[101]);
    }

    [Fact]
    public void Unequip_KeepsTheBackpackRow_AndTheEmptyHandSurvivesARestart()
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
    public void UnequipOfALegacyItemWithNoBackpackRow_RescuesIt_AndARestartSeesTheRescue()
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
        Dictionary<int, long> rows = restarted.Rows();
        Assert.Equal(90L, rows[1]);
        Assert.False(rows.ContainsKey(100));
        Assert.True(restarted.HasItem(90));
    }

    [Fact]
    public void UnequipOfALegacyItem_WithAFullBackpack_IsRejected_AndTheRowsDoNotChange()
    {
        using MigrationTestDatabase db = Migrated();
        using (Run setup = new(db))
        {
            Give(setup.World, Player, 100, 90, "tool.torch");
            for (int slot = 0; slot < InventoryConstants.BackpackSlotCount; slot++)
            {
                Give(setup.World, Player, slot, 200 + slot, "tool.builder-hammer");
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
    public void EquipOverALegacyItemWithNoBackpackRow_RescuesTheDisplacedItem_AcrossARestart()
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
        Assert.Equal(90L, rows[1]);
    }

    [Fact]
    public void Destroy_RemovesBothRows_AndNeitherARestartNorAKitTopUpReturnsTheItem()
    {
        using MigrationTestDatabase db = Migrated();
        long lantern;
        using (Run run = new(db))
        {
            Assert.True(run.Inventory.Ensure(Player));
            lantern = run.Inventory.BuildSnapshot(Player).Slots[3].Item!.Value.ItemInstanceId;

            Assert.Equal(InventoryEquipOutcome.Equipped, run.Inventory.TryDestroy(Player, 3, lantern));

            Assert.False(run.Rows().ContainsKey(3));
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
            setup.World.SaveInventorySlot(new PersistedInventorySlot(Player, 0, 90, DateTime.UtcNow));
            Give(setup.World, Player, 1, 91, "tool.torch");
            setup.World.SaveInventorySlot(new PersistedInventorySlot(Player, 102, 91, DateTime.UtcNow));
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

    // What H3's panel sends for an unequip, from each of its two menus, is what TryUnequip takes.
    [Fact]
    public void TheHudsUnequipIntents_FromEitherMenu_AreAcceptedForAllFourSlots()
    {
        using MigrationTestDatabase db = Migrated();
        using Run run = new(db);
        string[] gear = ["gear.iron-blade", "gear.ward-lantern", "gear.leather-cap", "gear.leather-vest"];
        Assert.True(run.Inventory.Ensure(Player));
        for (int i = 0; i < gear.Length; i++)
        {
            ItemRollSeed seed = new(1, 2, 3, 4, i);
            Assert.Equal(InventoryEquipOutcome.Equipped, run.Inventory.TryGrantRolled(Player, gear[i], ItemRarity.Common, in seed));
        }

        void EquipAll()
        {
            foreach (string kind in gear)
            {
                int slot = run.Inventory.BuildSnapshot(Player).Slots.First(row => row.Item?.ItemKind == kind).SlotIndex;
                Assert.Equal(InventoryEquipOutcome.Equipped, run.Inventory.TryEquip(Player, slot));
            }
        }

        InventoryEquipOutcome Send(InventoryHudAction action)
        {
            InventoryUnequipIntent intent = Assert.IsType<InventoryUnequipIntent>(PlayScene.ToInventoryIntent(in action));
            Assert.InRange(intent.SlotIndex, EquipmentSlots.FirstSlotIndex, EquipmentSlots.LastSlotIndex);
            return run.Inventory.TryUnequip(Player, intent.SlotIndex);
        }

        EquipAll();
        foreach (EquipmentSlot slot in Enum.GetValues<EquipmentSlot>())
        {
            Assert.Equal(InventoryEquipOutcome.Equipped, Send(CharacterPanelModel.EquipmentMenu(slot).Single().Action));
        }

        EquipAll();
        foreach (string kind in gear)
        {
            PlayerInventorySnapshot snapshot = run.Inventory.BuildSnapshot(Player);
            InventorySlotSnapshot row = snapshot.Slots.First(s => s.Item?.ItemKind == kind);
            InventoryItemSnapshot item = row.Item!.Value;
            CharacterMenuEntry unequip = CharacterPanelModel.BackpackMenu(in snapshot, row.SlotIndex, in item).Single(entry => entry.Label == "Unequip");
            Assert.Equal(InventoryEquipOutcome.Equipped, Send(unequip.Action));
        }

        Assert.DoesNotContain(run.Rows().Keys, slot => slot >= EquipmentSlots.FirstSlotIndex);
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
