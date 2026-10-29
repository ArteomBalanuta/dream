using Dream.Simulation.World;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Server.Persistence;
using Dream.Simulation.Combat;
using Dream.Simulation.Persistence;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Server.Persistence;

// Every cube hit and placement used to reload the whole placed_cube_instances table (plus a
// profile query) on the simulation tick, and every placement, fracture, enemy spawn/death and
// profile save committed synchronously there.
public sealed class PersistenceOffTickTests
{
    [Fact]
    public void CubeHitsAndPlacement_DoNotReadThePersistenceTables()
    {
        using TempDatabase database = new();
        CountingWorldRepository world = new(new SqliteWorldPersistenceRepository(database.Schema));
        CountingProfileRepository profiles = new(new SqlitePlayerProfileRepository(database.Schema));
        using WorldSessionRuntime runtime = CreateRuntime(world, profiles, PersistenceWriteQueue.Inline());
        using RuntimeAuthoritativeSessionHost host = new(runtime);
        PlayerSessionId session = new("session-a");
        host.JoinSession(session.Value, "Player A");
        runtime.Enemies.Clear();
        world.ResetCounts();
        profiles.ResetCounts();

        Assert.True(host.ProcessAction(new PlayerActionMessage(session, 1, new BuildUseIntent(new Vector3(7f, 1f, 7f)))).Accepted);
        runtime.SetAuthoritativeInteractionMode(PlayerInteractionMode.Combat);
        Vector3 origin = new(7.5f, 1.5f, 9.5f);
        runtime.Player.SetWorldPosition(origin);
        for (ulong sequence = 2; sequence <= 4; sequence++)
        {
            runtime.Player.Combatant.AdvanceCombat(5f);
            host.ProcessAction(new PlayerActionMessage(session, sequence, new CombatUseIntent(origin, -Vector3.UnitZ)));
        }

        Assert.Equal(0, world.Reads);
        Assert.Equal(0, profiles.Reads);
        Assert.True(world.Writes > 0);
    }

    [Fact]
    public void BackgroundQueue_PlacementReturnsWhileTheDatabaseWriteIsBlocked()
    {
        using TempDatabase database = new();
        CountingWorldRepository world = new(new SqliteWorldPersistenceRepository(database.Schema));
        using PersistenceWriteQueue writes = PersistenceWriteQueue.Background();
        using WorldSessionRuntime runtime = CreateRuntime(
            world,
            new SqlitePlayerProfileRepository(database.Schema),
            writes);
        using RuntimeAuthoritativeSessionHost host = new(runtime);
        PlayerSessionId session = new("session-a");
        host.JoinSession(session.Value, "Player A");
        world.BlockWrites();

        Task<AuthoritativeActionResult> place = Task.Run<AuthoritativeActionResult>(() => host.ProcessAction(new PlayerActionMessage(session, 1, new BuildUseIntent(new Vector3(7f, 1f, 7f)))));

        Assert.True(place.Wait(TimeSpan.FromSeconds(5)), "Placement waited for the database write.");
        Assert.True(place.Result.Accepted);
        world.ReleaseWrites();
        Assert.True(writes.Flush(TimeSpan.FromSeconds(5)));
        Assert.Single(world.Inner.LoadPlacedCubes());
    }

    [Fact]
    public void WriteQueue_RunsWritesInOrderAndCountsFailures()
    {
        using PersistenceWriteQueue writes = PersistenceWriteQueue.Background();
        List<int> order = [];

        writes.Enqueue(() => order.Add(1));
        writes.Enqueue(() => throw new InvalidOperationException("disk full"));
        writes.Enqueue(() => order.Add(3));

        Assert.True(writes.Flush(TimeSpan.FromSeconds(5)));
        Assert.Equal([1, 3], order);
        Assert.Equal(1, writes.FailedWrites);
        Assert.Equal(2, writes.CompletedWrites);
    }

    [Fact]
    public void InlineQueue_RunsImmediatelyAndPropagatesFailures()
    {
        using PersistenceWriteQueue writes = PersistenceWriteQueue.Inline();
        int value = 0;

        writes.Enqueue(() => value = 5);

        Assert.Equal(5, value);
        Assert.Throws<InvalidOperationException>(() => writes.Enqueue(() => throw new InvalidOperationException()));
    }

    private static WorldSessionRuntime CreateRuntime(
        IWorldPersistenceRepository world,
        IPlayerProfileRepository profiles,
        PersistenceWriteQueue writes)
    {
        return new WorldSessionRuntime(
            12,
            new Vector3(5f, 1.5f, 5f),
            new SceneEventHub(),
            playerProfiles: profiles,
            worldPersistence: world,
            persistenceWrites: writes);
    }

    private sealed class CountingProfileRepository : IPlayerProfileRepository
    {
        private readonly IPlayerProfileRepository _inner;

        public CountingProfileRepository(IPlayerProfileRepository inner)
        {
            _inner = inner;
        }

        public int Reads { get; private set; }

        public void ResetCounts()
        {
            Reads = 0;
        }

        public PersistedPlayerProfile GetOrCreate(string playerName)
        {
            Reads++;
            return _inner.GetOrCreate(playerName);
        }

