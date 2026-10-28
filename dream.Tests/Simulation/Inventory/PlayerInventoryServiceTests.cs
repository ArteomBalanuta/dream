using Xunit;
using Dream.Server.Persistence;
using Dream.Simulation.Inventory;
using Dream.Simulation.Persistence;
using Dream.Tests.Server.Persistence;

namespace Dream.Tests.Simulation.Inventory;

// ARCH-3 step B: PlayerInventoryService on its own, without a WorldSessionRuntime: starter
// seeding, loading, equip, item-id allocation and eviction (including the failed-write retention).
// Wave T2b: the torch, held lights and the starter top-up.
public sealed class PlayerInventoryServiceTests
{
    private static int StarterCount => ToolCatalog.StarterItemKinds.Count;

    [Fact]
    public void Ensure_SeedsTheStarterInventory_AndEquipsTheFirstItem()
    {
        FakeWorld world = new();
        using PersistenceWriteQueue writes = PersistenceWriteQueue.Inline();
        PlayerInventoryService inventory = new(world, writes);

        Assert.True(inventory.Ensure("Alice"));

        PlayerInventorySnapshot snapshot = inventory.BuildSnapshot("Alice");
        Assert.Equal("Alice", snapshot.PlayerName);
        for (int i = 0; i < StarterCount; i++)
        {
            InventoryItemSnapshot item = snapshot.Slots[i].Item!.Value;
            Assert.Equal(ToolCatalog.StarterItemKinds[i], item.ItemKind);
            Assert.Equal(i + 1, item.ItemInstanceId);
        }

        Assert.Equal(1, snapshot.EquippedSlot!.Value.Item!.Value.ItemInstanceId);
        Assert.True(snapshot.Slots[0].IsEquipped);
        Assert.Equal(StarterCount, world.ItemSaves);
        // A slot row per starter item plus the equipped slot.
        Assert.Equal(StarterCount + 1, world.SlotSaves);
        Assert.Equal(1, inventory.LoadedInventoryCount);
        Assert.Equal(StarterCount, inventory.LoadedItemInstanceCount);
        Assert.Equal(ToolCatalog.Resolve(ToolCatalog.StarterItemKinds[0]).StatModifier, inventory.ResolveEquippedToolModifier("Alice"));

        // A second Ensure neither re-seeds nor writes.
        Assert.True(inventory.Ensure("alice"));
        Assert.Equal(StarterCount, world.ItemSaves);
        Assert.Equal(StarterCount + 1, world.SlotSaves);
    }

    [Fact]
    public void Ensure_WithoutPersistence_SeedsInMemory()
    {
        PlayerInventoryService inventory = new(worldPersistence: null, writes: null);

        Assert.True(inventory.Ensure("Solo"));

        Assert.Equal(StarterCount, inventory.LoadedItemInstanceCount);
        Assert.Equal(StarterCount + 1, inventory.NextItemInstanceId);
        Assert.Equal(0, inventory.UnconfirmedEvictionCount);
    }

    [Fact]
    public void ItemIds_StartAboveThePersistedMax_AndAboveEveryLoadedId()
    {
        FakeWorld world = new();
        world.Seed("Stored", firstItemId: 40);
        using PersistenceWriteQueue writes = PersistenceWriteQueue.Inline();
        PlayerInventoryService inventory = new(world, writes);
        Assert.Equal(1, inventory.NextItemInstanceId);

        inventory.RestoreItemIdCounter();
        Assert.Equal(40 + StarterCount, inventory.NextItemInstanceId);
        Assert.Equal(0, inventory.LoadedInventoryCount);

        // A loaded row above the counter (written by another run after the max was read) bumps it.
        inventory.ApplyLoaded("Late", new PersistedPlayerInventory(
            [new PersistedInventorySlot("Late", 0, 70, DateTime.UtcNow)],
            [Item(70, "Late")]));
        Assert.Equal(71, inventory.NextItemInstanceId);

        Assert.True(inventory.Ensure("Fresh"));
        Assert.Equal(
            Enumerable.Range(71, StarterCount).Select(id => (long)id),
            inventory.BuildSnapshot("Fresh").Slots.Take(StarterCount).Select(slot => slot.Item!.Value.ItemInstanceId));
    }

