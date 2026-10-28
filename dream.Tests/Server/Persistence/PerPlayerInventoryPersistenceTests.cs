using OpenTK.Mathematics;
using Microsoft.Data.Sqlite;
using Xunit;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Server;
using Dream.Server.Persistence;
using Dream.Simulation.Inventory;
using Dream.Simulation.Persistence;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;

namespace Dream.Tests.Server.Persistence;

// SCALE-7 remainder: inventories are loaded per player (with the join prefetch) and evicted with
// the departed-profile LRU, instead of all being loaded at startup and kept forever. Item ids come
// from the database's highest id, so they never collide with an inventory that is not loaded.
public sealed class PerPlayerInventoryPersistenceTests
{
    [Fact]
    public void Inventory_SurvivesLeaveEvictionAndRejoin()
    {
        using PersistenceTestDatabase database = new();
        using WorldSessionRuntime runtime = CreateRuntime(database);
        runtime.MaxDepartedProfiles = 1;
        using RuntimeAuthoritativeSessionHost host = new(runtime);
        host.JoinSession("primary", "Primary");

        host.JoinSession("visitor", "Visitor");
        PlayerInventorySnapshot before = Capture(runtime, "visitor");
        Assert.True(runtime.ProcessAuthoritativeAction(Equip("visitor", sequence: 1, slotIndex: 2)).Accepted);
        long equipped = EquippedItemId(Capture(runtime, "visitor"));
        Assert.Equal(SlotItemId(before, 2), equipped);
        host.ReleaseSession("visitor");

        // Two later visitors push "Visitor" out of the one-name LRU.
        for (int i = 0; i < 2; i++)
        {
            host.JoinSession($"other-{i}", $"Other {i}");
            host.ReleaseSession($"other-{i}");
        }

        Assert.Equal(2, runtime.LoadedInventoryCount);
        Assert.Equal(2 * ToolCatalog.StarterItemKinds.Count, runtime.LoadedItemInstanceCount);

        host.JoinSession("visitor-again", "Visitor");
        PlayerInventorySnapshot after = Capture(runtime, "visitor-again");
        Assert.Equal(ItemIds(before), ItemIds(after));
        Assert.Equal(equipped, EquippedItemId(after));
        // The rejoin did not seed a second starter inventory.
        Assert.Equal(4 * ToolCatalog.StarterItemKinds.Count, database.Count("SELECT COUNT(*) FROM item_instances;"));
    }

    [Fact]
    public void TwoThousandDistinctPlayers_KeepInventoryMapsBounded()
    {
        const int Players = 2000;
        InMemoryProfiles profiles = new();
        CountingWorld world = new();
        using WorldSessionRuntime runtime = new(
            16,
            new Vector3(5f, 1.5f, 5f),
            new SceneEventHub(),
            playerProfiles: profiles,
            worldPersistence: world);
        using RuntimeAuthoritativeSessionHost host = new(runtime);
        host.JoinSession("primary", "Primary");
        PlayerInventorySnapshot firstVisitor = default;
        for (int i = 0; i < Players; i++)
        {
            host.JoinSession($"client-{i}", $"Visitor {i}");
            if (i == 0)
            {
                firstVisitor = Capture(runtime, "client-0");
            }

            host.ReleaseSession($"client-{i}");
        }

        int maxLoaded = 1 + runtime.MaxDepartedProfiles;
        Assert.True(Players > maxLoaded);
        Assert.Equal(maxLoaded, runtime.LoadedInventoryCount);
        Assert.Equal(maxLoaded * ToolCatalog.StarterItemKinds.Count, runtime.LoadedItemInstanceCount);
        Assert.True(runtime.KnownProfileNameCount <= maxLoaded, $"{runtime.KnownProfileNameCount} known profile names");
        Assert.Equal(maxLoaded, runtime.ProfileCacheCount);
        // Every player's inventory was written; none was lost by its eviction.
        Assert.Equal((Players + 1) * ToolCatalog.StarterItemKinds.Count, world.ItemCount);

        // The first visitor was evicted long ago; its inventory comes back from the repository.
        host.JoinSession("client-0-again", "Visitor 0");
        Assert.Equal(ItemIds(firstVisitor), ItemIds(Capture(runtime, "client-0-again")));
        Assert.Equal((Players + 1) * ToolCatalog.StarterItemKinds.Count, world.ItemCount);
    }

