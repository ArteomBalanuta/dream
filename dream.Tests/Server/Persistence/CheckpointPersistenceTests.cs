using Dream.Multiplayer.Protocol;
using Dream.Server;
using Dream.Server.Persistence;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World.Geometry;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Server.Persistence;

// Regression coverage for the 5-second checkpoint stall: the checkpoint used to write every row
// on its own connection on the tick thread (about 0.5 s per checkpoint in the 2026-07-05 perf logs).
public sealed class CheckpointPersistenceTests
{
    private const string Player = "player-one";

    [Fact]
    public void SqliteWriter_UpdatesExistingRowsInOneCheckpoint()
    {
        using TempDatabase database = new();
        SeedPlayerCubePieceAndEnemy(database);
        SqliteWorldPersistenceRepository world = new(database.Schema);

        new SqlitePersistenceCheckpointWriter(database.Schema).Write(new PersistenceCheckpoint(
            [new PersistedPlayerWorldState(Player, new Vector3(4f, 1.5f, 6f), Vector3.UnitX, 70, 0, DateTime.UtcNow)],
            [new PlayerCheckpointVitals(Player, PlayerInteractionMode.Combat, 70, 100)],
            [CreateEnemy(id: 50, new Vector3(9f, 1f, 9f), currentHp: 12)],
            [CreateCube(id: 7, center: new Vector3(4.6f, 1.5f, 3.2f), currentHealth: 3)],
            [CreatePiece(cubeId: 8, index: 0, center: new Vector3(2.9f, 1.1f, 2.2f))]));

        PersistedPlayerWorldState? state = world.TryGetPlayerWorldState(Player);
        Assert.NotNull(state);
        Assert.Equal(new Vector3(4f, 1.5f, 6f), state.Value.Position);
        PersistedPlacedCube cube = Assert.Single(world.LoadPlacedCubes());
        Assert.Equal(new Vector3(4.6f, 1.5f, 3.2f), cube.CenterPosition);
        Assert.Equal(new Vector3(4f, 1f, 3f), cube.CellPosition);
        Assert.Equal(3, cube.CurrentHealth);
        Assert.Equal(new Vector3(2.9f, 1.1f, 2.2f), Assert.Single(world.LoadCubeFracturePieces()).Center);
        PersistedEnemyInstance enemy = Assert.Single(world.LoadActiveEnemies());
        Assert.Equal(new Vector3(9f, 1f, 9f), enemy.Position);
        Assert.Equal(12, enemy.Stats.CurrentHealth);
        PersistedPlayerProfile profile = Assert.IsType<PersistedPlayerProfile>(
            new SqlitePlayerProfileRepository(database.Schema).TryGet(Player));
        Assert.Equal(PlayerInteractionMode.Combat, profile.InteractionMode);
        Assert.Equal(70, profile.Stats.CurrentHealth);
    }

    [Fact]
    public void SqliteWriter_LateCheckpoint_DoesNotResurrectDeletedRows()
    {
        // The background write can land after the tick thread deleted a destroyed cube, a dead
        // enemy or a stale piece. Re-inserting them would duplicate cubes on the next restart.
        using TempDatabase database = new();
        SeedPlayerCubePieceAndEnemy(database);
        SqliteWorldPersistenceRepository world = new(database.Schema);
        world.DeletePlacedCube(7);
        world.DeleteEnemyInstance(50);
        world.DeleteCubeFracturePieces(8);

        new SqlitePersistenceCheckpointWriter(database.Schema).Write(new PersistenceCheckpoint(
            [],
            [],
            [CreateEnemy(id: 50, new Vector3(9f, 1f, 9f), currentHp: 12)],
            [CreateCube(id: 7, center: new Vector3(4.6f, 1.5f, 3.2f), currentHealth: 3)],
            [CreatePiece(cubeId: 8, index: 0, center: new Vector3(2.9f, 1.1f, 2.2f))]));

        Assert.Empty(world.LoadPlacedCubes());
        Assert.Empty(world.LoadActiveEnemies());
        Assert.Empty(world.LoadCubeFracturePieces());
    }