    [Fact]
    public void ApplyLoaded_InstallsTheRows_AndNeverReplacesALoadedInventory()
    {
        FakeWorld world = new();
        using PersistenceWriteQueue writes = PersistenceWriteQueue.Inline();
        PlayerInventoryService inventory = new(world, writes);

        // Backpack items but no equipped row: Ensure equips the first occupied slot.
        inventory.ApplyLoaded("Bob", new PersistedPlayerInventory(
            [
                new PersistedInventorySlot("Bob", 3, 11, DateTime.UtcNow),
                new PersistedInventorySlot("Bob", 5, 12, DateTime.UtcNow)
            ],
            [Item(11, "Bob"), Item(12, "Bob")]));
        Assert.True(inventory.IsLoaded("bob"));
        Assert.True(inventory.LoadedSlotsByPlayer.ContainsKey("BOB"));

        inventory.ApplyLoaded("Bob", new PersistedPlayerInventory(
            [new PersistedInventorySlot("Bob", 0, 99, DateTime.UtcNow)],
            [Item(99, "Bob")]));
        Assert.Equal(2, inventory.LoadedItemInstanceCount);

        Assert.True(inventory.Ensure("Bob"));
        // The equipped slot first, then the starter top-up: Bob holds only hammers, so every other
        // starter kind is granted into the lowest free slots (0, 1, 2, then 4).
        Assert.Equal(StarterCount - 1, world.ItemSaves);
        Assert.Equal(1 + (StarterCount - 1), world.SlotSaves);
        PlayerInventorySnapshot snapshot = inventory.BuildSnapshot("Bob");
        Assert.Equal(11, snapshot.EquippedSlot!.Value.Item!.Value.ItemInstanceId);
        Assert.True(snapshot.Slots[3].IsEquipped);
        // Slot 0 holds the topped-up blade, not the ignored second load's item 99.
        Assert.Equal("tool.field-blade", snapshot.Slots[0].Item!.Value.ItemKind);
        Assert.NotEqual(99, snapshot.Slots[0].Item!.Value.ItemInstanceId);
        Assert.Equal(0, world.Loads);
    }

    [Fact]
    public void Ensure_LoadsAnUnloadedInventoryFromTheRepository()
    {
        FakeWorld world = new();
        world.Seed("Stored", firstItemId: 20);
        using PersistenceWriteQueue writes = PersistenceWriteQueue.Inline();
        PlayerInventoryService inventory = new(world, writes);

        Assert.True(inventory.Ensure("Stored"));

        Assert.Equal(1, world.Loads);
        Assert.Equal(0, world.ItemSaves);
        Assert.Equal(20, inventory.BuildSnapshot("Stored").EquippedSlot!.Value.Item!.Value.ItemInstanceId);
    }