    [Fact]
    public void ItemIds_NeverCollideAfterARestart_WhenNoInventoryIsLoaded()
    {
        using PersistenceTestDatabase database = new();
        HashSet<long> firstRunIds;
        using (WorldSessionRuntime first = CreateRuntime(database))
        using (RuntimeAuthoritativeSessionHost host = new(first))
        {
            host.JoinSession("a", "Alpha");
            firstRunIds = ItemIds(Capture(first, "a"));
            host.ReleaseSession("a");
        }

        SqliteConnection.ClearAllPools();
        using WorldSessionRuntime restarted = CreateRuntime(database);
        // Nothing is loaded at startup, yet the counter starts above every persisted id.
        Assert.Equal(0, restarted.LoadedInventoryCount);
        Assert.Equal(0, restarted.LoadedItemInstanceCount);
        Assert.Equal(firstRunIds.Max() + 1, restarted.NextItemInstanceId);

        using RuntimeAuthoritativeSessionHost restartedHost = new(restarted);
        restartedHost.JoinSession("b", "Bravo");
        HashSet<long> bravoIds = ItemIds(Capture(restarted, "b"));
        Assert.Empty(bravoIds.Intersect(firstRunIds));
        Assert.All(bravoIds, id => Assert.True(id > firstRunIds.Max()));
        long items = database.Count("SELECT COUNT(*) FROM item_instances;");
        Assert.Equal(2 * ToolCatalog.StarterItemKinds.Count, items);
        Assert.Equal(items, database.Count("SELECT COUNT(DISTINCT item_instance_id) FROM item_instances;"));

        // Alpha's inventory, never loaded by this run, is intact.
        restartedHost.JoinSession("a-again", "Alpha");
        Assert.Equal(firstRunIds, ItemIds(Capture(restarted, "a-again")));
    }

    [Fact]
    public void Join_WhileAnInventoryWriteIsStillQueued_RestoresTheLatestState()
    {
        using PersistenceTestDatabase database = new();
        using DedicatedServerHost host = CreateDedicatedHost(database);
        WorldSessionRuntime runtime = host.Runtime!;
        runtime.MaxDepartedProfiles = 0;
        host.HandleClientMessage("connection-1", new ClientJoinRequestMessage("session-a", "Player A"));
        WaitForWrites(runtime);
        AdvanceOneTick(host);
        Assert.Equal("session-a", runtime.PrimaryRemoteSessionId?.Value);
        PlayerInventorySnapshot joined = Capture(runtime, "session-a");
        WaitForWrites(runtime);

        using ManualResetEventSlim gate = new(initialState: false);
        try
        {
            // The equip's slot write waits behind a slow write when the player leaves, is evicted
            // (no departed profiles are kept) and asks to join again.
            runtime.PersistenceWrites!.Enqueue(() => gate.Wait(TimeSpan.FromSeconds(30)));
            Assert.True(runtime.ProcessAuthoritativeAction(Equip("session-a", sequence: 1, slotIndex: 3)).Accepted);
            host.ReleaseConnection("connection-1");
            Assert.Equal(0, runtime.LoadedInventoryCount);
            host.HandleClientMessage("connection-2", new ClientJoinRequestMessage("session-a", "Player A"));
            AdvanceOneTick(host);
            Assert.Equal(PersistedPlayerLoadState.Pending, runtime.GetPersistedPlayerLoadState("Player A"));
        }
        finally
        {
            gate.Set();
        }

        WaitForWrites(runtime);
        AdvanceOneTick(host);
        Assert.Equal("session-a", runtime.PrimaryRemoteSessionId?.Value);
        PlayerInventorySnapshot rejoined = Capture(runtime, "session-a");
        Assert.Equal(ItemIds(joined), ItemIds(rejoined));
        Assert.Equal(SlotItemId(joined, 3), EquippedItemId(rejoined));
    }

