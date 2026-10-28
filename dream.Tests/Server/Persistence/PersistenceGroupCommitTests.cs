using Microsoft.Data.Sqlite;
using OpenTK.Mathematics;
using Xunit;
using Dream.Server.Persistence;
using Dream.Simulation.Persistence;
using Dream.Simulation.Players;
using Dream.Simulation.World.Geometry;

namespace Dream.Tests.Server.Persistence;

// SCALE-3: WAL journal with synchronous=NORMAL, and PersistenceWriteQueue's group commit (one
// transaction per drained group, one savepoint per write).
public sealed class PersistenceGroupCommitTests
{
    [Fact]
    public void Schema_PutsTheDatabaseInWalMode_AndEveryConnectionUsesNormalSyncAndABusyTimeout()
    {
        using PersistenceTestDatabase database = new();

        using (SqliteConnection connection = database.Schema.CreateOpenConnection())
        {
            Assert.Equal("wal", Scalar(connection, "PRAGMA journal_mode;"));
            // 1 = NORMAL (FULL is 2, the default).
            Assert.Equal(1L, Scalar(connection, "PRAGMA synchronous;"));
            Assert.Equal(5000L, Scalar(connection, "PRAGMA busy_timeout;"));
        }

        // journal_mode=WAL is stored in the file: a plain connection that sets nothing sees it too.
        using SqliteConnection plain = new(database.Schema.ConnectionString);
        plain.Open();
        Assert.Equal("wal", Scalar(plain, "PRAGMA journal_mode;"));
    }

    [Fact]
    public void BurstOf1000Writes_CommitsInAFewTransactions_AndLandsEveryRow()
    {
        using PersistenceTestDatabase database = new();
        SqlitePlayerProfileRepository profiles = new(database.Schema);
        using PersistenceWriteQueue writes = PersistenceWriteQueue.Background();
        using ManualResetEventSlim gate = new(initialState: false);

        // Hold the worker so the burst is queued before it drains, as when the tick queues a
        // fracture's writes faster than the database commits them.
        writes.Enqueue(() => gate.Wait(TimeSpan.FromSeconds(30)));
        for (int i = 0; i < 1000; i++)
        {
            PersistedPlayerProfile profile = Profile($"burst-{i:D4}", experience: i);
            writes.Enqueue(() => profiles.Save(profile));
        }

        gate.Set();
        Assert.True(writes.Flush(TimeSpan.FromSeconds(60)));

        Assert.Equal(1001, writes.CompletedWrites);
        Assert.Equal(0, writes.FailedWrites);
        Assert.Equal(0, writes.PendingCount);
        // 1000 writes in groups of at most MaxWritesPerGroup; the gate's group touches no database.
        int expectedGroups = (1000 + PersistenceWriteQueue.MaxWritesPerGroup - 1) / PersistenceWriteQueue.MaxWritesPerGroup;
        Assert.InRange(writes.CommittedGroups, expectedGroups, expectedGroups + 1);
        Assert.Equal(1000L, database.Count("SELECT COUNT(*) FROM player_profiles WHERE player_name LIKE 'burst-%';"));
        Assert.Equal(999L, profiles.TryGet("burst-0999")!.Value.Stats.Experience);
    }

    [Fact]
    public void FailingWrites_InsideAGroup_RollBackOnlyThemselves()
    {
        using PersistenceTestDatabase database = new();
        SqlitePlayerProfileRepository profiles = new(database.Schema);
        SqliteWorldPersistenceRepository world = new(database.Schema);
        using PersistenceWriteQueue writes = PersistenceWriteQueue.Background();
        using ManualResetEventSlim gate = new(initialState: false);

        writes.Enqueue(() => gate.Wait(TimeSpan.FromSeconds(30)));
        writes.Enqueue(() => profiles.Save(Profile("before", experience: 1)));
        // Writes a row, then fails: the row must not survive.
        writes.Enqueue(() =>
        {
            profiles.Save(Profile("half-written", experience: 2));
            throw new InvalidOperationException("failed after a partial write");
        });
        // A SQLite-level failure: the owner does not exist, so the foreign key rejects the cube.
        writes.Enqueue(() => world.SavePlacedCube(Cube(cubeId: 7, owner: "nobody")));
        writes.Enqueue(() => profiles.Save(Profile("after", experience: 3)));
        long groupsBefore = writes.CommittedGroups;

        gate.Set();
        Assert.True(writes.Flush(TimeSpan.FromSeconds(30)));

        Assert.Equal(3, writes.CompletedWrites);
        Assert.Equal(2, writes.FailedWrites);
        Assert.Equal(1, writes.CommittedGroups - groupsBefore);
        Assert.NotNull(profiles.TryGet("before"));
        Assert.NotNull(profiles.TryGet("after"));
        Assert.Null(profiles.TryGet("half-written"));
        Assert.Empty(world.LoadPlacedCubes());
    }