    [Fact]
    public void TryEquip_RejectsOutOfRangeAndEmptySlots_AndEquipsAnOccupiedOne()
    {
        FakeWorld world = new();
        using PersistenceWriteQueue writes = PersistenceWriteQueue.Inline();
        PlayerInventoryService inventory = new(world, writes);

        Assert.Equal(InventoryEquipOutcome.SlotOutOfRange, inventory.TryEquip("Carol", -1));
        Assert.Equal(InventoryEquipOutcome.SlotOutOfRange, inventory.TryEquip("Carol", InventoryConstants.BackpackSlotCount));
        // A range rejection does not load or seed anything.
        Assert.Equal(0, inventory.LoadedInventoryCount);

        Assert.Equal(InventoryEquipOutcome.SlotEmpty, inventory.TryEquip("Carol", InventoryConstants.BackpackSlotCount - 1));
        int slotSaves = world.SlotSaves;

        Assert.Equal(InventoryEquipOutcome.Equipped, inventory.TryEquip("Carol", 1));
        Assert.Equal(slotSaves + 1, world.SlotSaves);
        Assert.Equal(2, world.Rows[("Carol", InventoryConstants.EquippedSlotIndex)].ItemInstanceId);
        PlayerInventorySnapshot snapshot = inventory.BuildSnapshot("Carol");
        Assert.Equal(2, snapshot.EquippedSlot!.Value.Item!.Value.ItemInstanceId);
        Assert.True(snapshot.Slots[1].IsEquipped);
        Assert.False(snapshot.Slots[0].IsEquipped);
        Assert.Equal(ToolCatalog.Resolve(ToolCatalog.StarterItemKinds[1]).StatModifier, inventory.ResolveEquippedToolModifier("Carol"));
        Assert.Equal(default, inventory.ResolveEquippedToolModifier("Nobody"));
    }

    [Fact]
    public void TryEquip_ReportsStillLoading_WhenTheQueuedWritesDoNotDrain()
    {
        FakeWorld world = new();
        using PersistenceWriteQueue writes = PersistenceWriteQueue.Background();
        PlayerInventoryService inventory = new(world, writes);
        using ManualResetEventSlim gate = new(initialState: false);
        try
        {
            writes.Enqueue(() => gate.Wait(TimeSpan.FromSeconds(30)));
            Assert.Equal(InventoryEquipOutcome.StillLoading, inventory.TryEquip("Dave", 0));
            Assert.Equal(0, inventory.LoadedInventoryCount);
        }
        finally
        {
            gate.Set();
        }
    }

    [Fact]
    public void Evict_DropsAnInventoryWhoseWritesSucceeded()
    {
        FakeWorld world = new();
        using PersistenceWriteQueue writes = PersistenceWriteQueue.Inline();
        PlayerInventoryService inventory = new(world, writes);
        Assert.True(inventory.Ensure("Eve"));

        inventory.Evict("eve");

        Assert.Equal(0, inventory.LoadedInventoryCount);
        Assert.Equal(0, inventory.LoadedItemInstanceCount);
        Assert.Equal(0, inventory.UnconfirmedEvictionCount);

        // The next load reads the rows (the first Ensure read the empty ones).
        Assert.True(inventory.Ensure("Eve"));
        Assert.Equal(2, world.Loads);
        Assert.Equal(StarterCount, world.ItemSaves);
    }

    [Fact]
    public void Evict_RetainsAnInventoryAfterAFailedWrite()
    {
        FakeWorld world = new();
        // Background: the inline queue rethrows a failed write instead of dropping it.
        using PersistenceWriteQueue writes = PersistenceWriteQueue.Background();
        PlayerInventoryService inventory = new(world, writes);
        Assert.True(inventory.Ensure("Frank"));
        Assert.True(writes.Flush(TimeSpan.FromSeconds(30)));

        world.FailSlotWrites = true;
        Assert.Equal(InventoryEquipOutcome.Equipped, inventory.TryEquip("Frank", 2));
        Assert.True(writes.Flush(TimeSpan.FromSeconds(30)));
        world.FailSlotWrites = false;
        Assert.Equal(1, writes.FailedWrites);

        inventory.Evict("Frank");
        Assert.Equal(1, inventory.LoadedInventoryCount);
        // Retained for the rest of the run, through later evictions too.
        inventory.Evict("Frank");
        Assert.Equal(1, inventory.LoadedInventoryCount);
        Assert.Equal(3, inventory.BuildSnapshot("Frank").EquippedSlot!.Value.Item!.Value.ItemInstanceId);
        Assert.Equal(1, world.Rows[("Frank", InventoryConstants.EquippedSlotIndex)].ItemInstanceId);
    }