    // Review S3: the queue drops a failed write, so an inventory whose write failed is the only
    // current copy. Leaving and being evicted must not swap it for the stale rows.
    [Fact]
    public void FailedInventoryWrite_KeepsTheInventoryThroughEviction_AndTheRejoinSeesTheInMemoryState()
    {
        CountingWorld world = new();
        using WorldSessionRuntime runtime = CreateBackgroundRuntime(world);
        runtime.MaxDepartedProfiles = 0;
        using RuntimeAuthoritativeSessionHost host = new(runtime);
        host.JoinSession("visitor", "Visitor");
        PlayerInventorySnapshot joined = Capture(runtime, "visitor");
        WaitForWrites(runtime);
        long failedBefore = runtime.PersistenceWrites!.FailedWrites;

        world.FailSlotWrites = true;
        Assert.True(runtime.ProcessAuthoritativeAction(Equip("visitor", sequence: 1, slotIndex: 2)).Accepted);
        WaitForWrites(runtime);
        world.FailSlotWrites = false;
        Assert.Equal(failedBefore + 1, runtime.PersistenceWrites.FailedWrites);
        // The row still names the first starter item.
        Assert.Equal(SlotItemId(joined, 0), EquippedRowItemId(world, "Visitor"));

        host.ReleaseSession("visitor");
        Assert.Equal(0, runtime.ProfileCacheCount);
        Assert.Equal(1, runtime.LoadedInventoryCount);

        host.JoinSession("visitor-again", "Visitor");
        PlayerInventorySnapshot rejoined = Capture(runtime, "visitor-again");
        Assert.Equal(ItemIds(joined), ItemIds(rejoined));
        Assert.Equal(SlotItemId(joined, 2), EquippedItemId(rejoined));

        // It stays retained through later evictions too.
        host.ReleaseSession("visitor-again");
        Assert.Equal(1, runtime.LoadedInventoryCount);
    }

    [Fact]
    public void InventoryWriteFailingAfterEviction_IsRestoredOnRejoin()
    {
        CountingWorld world = new();
        using WorldSessionRuntime runtime = CreateBackgroundRuntime(world);
        runtime.MaxDepartedProfiles = 0;
        using RuntimeAuthoritativeSessionHost host = new(runtime);
        host.JoinSession("visitor", "Visitor");
        PlayerInventorySnapshot joined = Capture(runtime, "visitor");
        WaitForWrites(runtime);

        using ManualResetEventSlim gate = new(initialState: false);
        try
        {
            // The equip's write is still queued (and will fail) when the player leaves and is evicted.
            runtime.PersistenceWrites!.Enqueue(() => gate.Wait(TimeSpan.FromSeconds(30)));
            world.FailSlotWrites = true;
            Assert.True(runtime.ProcessAuthoritativeAction(Equip("visitor", sequence: 1, slotIndex: 3)).Accepted);
            host.ReleaseSession("visitor");
            Assert.Equal(0, runtime.LoadedInventoryCount);
            Assert.Equal(1, runtime.UnconfirmedInventoryEvictionCount);
        }
        finally
        {
            gate.Set();
        }

        WaitForWrites(runtime);
        world.FailSlotWrites = false;
        Assert.Equal(SlotItemId(joined, 0), EquippedRowItemId(world, "Visitor"));

        host.JoinSession("visitor-again", "Visitor");
        PlayerInventorySnapshot rejoined = Capture(runtime, "visitor-again");
        Assert.Equal(ItemIds(joined), ItemIds(rejoined));
        Assert.Equal(SlotItemId(joined, 3), EquippedItemId(rejoined));
        Assert.Equal(0, runtime.UnconfirmedInventoryEvictionCount);
    }