    [Fact]
    public void SqliteWriter_LateCheckpoint_KeepsProgressionSavedAfterCapture()
    {
        using TempDatabase database = new();
        SqlitePlayerProfileRepository profiles = new(database.Schema);
        PersistedPlayerProfile profile = profiles.GetOrCreate(Player);
        profiles.Save(profile with { Stats = profile.Stats with { Level = 4, Experience = 900 } });

        new SqlitePersistenceCheckpointWriter(database.Schema).Write(new PersistenceCheckpoint(
            [],
            [new PlayerCheckpointVitals(Player, PlayerInteractionMode.Building, 40, 100)],
            [],
            [],
            []));

        PersistedPlayerProfile saved = Assert.IsType<PersistedPlayerProfile>(profiles.TryGet(Player));
        Assert.Equal(4, saved.Stats.Level);
        Assert.Equal(900, saved.Stats.Experience);
        Assert.Equal(40, saved.Stats.CurrentHealth);
    }

    [Fact]
    public void SqliteWriter_FailedRow_RollsBackWholeCheckpoint()
    {
        using TempDatabase database = new();
        SeedPlayerCubePieceAndEnemy(database);
        SqliteWorldPersistenceRepository world = new(database.Schema);
        PersistedPlayerWorldState before = Assert.IsType<PersistedPlayerWorldState>(world.TryGetPlayerWorldState(Player));

        // current_health > max_health violates a CHECK constraint after the world state was written.
        Assert.ThrowsAny<Exception>(() => new SqlitePersistenceCheckpointWriter(database.Schema).Write(new PersistenceCheckpoint(
            [new PersistedPlayerWorldState(Player, new Vector3(20f, 1.5f, 20f), Vector3.UnitZ, 70, 0, DateTime.UtcNow)],
            [],
            [],
            [CreateCube(id: 7, center: new Vector3(4.6f, 1.5f, 3.2f), currentHealth: 999)],
            [])));

        Assert.Equal(before.Position, world.TryGetPlayerWorldState(Player)!.Value.Position);
    }

    [Fact]
    public void BackgroundWriter_EnqueueReturnsWhileWriteBlocks_AndKeepsOnlyLatestPending()
    {
        BlockingCheckpointWriter writer = new();
        using BackgroundCheckpointWriter background = new(writer);
        PersistenceCheckpoint first = PersistenceCheckpoint.Empty with { PlayerVitals = [] };
        PersistenceCheckpoint second = PersistenceCheckpoint.Empty with { PlayerVitals = [] };
        PersistenceCheckpoint latest = PersistenceCheckpoint.Empty with { PlayerVitals = [] };

        background.Enqueue(first);
        Assert.True(writer.Started.Wait(TimeSpan.FromSeconds(5)));
        background.Enqueue(second);
        background.Enqueue(latest);
        Assert.False(background.Flush(TimeSpan.FromMilliseconds(50)));

        writer.Release.Set();
        Assert.True(background.Flush(TimeSpan.FromSeconds(5)));

        Assert.Equal(2, background.CompletedWrites);
        Assert.Equal(1, background.SupersededCheckpoints);
        Assert.Same(first, writer.Written[0]);
        Assert.Same(latest, writer.Written[1]);
    }

    [Fact]
    public void BackgroundWriter_FailedWrite_IsCountedAndDoesNotStopLaterWrites()
    {
        FailingOnceCheckpointWriter writer = new();
        using BackgroundCheckpointWriter background = new(writer);

        background.Enqueue(PersistenceCheckpoint.Empty);
        Assert.True(background.Flush(TimeSpan.FromSeconds(5)));
        background.Enqueue(PersistenceCheckpoint.Empty);
        Assert.True(background.Flush(TimeSpan.FromSeconds(5)));

        Assert.Equal(1, background.FailedWrites);
        Assert.Equal(1, background.CompletedWrites);
    }