    [Fact]
    public void Evict_BeforeAQueuedWriteFails_RestoresTheInventoryOnTheNextLoad()
    {
        FakeWorld world = new();
        using PersistenceWriteQueue writes = PersistenceWriteQueue.Background();
        PlayerInventoryService inventory = new(world, writes);
        Assert.True(inventory.Ensure("Grace"));
        Assert.True(writes.Flush(TimeSpan.FromSeconds(30)));

        using ManualResetEventSlim gate = new(initialState: false);
        try
        {
            writes.Enqueue(() => gate.Wait(TimeSpan.FromSeconds(30)));
            world.FailSlotWrites = true;
            Assert.Equal(InventoryEquipOutcome.Equipped, inventory.TryEquip("Grace", 3));
            inventory.Evict("Grace");
            Assert.Equal(0, inventory.LoadedInventoryCount);
            Assert.Equal(1, inventory.UnconfirmedEvictionCount);
        }
        finally
        {
            gate.Set();
        }

        Assert.True(writes.Flush(TimeSpan.FromSeconds(30)));
        world.FailSlotWrites = false;
        Assert.Equal(1, world.Rows[("Grace", InventoryConstants.EquippedSlotIndex)].ItemInstanceId);

        // The rows are stale; the moved-out copy is put back instead.
        inventory.ApplyLoaded("Grace", world.LoadPlayerInventory("Grace"));
        Assert.Equal(1, inventory.LoadedInventoryCount);
        Assert.Equal(0, inventory.UnconfirmedEvictionCount);
        Assert.Equal(4, inventory.BuildSnapshot("Grace").EquippedSlot!.Value.Item!.Value.ItemInstanceId);
    }

    [Fact]
    public void Evict_BeforeQueuedWritesSucceed_IsForgotten_AndTheNextLoadReadsTheRows()
    {
        FakeWorld world = new();
        using PersistenceWriteQueue writes = PersistenceWriteQueue.Background();
        PlayerInventoryService inventory = new(world, writes);
        Assert.True(inventory.Ensure("Heidi"));

        using ManualResetEventSlim gate = new(initialState: false);
        try
        {
            writes.Enqueue(() => gate.Wait(TimeSpan.FromSeconds(30)));
            Assert.Equal(InventoryEquipOutcome.Equipped, inventory.TryEquip("Heidi", 1));
            inventory.Evict("Heidi");
            Assert.Equal(1, inventory.UnconfirmedEvictionCount);
        }
        finally
        {
            gate.Set();
        }

        Assert.True(writes.Flush(TimeSpan.FromSeconds(30)));
        // Another eviction forgets the confirmed one.
        inventory.Evict("nobody");
        Assert.Equal(0, inventory.UnconfirmedEvictionCount);

        int loads = world.Loads;
        Assert.True(inventory.Ensure("Heidi"));
        Assert.Equal(loads + 1, world.Loads);
        Assert.Equal(2, inventory.BuildSnapshot("Heidi").EquippedSlot!.Value.Item!.Value.ItemInstanceId);
    }

    [Fact]
    public void Catalog_HasTheTorch_AndTheLanternCastsLight()
    {
        ToolDefinition torch = ToolCatalog.Resolve("tool.torch");
        Assert.Equal("tool.torch", torch.ItemKind);
        Assert.Equal("Torch", torch.DisplayName);
        Assert.Equal("held.tool.torch", torch.VisualId);
        Assert.Equal(default, torch.StatModifier);
        Assert.Equal(HeldLightKind.Torch, torch.HeldLight);
        // Case-insensitive like every other kind (item_kind is stored as text).
        Assert.Equal(HeldLightKind.Torch, ToolCatalog.Resolve("TOOL.TORCH").HeldLight);

        Assert.Equal(HeldLightKind.Lantern, ToolCatalog.Resolve("tool.survey-lantern").HeldLight);
        Assert.Equal(HeldLightKind.None, ToolCatalog.Resolve("tool.builder-hammer").HeldLight);
        Assert.Equal(HeldLightKind.None, ToolCatalog.Resolve("tool.field-blade").HeldLight);
        Assert.Equal(HeldLightKind.None, ToolCatalog.Resolve("tool.stone-maul").HeldLight);
    }