    [Fact]
    public void AGroupWhoseCommitFails_ReplaysItsWritesOneByOne_AndLosesOnlyTheBadOne()
    {
        using PersistenceTestDatabase database = new();
        SqlitePlayerProfileRepository profiles = new(database.Schema);
        SqliteWorldPersistenceRepository world = new(database.Schema);
        using PersistenceWriteQueue writes = PersistenceWriteQueue.Background();
        using ManualResetEventSlim gate = new(initialState: false);

        writes.Enqueue(() => gate.Wait(TimeSpan.FromSeconds(30)));
        writes.Enqueue(() => profiles.Save(Profile("first", experience: 1)));
        // Deferred foreign keys are checked at COMMIT, so this write passes its savepoint and then
        // fails the whole group's commit.
        writes.Enqueue(() =>
        {
            using (SqliteConnectionLease lease = database.Schema.OpenConnection())
            using (SqliteCommand defer = lease.Connection.CreateCommand())
            {
                defer.CommandText = "PRAGMA defer_foreign_keys=ON;";
                defer.ExecuteNonQuery();
            }

            world.SavePlacedCube(Cube(cubeId: 11, owner: "nobody"));
        });
        writes.Enqueue(() => profiles.Save(Profile("second", experience: 2)));
        long groupsBefore = writes.CommittedGroups;

        gate.Set();
        Assert.True(writes.Flush(TimeSpan.FromSeconds(30)));

        // The failed group committed nothing; its two good writes were replayed in groups of their own.
        Assert.Equal(2, writes.CommittedGroups - groupsBefore);
        Assert.NotNull(profiles.TryGet("first"));
        Assert.NotNull(profiles.TryGet("second"));
        Assert.Empty(world.LoadPlacedCubes());
        Assert.Equal(1, writes.FailedWrites);
        Assert.Equal(3, writes.CompletedWrites);
    }

    [Fact]
    public void WritesInsideAGroup_KeepTheirOrder()
    {
        using PersistenceTestDatabase database = new();
        SqlitePlayerProfileRepository profiles = new(database.Schema);
        SqliteWorldPersistenceRepository world = new(database.Schema);
        using PersistenceWriteQueue writes = PersistenceWriteQueue.Background();
        using ManualResetEventSlim gate = new(initialState: false);
        List<int> order = [];

        writes.Enqueue(() => gate.Wait(TimeSpan.FromSeconds(30)));
        profiles.Save(Profile("owner", experience: 0));
        for (int i = 1; i <= 300; i++)
        {
            int step = i;
            PersistedPlayerProfile profile = Profile("ordered", experience: step);
            writes.Enqueue(() =>
            {
                order.Add(step);
                profiles.Save(profile);
            });
        }

        // Save, delete, save again: only the order save -> delete -> save leaves the cube with health 2.
        writes.Enqueue(() => world.SavePlacedCube(Cube(cubeId: 9, owner: "owner") with { CurrentHealth = 1 }));
        writes.Enqueue(() => world.DeletePlacedCube(9));
        writes.Enqueue(() => world.SavePlacedCube(Cube(cubeId: 9, owner: "owner") with { CurrentHealth = 2 }));

        gate.Set();
        Assert.True(writes.Flush(TimeSpan.FromSeconds(30)));

        Assert.Equal(Enumerable.Range(1, 300), order);
        Assert.Equal(300, profiles.TryGet("ordered")!.Value.Stats.Experience);
        Assert.Equal(2, Assert.Single(world.LoadPlacedCubes()).CurrentHealth);
    }