    [Fact]
    public void DedicatedServerHost_CheckpointTick_DoesNotWaitForDatabaseWrite()
    {
        using TempDatabase database = new();
        BlockingCheckpointWriter writer = new();
        WorldSessionRuntime runtime = new(
            10,
            new Vector3(5f, 1.5f, 5f),
            new SceneEventHub(),
            playerProfiles: new SqlitePlayerProfileRepository(database.Schema),
            worldPersistence: new SqliteWorldPersistenceRepository(database.Schema),
            checkpointWriter: writer);
        using DedicatedServerHost host = new(new RuntimeAuthoritativeSessionHost(runtime), CreateRouter());
        BackgroundCheckpointWriter background = Assert.IsType<BackgroundCheckpointWriter>(host.CheckpointWrites);

        // 40 advances of 8 ticks reach the 5-second (320 tick) checkpoint. With the write
        // blocked, a synchronous checkpoint would hang here.
        Task advance = Task.Run(() =>
        {
            for (int index = 0; index < 40; index++)
            {
                host.AdvanceTime(SimulationClock.FixedDeltaSeconds * SimulationClock.DefaultMaxTicksPerAdvance);
            }
        });

        Assert.True(advance.Wait(TimeSpan.FromSeconds(30)), "Tick thread waited on the checkpoint write.");
        Assert.True(writer.Started.Wait(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, background.CompletedWrites);

        writer.Release.Set();
        Assert.True(background.Flush(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, background.CompletedWrites);
    }

    private static void SeedPlayerCubePieceAndEnemy(TempDatabase database)
    {
        new SqlitePlayerProfileRepository(database.Schema).GetOrCreate(Player);
        SqliteWorldPersistenceRepository world = new(database.Schema);
        world.SavePlayerWorldState(new PersistedPlayerWorldState(Player, new Vector3(1f, 1.5f, 1f), Vector3.UnitZ, 100, 0, DateTime.UtcNow));
        world.SavePlacedCube(CreateCube(id: 7, center: new Vector3(3.5f, 1.5f, 3.5f), currentHealth: 5) with
        {
            CellPosition = new Vector3(3f, 1f, 3f)
        });
        world.SaveCubeFracturePiece(CreatePiece(cubeId: 8, index: 0, center: new Vector3(2.5f, 1.1f, 2.5f)));
        world.SaveEnemyInstance(CreateEnemy(id: 50, new Vector3(1f, 1f, 1f), currentHp: 30));
    }

    private static PersistedPlacedCube CreateCube(int id, Vector3 center, int currentHealth)
    {
        return new PersistedPlacedCube(
            id,
            Player,
            new Vector3(MathF.Floor(center.X), MathF.Floor(center.Y), MathF.Floor(center.Z)),
            GeometryShapeKind.SolidCube,
            GeometryMaterialId.Dirt,
            currentHealth,
            MaxHealth: 5,
            DateTime.UtcNow,
            DateTime.UtcNow,
            center);
    }

    private static PersistedCubeFracturePiece CreatePiece(int cubeId, int index, Vector3 center)
    {
        return new PersistedCubeFracturePiece(
            cubeId,
            index,
            Player,
            new Vector3(2f, 1f, 2f),
            center,
            0.2f,
            GeometryMaterialId.Dirt,
            DateTime.UtcNow,
            DateTime.UtcNow);
    }

    private static PersistedEnemyInstance CreateEnemy(int id, Vector3 position, int currentHp)
    {
        return new PersistedEnemyInstance(
            id,
            "wolf",
            "Wolf",
            position,
            Vector3.UnitZ,
            new PersistedCreatureStats(currentHp, 30, 10, 10, 10, 0, 2.8f, 0.95f, 2),
            DateTime.UtcNow,
            DateTime.UtcNow);
    }

    private static ProtocolRouter CreateRouter()
    {
        return new ProtocolRouter(
            [
                new ClientHelloHandler(),
                new JoinRequestHandler(),
                new PlayerCommandHandler(),
                new PlayerActionHandler()
            ],
            Array.Empty<IClientMessageHandler>());
    }

    private sealed class BlockingCheckpointWriter : IPersistenceCheckpointWriter
    {
        public ManualResetEventSlim Started { get; } = new();

        public ManualResetEventSlim Release { get; } = new();

        public List<PersistenceCheckpoint> Written { get; } = [];

        public void Write(PersistenceCheckpoint checkpoint)
        {
            Started.Set();
            Release.Wait(TimeSpan.FromSeconds(30));
            lock (Written)
            {
                Written.Add(checkpoint);
            }
        }
    }

    private sealed class FailingOnceCheckpointWriter : IPersistenceCheckpointWriter
    {
        private int _calls;

        public void Write(PersistenceCheckpoint checkpoint)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                throw new InvalidOperationException("disk full");
            }
        }
    }

    private sealed class TempDatabase : IDisposable
    {
        private readonly string _rootDirectory;

        public TempDatabase()
        {
            _rootDirectory = Path.Combine(Path.GetTempPath(), $"dream-checkpoint-tests-{Guid.NewGuid():N}");
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