    [Fact]
    public void StarterItems_EndWithTheTorch_AndKeepTheHammerEquipped()
    {
        Assert.Equal(
            ["tool.builder-hammer", "tool.field-blade", "tool.stone-maul", "tool.survey-lantern", "tool.torch"],
            ToolCatalog.StarterItemKinds);

        PlayerInventoryService inventory = new(worldPersistence: null, writes: null);
        Assert.True(inventory.Ensure("Ivan"));
        PlayerInventorySnapshot snapshot = inventory.BuildSnapshot("Ivan");
        Assert.Equal("tool.builder-hammer", snapshot.EquippedSlot!.Value.Item!.Value.ItemKind);
        Assert.Equal("tool.torch", snapshot.Slots[StarterCount - 1].Item!.Value.ItemKind);
        Assert.Equal(HeldLightKind.None, inventory.ResolveEquippedHeldLight("Ivan"));
    }

    [Fact]
    public void ResolveEquippedHeldLight_FollowsTheEquippedItem()
    {
        PlayerInventoryService inventory = new(worldPersistence: null, writes: null);
        Assert.Equal(HeldLightKind.None, inventory.ResolveEquippedHeldLight("Nobody"));
        Assert.True(inventory.Ensure("Judy"));
        int torchSlot = IndexOfStarter("tool.torch");
        int lanternSlot = IndexOfStarter("tool.survey-lantern");

        Assert.Equal(InventoryEquipOutcome.Equipped, inventory.TryEquip("Judy", torchSlot));
        Assert.Equal(HeldLightKind.Torch, inventory.ResolveEquippedHeldLight("judy"));
        Assert.Equal(InventoryEquipOutcome.Equipped, inventory.TryEquip("Judy", lanternSlot));
        Assert.Equal(HeldLightKind.Lantern, inventory.ResolveEquippedHeldLight("Judy"));
        Assert.Equal(InventoryEquipOutcome.Equipped, inventory.TryEquip("Judy", 0));
        Assert.Equal(HeldLightKind.None, inventory.ResolveEquippedHeldLight("Judy"));
    }

    [Fact]
    public void Ensure_TopsUpALegacyInventory_WithTheTorchInTheNextFreeSlot_Once()
    {
        FakeWorld world = new();
        using PersistenceWriteQueue writes = PersistenceWriteQueue.Inline();
        PlayerInventoryService inventory = new(world, writes);
        inventory.ApplyLoaded("Ken", LegacyInventory("Ken", firstItemId: 1, equippedSlot: 2));

        Assert.True(inventory.Ensure("Ken"));

        PlayerInventorySnapshot snapshot = inventory.BuildSnapshot("Ken");
        InventoryItemSnapshot torch = snapshot.Slots[4].Item!.Value;
        Assert.Equal("tool.torch", torch.ItemKind);
        Assert.Equal(5, torch.ItemInstanceId);
        Assert.Equal(1, world.ItemSaves);
        Assert.Equal(1, world.SlotSaves);
        Assert.Equal(5, world.Rows[("Ken", 4)].ItemInstanceId);
        // The equipped slot is untouched.
        Assert.Equal(3, snapshot.EquippedSlot!.Value.Item!.Value.ItemInstanceId);
        Assert.False(world.Rows.ContainsKey(("Ken", InventoryConstants.EquippedSlotIndex)));
        Assert.Equal(StarterCount, inventory.LoadedItemInstanceCount);

        // Idempotent: a second Ensure finds nothing missing and writes nothing.
        Assert.True(inventory.Ensure("Ken"));
        Assert.Equal(1, world.ItemSaves);
        Assert.Equal(1, world.SlotSaves);
        Assert.Equal(StarterCount, inventory.LoadedItemInstanceCount);
    }