    [Fact]
    public void QueuedRead_SeesEveryWriteQueuedBeforeIt_AndIsNotCountedAsAWrite()
    {
        using PersistenceTestDatabase database = new();
        SqlitePlayerProfileRepository profiles = new(database.Schema);
        using PersistenceWriteQueue writes = PersistenceWriteQueue.Background();
        using ManualResetEventSlim gate = new(initialState: false);
        PersistedPlayerProfile? seen = null;

        writes.Enqueue(() => gate.Wait(TimeSpan.FromSeconds(30)));
        writes.Enqueue(() => profiles.Save(Profile("reader", experience: 41)));
        writes.EnqueueRead(() => seen = profiles.TryGet("reader"));
        writes.Enqueue(() => profiles.Save(Profile("reader", experience: 42)));

        gate.Set();
        Assert.True(writes.Flush(TimeSpan.FromSeconds(30)));

        Assert.Equal(41, seen!.Value.Stats.Experience);
        Assert.Equal(3, writes.CompletedWrites);
        Assert.Equal(42, profiles.TryGet("reader")!.Value.Stats.Experience);
    }

    [Fact]
    public void ReadsOutsideTheWorker_DoNotWaitForAnOpenGroup()
    {
        using PersistenceTestDatabase database = new();
        SqlitePlayerProfileRepository profiles = new(database.Schema);
        profiles.Save(Profile("committed", experience: 5));
        using PersistenceWriteQueue writes = PersistenceWriteQueue.Background();
        using ManualResetEventSlim inGroup = new(initialState: false);
        using ManualResetEventSlim release = new(initialState: false);
        using ManualResetEventSlim gate = new(initialState: false);

        // The worker holds the group's write transaction open (a write already ran in it). The gate
        // keeps the save and the hold in one group.
        writes.Enqueue(() => gate.Wait(TimeSpan.FromSeconds(30)));
        writes.Enqueue(() => profiles.Save(Profile("uncommitted", experience: 6)));
        writes.Enqueue(() =>
        {
            inGroup.Set();
            release.Wait(TimeSpan.FromSeconds(30));
        });
        gate.Set();
        Assert.True(inGroup.Wait(TimeSpan.FromSeconds(10)));

        // WAL: a reader on another connection is not blocked by the open writer and does not see
        // its uncommitted rows.
        Task<PersistedPlayerProfile?> read = Task.Run(() => profiles.TryGet("committed"));
        Assert.True(read.Wait(TimeSpan.FromSeconds(5)), "A read waited for the write queue's open transaction.");
        Assert.Equal(5, read.Result!.Value.Stats.Experience);
        Assert.Null(profiles.TryGet("uncommitted"));

        release.Set();
        Assert.True(writes.Flush(TimeSpan.FromSeconds(30)));
        Assert.NotNull(profiles.TryGet("uncommitted"));
    }

    internal static PersistedPlayerProfile Profile(string name, int experience, int level = 1, PlayerInteractionMode mode = PlayerInteractionMode.Building)
    {
        DateTime now = DateTime.UtcNow;
        return new PersistedPlayerProfile(
            name,
            name,
            mode,
            new PersistedCreatureStats(100, 100, 10, 10, 10, 10, 6f, 2f, level, experience),
            now,
            now);
    }

    private static PersistedPlacedCube Cube(int cubeId, string owner)
    {
        DateTime now = DateTime.UtcNow;
        return new PersistedPlacedCube(
            cubeId,
            owner,
            new Vector3(3f, 1f, 3f),
            GeometryShapeKind.SolidCube,
            GeometryMaterialId.Stone,
            5,
            5,
            now,
            now);
    }

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }
}

internal sealed class PersistenceTestDatabase : IDisposable
{
    private readonly string _rootDirectory;

    public PersistenceTestDatabase()
    {
        _rootDirectory = Path.Combine(Path.GetTempPath(), $"dream-group-commit-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_rootDirectory);
        SchemaPath = Path.Combine(Dream.Tests.Integration.RepoRoot.Find(), "dream", "config", "player-profiles.init.sql");
        DatabasePath = Path.Combine(_rootDirectory, "persistence.sqlite");
        Schema = new SqliteSchemaBootstrapper(DatabasePath, SchemaPath);
        Schema.EnsureSchema();
    }

    public SqliteSchemaBootstrapper Schema { get; }

    public string DatabasePath { get; }

    public string SchemaPath { get; }

    public long Count(string sql)
    {
        using SqliteConnection connection = Schema.CreateOpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_rootDirectory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