        public PersistedPlayerProfile? TryGet(string playerName)
        {
            Reads++;
            return _inner.TryGet(playerName);
        }

        public void Save(PersistedPlayerProfile profile)
        {
            _inner.Save(profile);
        }
    }

    private sealed class CountingWorldRepository : IWorldPersistenceRepository
    {
        private readonly ManualResetEventSlim _writeGate = new(initialState: true);
        private int _reads;
        private int _writes;

        public CountingWorldRepository(IWorldPersistenceRepository inner)
        {
            Inner = inner;
        }

        public IWorldPersistenceRepository Inner { get; }

        public int Reads => _reads;

        public int Writes => _writes;

        public void ResetCounts()
        {
            _reads = 0;
            _writes = 0;
        }

        public void BlockWrites() => _writeGate.Reset();

        public void ReleaseWrites() => _writeGate.Set();

        public PersistedPlayerWorldState? TryGetPlayerWorldState(string playerName) => Read(() => Inner.TryGetPlayerWorldState(playerName));

        public void SavePlayerWorldState(PersistedPlayerWorldState state) => Write(() => Inner.SavePlayerWorldState(state));

        public IReadOnlyList<PersistedPlacedCube> LoadPlacedCubes() => Read(Inner.LoadPlacedCubes);

        public void SavePlacedCube(PersistedPlacedCube cube) => Write(() => Inner.SavePlacedCube(cube));

        public void DeletePlacedCube(ObjectId objectId) => Write(() => Inner.DeletePlacedCube(objectId));

        public IReadOnlyList<PersistedCubeFracturePiece> LoadCubeFracturePieces() => Read(Inner.LoadCubeFracturePieces);

        public void SaveCubeFracturePiece(PersistedCubeFracturePiece piece) => Write(() => Inner.SaveCubeFracturePiece(piece));

        public void DeleteCubeFracturePieces(ObjectId originObjectId) => Write(() => Inner.DeleteCubeFracturePieces(originObjectId));

        public IReadOnlyList<PersistedEnemyInstance> LoadActiveEnemies() => Read(Inner.LoadActiveEnemies);

        public void SaveEnemyInstance(PersistedEnemyInstance enemy) => Write(() => Inner.SaveEnemyInstance(enemy));

        public void DeleteEnemyInstance(ObjectId objectId) => Write(() => Inner.DeleteEnemyInstance(objectId));

        public long LoadNextObjectId() => 1;


        public void ReassignEnemyId(int oldEnemyId, int newEnemyId) => Write(() => Inner.ReassignEnemyId(oldEnemyId, newEnemyId));

        public IReadOnlyList<PersistedItemInstance> LoadItemInstances() => Read(Inner.LoadItemInstances);

        public void SaveItemInstance(PersistedItemInstance item) => Write(() => Inner.SaveItemInstance(item));

        public void DeleteItemInstance(long itemInstanceId) => Write(() => Inner.DeleteItemInstance(itemInstanceId));

        public IReadOnlyList<PersistedInventorySlot> LoadInventorySlots() => Read(Inner.LoadInventorySlots);

        public PersistedPlayerInventory LoadPlayerInventory(string playerName) => Read(() => Inner.LoadPlayerInventory(playerName));

        public long GetMaxItemInstanceId() => Read(Inner.GetMaxItemInstanceId);

        public void SaveInventorySlot(PersistedInventorySlot slot) => Write(() => Inner.SaveInventorySlot(slot));

        public void DeleteInventorySlots(string playerName) => Write(() => Inner.DeleteInventorySlots(playerName));

        public void SaveStarterKitGrant(string playerName, int kitVersion) => Write(() => Inner.SaveStarterKitGrant(playerName, kitVersion));

        private T Read<T>(Func<T> read)
        {
            Interlocked.Increment(ref _reads);
            return read();
        }

        private void Write(Action write)
        {
            _writeGate.Wait(TimeSpan.FromSeconds(30));
            Interlocked.Increment(ref _writes);
            write();
        }
    }

    private sealed class TempDatabase : IDisposable
    {
        private readonly string _rootDirectory;

        public TempDatabase()
        {
            _rootDirectory = Path.Combine(Path.GetTempPath(), $"dream-offtick-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_rootDirectory);
            string schemaPath = Path.Combine(FindRepoRoot(), "dream", "config", "player-profiles.init.sql");
            Schema = new SqliteSchemaBootstrapper(Path.Combine(_rootDirectory, "persistence.sqlite"), schemaPath);
            Schema.EnsureSchema();
        }

        public SqliteSchemaBootstrapper Schema { get; }

        public void Dispose()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(_rootDirectory, recursive: true);
            }
            catch (IOException)
            {
            }
        }

        private static string FindRepoRoot()
        {
            DirectoryInfo? current = new(AppContext.BaseDirectory);
            while (current != null)
            {
                if (Directory.Exists(Path.Combine(current.FullName, "dream")) &&
                    Directory.Exists(Path.Combine(current.FullName, "dream.Tests")))
                {
                    return current.FullName;
                }

                current = current.Parent;
            }

            throw new DirectoryNotFoundException("Could not locate repository root.");
        }
    }
}