    [Fact]
    public void Ensure_DoesNotTopUpAFullBackpack()
    {
        FakeWorld world = new();
        using PersistenceWriteQueue writes = PersistenceWriteQueue.Inline();
        PlayerInventoryService inventory = new(world, writes);
        List<PersistedInventorySlot> slots = [];
        List<PersistedItemInstance> items = [];
        for (int slotIndex = 0; slotIndex < InventoryConstants.BackpackSlotCount; slotIndex++)
        {
            long id = slotIndex + 1;
            slots.Add(new PersistedInventorySlot("Liz", slotIndex, id, DateTime.UtcNow));
            items.Add(Item(id, "Liz"));
        }

        slots.Add(new PersistedInventorySlot("Liz", InventoryConstants.EquippedSlotIndex, 1, DateTime.UtcNow));
        inventory.ApplyLoaded("Liz", new PersistedPlayerInventory(slots, items));

        Assert.True(inventory.Ensure("Liz"));

        Assert.Equal(0, world.ItemSaves);
        Assert.Equal(0, world.SlotSaves);
        Assert.Equal(InventoryConstants.BackpackSlotCount, inventory.LoadedItemInstanceCount);
        Assert.Equal(1, inventory.BuildSnapshot("Liz").EquippedSlot!.Value.Item!.Value.ItemInstanceId);
    }

    [Fact]
    public void Ensure_TopUpStopsWhenTheBackpackFills()
    {
        PlayerInventoryService inventory = new(worldPersistence: null, writes: null);
        // Hammers everywhere but one slot: only the first missing kind (the blade) fits.
        List<PersistedInventorySlot> slots = [];
        List<PersistedItemInstance> items = [];
        int freeSlot = 7;
        for (int slotIndex = 0; slotIndex < InventoryConstants.BackpackSlotCount; slotIndex++)
        {
            if (slotIndex == freeSlot)
            {
                continue;
            }

            long id = slotIndex + 1;
            slots.Add(new PersistedInventorySlot("Max", slotIndex, id, DateTime.UtcNow));
            items.Add(Item(id, "Max"));
        }

        inventory.ApplyLoaded("Max", new PersistedPlayerInventory(slots, items));

        Assert.True(inventory.Ensure("Max"));

        PlayerInventorySnapshot snapshot = inventory.BuildSnapshot("Max");
        Assert.Equal("tool.field-blade", snapshot.Slots[freeSlot].Item!.Value.ItemKind);
        Assert.Equal(InventoryConstants.BackpackSlotCount, inventory.LoadedItemInstanceCount);
        // Nothing was equipped, so Ensure equipped the first occupied slot before topping up.
        Assert.Equal(1, snapshot.EquippedSlot!.Value.Item!.Value.ItemInstanceId);
    }