    [Fact]
    public void ConfirmedInventoryEviction_IsForgotten_AndTheRejoinReadsTheRows()
    {
        CountingWorld world = new();
        using WorldSessionRuntime runtime = CreateBackgroundRuntime(world);
        runtime.MaxDepartedProfiles = 0;
        using RuntimeAuthoritativeSessionHost host = new(runtime);
        host.JoinSession("visitor", "Visitor");
        PlayerInventorySnapshot joined = Capture(runtime, "visitor");
        Assert.True(runtime.ProcessAuthoritativeAction(Equip("visitor", sequence: 1, slotIndex: 1)).Accepted);
        host.ReleaseSession("visitor");
        Assert.Equal(0, runtime.LoadedInventoryCount);
        WaitForWrites(runtime);
        Assert.Equal(SlotItemId(joined, 1), EquippedRowItemId(world, "Visitor"));

        // The next eviction forgets the confirmed one.
        host.JoinSession("other", "Other");
        host.ReleaseSession("other");
        WaitForWrites(runtime);
        host.JoinSession("third", "Third");
        host.ReleaseSession("third");
        Assert.True(runtime.UnconfirmedInventoryEvictionCount <= 1);

        int loadsBefore = world.Calls(nameof(IWorldPersistenceRepository.LoadPlayerInventory));
        host.JoinSession("visitor-again", "Visitor");
        Assert.Equal(loadsBefore + 1, world.Calls(nameof(IWorldPersistenceRepository.LoadPlayerInventory)));
        Assert.Equal(SlotItemId(joined, 1), EquippedItemId(Capture(runtime, "visitor-again")));
    }

    [Fact]
    public void Startup_LoadsNoInventory_AndAJoinLoadsOnlyItsOwn()
    {
        CountingWorld world = new();
        world.Seed("Stored", firstItemId: 40);
        world.Seed("Other", firstItemId: 90);
        using WorldSessionRuntime runtime = new(
            16,
            new Vector3(5f, 1.5f, 5f),
            new SceneEventHub(),
            playerProfiles: new InMemoryProfiles(),
            worldPersistence: world);

        Assert.Equal(0, world.Calls(nameof(IWorldPersistenceRepository.LoadItemInstances)));
        Assert.Equal(0, world.Calls(nameof(IWorldPersistenceRepository.LoadInventorySlots)));
        Assert.Equal(0, world.Calls(nameof(IWorldPersistenceRepository.LoadPlayerInventory)));
        Assert.Equal(1, world.Calls(nameof(IWorldPersistenceRepository.GetMaxItemInstanceId)));
        Assert.Equal(0, runtime.LoadedInventoryCount);
        Assert.Equal(94, runtime.NextItemInstanceId);

        using RuntimeAuthoritativeSessionHost host = new(runtime);
        host.JoinSession("stored", "Stored");
        Assert.Equal(1, world.Calls(nameof(IWorldPersistenceRepository.LoadPlayerInventory)));
        Assert.Equal(0, world.Calls(nameof(IWorldPersistenceRepository.LoadItemInstances)));
        Assert.Equal(0, world.Calls(nameof(IWorldPersistenceRepository.LoadInventorySlots)));
        Assert.Equal(1, runtime.LoadedInventoryCount);
        Assert.Equal(new HashSet<long> { 40, 41, 42, 43 }, ItemIds(Capture(runtime, "stored")));
        // The stored inventory was applied, not re-seeded.
        Assert.Equal(0, world.Calls(nameof(IWorldPersistenceRepository.SaveItemInstance)));
    }

    [Fact]
    public void JoinPrefetch_LoadsThePlayersInventory()
    {
        using PersistenceTestDatabase database = new();
        using DedicatedServerHost host = CreateDedicatedHost(database);
        WorldSessionRuntime runtime = host.Runtime!;
        PersistedPlayerPrefetch? prefetch = runtime.BeginPersistedPlayerPrefetch("Player A");
        WaitForWrites(runtime);
        Assert.NotNull(prefetch);
        Assert.True(prefetch.TryGetInventory(out PersistedPlayerInventory? inventory));
        Assert.Empty(inventory!.Slots);
    }