    [Fact]
    public void TopUpTorch_SurvivesASqliteRestart()
    {
        using PersistenceTestDatabase database = new();
        using (SqlitePlayerProfileRepository profiles = new(database.Schema))
        {
            // Item and slot rows reference the owner's profile row.
            profiles.GetOrCreate("Nia");
        }

        using (SqliteWorldPersistenceRepository world = new(database.Schema))
        {
            // A pre-T2 player: the four old starter items, saved as the old seeding saved them.
            PersistedPlayerInventory legacy = LegacyInventory("Nia", firstItemId: 1, equippedSlot: 0);
            foreach (PersistedItemInstance item in legacy.Items)
            {
                world.SaveItemInstance(item);
            }

            foreach (PersistedInventorySlot slot in legacy.Slots)
            {
                world.SaveInventorySlot(slot);
            }

            using PersistenceWriteQueue writes = PersistenceWriteQueue.Inline();
            PlayerInventoryService inventory = new(world, writes);
            inventory.RestoreItemIdCounter();
            Assert.True(inventory.Ensure("Nia"));
            Assert.Equal("tool.torch", inventory.BuildSnapshot("Nia").Slots[4].Item!.Value.ItemKind);
            Assert.True(writes.Flush(TimeSpan.FromSeconds(30)));
        }

        using (SqliteWorldPersistenceRepository world = new(database.Schema))
        {
            using PersistenceWriteQueue writes = PersistenceWriteQueue.Inline();
            PlayerInventoryService inventory = new(world, writes);
            inventory.RestoreItemIdCounter();
            Assert.Equal(6, inventory.NextItemInstanceId);
            Assert.True(inventory.Ensure("Nia"));

            PlayerInventorySnapshot snapshot = inventory.BuildSnapshot("Nia");
            InventoryItemSnapshot torch = snapshot.Slots[4].Item!.Value;
            Assert.Equal("tool.torch", torch.ItemKind);
            Assert.Equal("Torch", torch.DisplayName);
            Assert.Equal("held.tool.torch", torch.VisualId);
            Assert.Equal(5, torch.ItemInstanceId);
            Assert.Equal(StarterCount, inventory.LoadedItemInstanceCount);
            // No second torch after the restart.
            Assert.Equal(6, inventory.NextItemInstanceId);
            Assert.Equal(HeldLightKind.None, inventory.ResolveEquippedHeldLight("Nia"));
            Assert.Equal(InventoryEquipOutcome.Equipped, inventory.TryEquip("Nia", 4));
            Assert.Equal(HeldLightKind.Torch, inventory.ResolveEquippedHeldLight("Nia"));
        }
    }

    private static int IndexOfStarter(string itemKind)
    {
        for (int i = 0; i < StarterCount; i++)
        {
            if (ToolCatalog.StarterItemKinds[i] == itemKind)
            {
                return i;
            }
        }

        throw new KeyNotFoundException(itemKind);
    }

    // The starter inventory as seeded before wave T2: the first four starter kinds in slots 0-3
    // and the item of equippedSlot equipped.
    private static PersistedPlayerInventory LegacyInventory(string owner, long firstItemId, int equippedSlot)
    {
        string[] legacyKinds = ["tool.builder-hammer", "tool.field-blade", "tool.stone-maul", "tool.survey-lantern"];
        List<PersistedInventorySlot> slots = [];
        List<PersistedItemInstance> items = [];
        for (int i = 0; i < legacyKinds.Length; i++)
        {
            items.Add(Item(firstItemId + i, owner, legacyKinds[i]));
            slots.Add(new PersistedInventorySlot(owner, i, firstItemId + i, DateTime.UtcNow));
        }

        slots.Add(new PersistedInventorySlot(owner, InventoryConstants.EquippedSlotIndex, firstItemId + equippedSlot, DateTime.UtcNow));
        return new PersistedPlayerInventory(slots, items);
    }

    private static PersistedItemInstance Item(long itemInstanceId, string owner, string? itemKind = null)
    {
        ToolDefinition definition = ToolCatalog.Resolve(itemKind ?? ToolCatalog.StarterItemKinds[0]);
        return new PersistedItemInstance(
            itemInstanceId,
            definition.ItemKind,
            definition.DisplayName,
            definition.VisualId,
            owner,
            WorldPosition: null,
            State: "Inventory",
            StackCount: 1,
            CreatedUtc: DateTime.UtcNow,
            UpdatedUtc: DateTime.UtcNow);
    }

    // Inventory rows only; the other members are unused by the service.
    private sealed class FakeWorld : IWorldPersistenceRepository
    {
        private readonly Dictionary<long, PersistedItemInstance> _items = [];
        private bool _failSlotWrites;
        private int _itemSaves;
        private int _slotSaves;
        private int _loads;

        public Dictionary<(string Player, int Slot), PersistedInventorySlot> Rows { get; } = [];

        public int ItemSaves => Volatile.Read(ref _itemSaves);

        public int SlotSaves => Volatile.Read(ref _slotSaves);

        public int Loads => Volatile.Read(ref _loads);

        // Makes SaveInventorySlot throw, like a write the queue drops.
        public bool FailSlotWrites
        {
            get => Volatile.Read(ref _failSlotWrites);
            set => Volatile.Write(ref _failSlotWrites, value);
        }

        public void Seed(string playerName, long firstItemId)
        {
            for (int i = 0; i < StarterCount; i++)
            {
                // One item of each starter kind, so the starter top-up finds nothing missing.
                _items[firstItemId + i] = Item(firstItemId + i, playerName, ToolCatalog.StarterItemKinds[i]);
                Rows[(playerName, i)] = new PersistedInventorySlot(playerName, i, firstItemId + i, DateTime.UtcNow);
            }

            Rows[(playerName, InventoryConstants.EquippedSlotIndex)] =
                new PersistedInventorySlot(playerName, InventoryConstants.EquippedSlotIndex, firstItemId, DateTime.UtcNow);
        }

        public PersistedPlayerInventory LoadPlayerInventory(string playerName)
        {
            Interlocked.Increment(ref _loads);
            PersistedInventorySlot[] slots = Rows.Values
                .Where(slot => slot.PlayerName == playerName)
                .OrderBy(slot => slot.SlotIndex)
                .ToArray();
            PersistedItemInstance[] items = slots
                .Select(slot => slot.ItemInstanceId)
                .Distinct()
                .Where(_items.ContainsKey)
                .Select(id => _items[id])
                .ToArray();
            return new PersistedPlayerInventory(slots, items);
        }

        public long GetMaxItemInstanceId()
        {
            long max = _items.Count == 0 ? 0 : _items.Keys.Max();
            return Rows.Count == 0 ? max : Math.Max(max, Rows.Values.Max(slot => slot.ItemInstanceId));
        }

        public void SaveItemInstance(PersistedItemInstance item)
        {
            Interlocked.Increment(ref _itemSaves);
            _items[item.ItemInstanceId] = item;
        }

        public void SaveInventorySlot(PersistedInventorySlot slot)
        {
            if (FailSlotWrites)
            {
                throw new InvalidOperationException("Simulated slot write failure.");
            }

            Interlocked.Increment(ref _slotSaves);
            Rows[(slot.PlayerName, slot.SlotIndex)] = slot;
        }

        public PersistedPlayerWorldState? TryGetPlayerWorldState(string playerName) => null;

        public void SavePlayerWorldState(PersistedPlayerWorldState state) { }

        public IReadOnlyList<PersistedPlacedCube> LoadPlacedCubes() => [];

        public void SavePlacedCube(PersistedPlacedCube cube) { }

        public void DeletePlacedCube(int cubeId) { }

        public IReadOnlyList<PersistedCubeFracturePiece> LoadCubeFracturePieces() => [];

        public void SaveCubeFracturePiece(PersistedCubeFracturePiece piece) { }

        public void DeleteCubeFracturePieces(int sourceCubeId) { }

        public IReadOnlyList<PersistedEnemyInstance> LoadActiveEnemies() => [];

        public void SaveEnemyInstance(PersistedEnemyInstance enemy) { }

        public void DeleteEnemyInstance(int enemyId) { }

        public void ReassignEnemyId(int oldEnemyId, int newEnemyId) { }

        public IReadOnlyList<PersistedItemInstance> LoadItemInstances() => _items.Values.ToArray();

        public void DeleteItemInstance(long itemInstanceId) => _items.Remove(itemInstanceId);

        public IReadOnlyList<PersistedInventorySlot> LoadInventorySlots() => Rows.Values.ToArray();

        public void DeleteInventorySlots(string playerName) { }
    }
}