    [Fact]
    public void LoadPlayerInventory_ReadsOnePlayersRowsThroughIndexes()
    {
        using PersistenceTestDatabase database = new();
        using SqliteConnection connection = database.Schema.CreateOpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN " + SqliteWorldPersistenceRepository.LoadPlayerInventorySql;
        command.Parameters.AddWithValue("$playerName", "Player A");
        List<string> plan = [];
        using (SqliteDataReader reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                plan.Add(reader.GetString(3));
            }
        }

        Assert.NotEmpty(plan);
        Assert.DoesNotContain(plan, step => step.StartsWith("SCAN", StringComparison.Ordinal));
    }

    [Fact]
    public void SqliteRepository_LoadsOnePlayersInventory_AndTheMaxItemId()
    {
        using PersistenceTestDatabase database = new();
        SqlitePlayerProfileRepository profiles = new(database.Schema);
        SqliteWorldPersistenceRepository world = new(database.Schema);
        Assert.Equal(0, world.GetMaxItemInstanceId());
        profiles.GetOrCreate("player-one");
        profiles.GetOrCreate("player-two");
        world.SaveItemInstance(Item(101, "player-one"));
        world.SaveItemInstance(Item(102, "player-one"));
        world.SaveItemInstance(Item(205, "player-two"));
        world.SaveInventorySlot(new PersistedInventorySlot("player-one", 0, 101, DateTime.UtcNow));
        world.SaveInventorySlot(new PersistedInventorySlot("player-one", 1, 102, DateTime.UtcNow));
        world.SaveInventorySlot(new PersistedInventorySlot("player-one", InventoryConstants.EquippedSlotIndex, 101, DateTime.UtcNow));
        world.SaveInventorySlot(new PersistedInventorySlot("player-two", 0, 205, DateTime.UtcNow));

        PersistedPlayerInventory one = world.LoadPlayerInventory(" player-one ");
        Assert.Equal(new[] { 0, 1, InventoryConstants.EquippedSlotIndex }, one.Slots.Select(slot => slot.SlotIndex));
        Assert.Equal(new[] { 101L, 102L }, one.Items.Select(item => item.ItemInstanceId).Order());
        Assert.All(one.Items, item => Assert.Equal("player-one", item.OwnerPlayerName));
        Assert.Empty(world.LoadPlayerInventory("nobody").Slots);
        Assert.Equal(205, world.GetMaxItemInstanceId());
    }

    private static WorldSessionRuntime CreateRuntime(PersistenceTestDatabase database)
    {
        return new WorldSessionRuntime(
            16,
            new Vector3(5f, 1.5f, 5f),
            new SceneEventHub(),
            playerProfiles: new SqlitePlayerProfileRepository(database.Schema),
            worldPersistence: new SqliteWorldPersistenceRepository(database.Schema));
    }

    private static WorldSessionRuntime CreateBackgroundRuntime(CountingWorld world)
    {
        return new WorldSessionRuntime(
            16,
            new Vector3(5f, 1.5f, 5f),
            new SceneEventHub(),
            playerProfiles: new InMemoryProfiles(),
            worldPersistence: world,
            persistenceWrites: PersistenceWriteQueue.Background());
    }

    private static long EquippedRowItemId(CountingWorld world, string playerName)
    {
        return world.LoadPlayerInventory(playerName).Slots
            .Single(slot => slot.SlotIndex == InventoryConstants.EquippedSlotIndex)
            .ItemInstanceId;
    }

    private static DedicatedServerHost CreateDedicatedHost(PersistenceTestDatabase database)
    {
        WorldSessionRuntime runtime = new(
            12,
            new Vector3(5f, 1.5f, 5f),
            new SceneEventHub(),
            playerProfiles: new SqlitePlayerProfileRepository(database.Schema),
            worldPersistence: new SqliteWorldPersistenceRepository(database.Schema),
            persistenceWrites: PersistenceWriteQueue.Background());
        ProtocolRouter router = new(
            [
                new ClientHelloHandler(),
                new JoinRequestHandler(),
                new PlayerCommandHandler(),
                new PlayerActionHandler(),
                new WorldResyncRequestHandler()
            ],
            Array.Empty<IClientMessageHandler>());
        return new DedicatedServerHost(new RuntimeAuthoritativeSessionHost(runtime), router, deferJoinsToTick: true);
    }

    private static int AdvanceOneTick(DedicatedServerHost host)
    {
        int executed = host.AdvanceTime(SimulationClock.FixedDeltaSeconds);
        return executed != 0 ? executed : host.AdvanceTime(SimulationClock.FixedDeltaSeconds * 0.01);
    }

    private static void WaitForWrites(WorldSessionRuntime runtime)
    {
        Assert.True(runtime.PersistenceWrites!.Flush(TimeSpan.FromSeconds(30)), "The persistence worker did not drain.");
    }

    private static PlayerActionMessage Equip(string sessionId, ulong sequence, int slotIndex)
    {
        return new PlayerActionMessage(new PlayerSessionId(sessionId), sequence, new InventoryEquipIntent(slotIndex));
    }

    private static PlayerInventorySnapshot Capture(WorldSessionRuntime runtime, string sessionId)
    {
        PlayerInventorySnapshot? snapshot = runtime.TryCaptureInventorySnapshot(new PlayerSessionId(sessionId));
        Assert.NotNull(snapshot);
        return snapshot.Value;
    }

    private static HashSet<long> ItemIds(PlayerInventorySnapshot snapshot)
    {
        return snapshot.Slots
            .Where(slot => slot.Item.HasValue)
            .Select(slot => slot.Item!.Value.ItemInstanceId)
            .ToHashSet();
    }

    private static long SlotItemId(PlayerInventorySnapshot snapshot, int slotIndex)
    {
        return snapshot.Slots.Single(slot => slot.SlotIndex == slotIndex).Item!.Value.ItemInstanceId;
    }

    private static long EquippedItemId(PlayerInventorySnapshot snapshot)
    {
        return snapshot.EquippedSlot!.Value.Item!.Value.ItemInstanceId;
    }

    private static PersistedItemInstance Item(long id, string owner)
    {
        ToolDefinition definition = ToolCatalog.Resolve(ToolCatalog.StarterItemKinds[0]);
        return new PersistedItemInstance(
            id,
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

    private sealed class InMemoryProfiles : IPlayerProfileRepository
    {
        private readonly Dictionary<string, PersistedPlayerProfile> _profiles = new(StringComparer.Ordinal);

        public PersistedPlayerProfile GetOrCreate(string playerName)
        {
            if (!_profiles.TryGetValue(playerName.Trim(), out PersistedPlayerProfile profile))
            {
                profile = PersistedPlayerProfile.CreateDefault(playerName.Trim(), DateTime.UtcNow);
                _profiles[profile.PlayerName] = profile;
            }

            return profile;
        }

        public PersistedPlayerProfile? TryGet(string playerName)
        {
            return _profiles.TryGetValue(playerName.Trim(), out PersistedPlayerProfile profile) ? profile : null;
        }

        public void Save(PersistedPlayerProfile profile)
        {
            _profiles[profile.PlayerName] = profile;
        }
    }

    // An in-memory world repository that counts calls by method name. Only inventory rows are kept.
    private sealed class CountingWorld : IWorldPersistenceRepository
    {
        private readonly Dictionary<string, int> _calls = [];
        private readonly Dictionary<long, PersistedItemInstance> _items = [];
        private readonly Dictionary<(string Player, int Slot), PersistedInventorySlot> _slots = [];

        private bool _failSlotWrites;

        public int ItemCount => _items.Count;

        // Makes SaveInventorySlot throw (on the write worker), like a write the queue drops.
        public bool FailSlotWrites
        {
            get => Volatile.Read(ref _failSlotWrites);
            set => Volatile.Write(ref _failSlotWrites, value);
        }

        public int Calls(string method) => _calls.GetValueOrDefault(method);

        public void Seed(string playerName, long firstItemId)
        {
            for (int i = 0; i < ToolCatalog.StarterItemKinds.Count; i++)
            {
                _items[firstItemId + i] = Item(firstItemId + i, playerName);
                _slots[(playerName, i)] = new PersistedInventorySlot(playerName, i, firstItemId + i, DateTime.UtcNow);
            }

            _slots[(playerName, InventoryConstants.EquippedSlotIndex)] =
                new PersistedInventorySlot(playerName, InventoryConstants.EquippedSlotIndex, firstItemId, DateTime.UtcNow);
        }

        private void Count(string method) => _calls[method] = Calls(method) + 1;

        public PersistedPlayerWorldState? TryGetPlayerWorldState(string playerName)
        {
            Count(nameof(TryGetPlayerWorldState));
            return null;
        }

        public void SavePlayerWorldState(PersistedPlayerWorldState state) => Count(nameof(SavePlayerWorldState));

        public IReadOnlyList<PersistedPlacedCube> LoadPlacedCubes() => [];

        public void SavePlacedCube(PersistedPlacedCube cube) => Count(nameof(SavePlacedCube));

        public void DeletePlacedCube(int cubeId) => Count(nameof(DeletePlacedCube));

        public IReadOnlyList<PersistedCubeFracturePiece> LoadCubeFracturePieces() => [];

        public void SaveCubeFracturePiece(PersistedCubeFracturePiece piece) => Count(nameof(SaveCubeFracturePiece));

        public void DeleteCubeFracturePieces(int sourceCubeId) => Count(nameof(DeleteCubeFracturePieces));

        public IReadOnlyList<PersistedEnemyInstance> LoadActiveEnemies() => [];

        public void SaveEnemyInstance(PersistedEnemyInstance enemy) => Count(nameof(SaveEnemyInstance));

        public void DeleteEnemyInstance(int enemyId) => Count(nameof(DeleteEnemyInstance));

        public void ReassignEnemyId(int oldEnemyId, int newEnemyId) => Count(nameof(ReassignEnemyId));

        public IReadOnlyList<PersistedItemInstance> LoadItemInstances()
        {
            Count(nameof(LoadItemInstances));
            return _items.Values.ToArray();
        }

        public void SaveItemInstance(PersistedItemInstance item)
        {
            Count(nameof(SaveItemInstance));
            _items[item.ItemInstanceId] = item;
        }

        public void DeleteItemInstance(long itemInstanceId)
        {
            Count(nameof(DeleteItemInstance));
            _items.Remove(itemInstanceId);
        }

        public IReadOnlyList<PersistedInventorySlot> LoadInventorySlots()
        {
            Count(nameof(LoadInventorySlots));
            return _slots.Values.ToArray();
        }

        public PersistedPlayerInventory LoadPlayerInventory(string playerName)
        {
            Count(nameof(LoadPlayerInventory));
            string name = playerName.Trim();
            PersistedInventorySlot[] slots = _slots.Values
                .Where(slot => slot.PlayerName == name)
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
            Count(nameof(GetMaxItemInstanceId));
            long max = _items.Count == 0 ? 0 : _items.Keys.Max();
            return _slots.Count == 0 ? max : Math.Max(max, _slots.Values.Max(slot => slot.ItemInstanceId));
        }

        public void SaveInventorySlot(PersistedInventorySlot slot)
        {
            Count(nameof(SaveInventorySlot));
            if (Volatile.Read(ref _failSlotWrites))
            {
                throw new InvalidOperationException("Simulated inventory slot write failure.");
            }

            _slots[(slot.PlayerName.Trim(), slot.SlotIndex)] = slot with { PlayerName = slot.PlayerName.Trim() };
        }

        public void DeleteInventorySlots(string playerName)
        {
            Count(nameof(DeleteInventorySlots));
            foreach ((string Player, int Slot) key in _slots.Keys.Where(key => key.Player == playerName.Trim()).ToArray())
            {
                _slots.Remove(key);
            }
        }
    }
}
