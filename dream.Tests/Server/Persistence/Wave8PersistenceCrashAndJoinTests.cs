using System.Diagnostics;
using Microsoft.Data.Sqlite;
using OpenTK.Mathematics;
using Xunit;
using Xunit.Abstractions;
using Dream.Server.Persistence;
using Dream.Simulation.Persistence;
using Dream.Simulation.Players;
using Dream.Simulation.World.Geometry;

namespace Dream.Tests.Server.Persistence;

// Wave 8 tester, SCALE-3 (WAL + group commit, join prefetch on the write worker).
public sealed class Wave8PersistenceCrashAndJoinTests(ITestOutputHelper output)
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(60);

    // Crash consistency: the files are copied while the worker holds a group open with hundreds of
    // uncommitted rows (enough to spill pages into the WAL), which is what a process kill or power
    // cut mid-group leaves on disk. Opening the copy must recover to the last commit: integrity ok,
    // every committed row, none of the open group's; and replaying that group's writes on the copy
    // (what a restart re-deriving them would do) lands them.
    [Fact]
    public void KillMidGroup_LeavesTheLastCommittedState_AndTheGroupReplaysCleanly()
    {
        using PersistenceTestDatabase database = new();
        SqlitePlayerProfileRepository profiles = new(database.Schema);
        using (PersistenceWriteQueue inline = PersistenceWriteQueue.Inline())
        {
            inline.Enqueue(() => profiles.Save(Profile("committed", experience: 5)));
        }

        using (SqliteConnection connection = database.Schema.CreateOpenConnection())
        using (SqliteCommand create = connection.CreateCommand())
        {
            create.CommandText = "CREATE TABLE crash_filler (payload BLOB NOT NULL);";
            create.ExecuteNonQuery();
        }

        using PersistenceWriteQueue writes = PersistenceWriteQueue.Background();
        using ManualResetEventSlim gate = new();
        using ManualResetEventSlim groupOpen = new();
        using ManualResetEventSlim release = new();
        const int GroupRows = 600;
        // Hold the worker so the groups are deterministic: 256 + 256 commit, the third group
        // (88 profiles, then the hold) stays open.
        using ManualResetEventSlim gateEntered = new();
        writes.Enqueue(() =>
        {
            gateEntered.Set();
            gate.Wait(Deadline);
        });
        Assert.True(gateEntered.Wait(Deadline));
        for (int i = 0; i < GroupRows; i++)
        {
            PersistedPlayerProfile profile = Profile($"open-group-{i:D4}", experience: i);
            writes.Enqueue(() => profiles.Save(profile));
        }

        writes.Enqueue(() =>
        {
            // 4 MB of uncommitted pages, past SQLite's 2 MB page cache, so they spill into the WAL
            // without a commit frame.
            using (SqliteConnectionLease lease = database.Schema.OpenConnection())
            using (SqliteCommand fill = lease.Connection.CreateCommand())
            {
                fill.CommandText =
                    "WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 4000) " +
                    "INSERT INTO crash_filler (payload) SELECT randomblob(1024) FROM n;";
                fill.ExecuteNonQuery();
            }

            groupOpen.Set();
            release.Wait(Deadline);
        });
        gate.Set();

        Assert.True(groupOpen.Wait(Deadline));
        string crashDirectory = Path.Combine(Path.GetDirectoryName(database.DatabasePath)!, "crash-copy");
        Directory.CreateDirectory(crashDirectory);
        string crashPath = Path.Combine(crashDirectory, Path.GetFileName(database.DatabasePath));
        CopyShared(database.DatabasePath, crashPath);
        bool walCopied = CopyShared(database.DatabasePath + "-wal", crashPath + "-wal");
        long walBytes = walCopied ? new FileInfo(crashPath + "-wal").Length : 0;
        release.Set();
        Assert.True(writes.Flush(Deadline));

        SqliteSchemaBootstrapper crashed = new(crashPath, database.SchemaPath);
        using (SqliteConnection connection = crashed.CreateOpenConnection())
        {
            Assert.Equal("ok", Scalar(connection, "PRAGMA integrity_check;"));
        }

        SqlitePlayerProfileRepository recovered = new(crashed);
        Assert.Equal(5, recovered.TryGet("committed")!.Value.Stats.Experience);
        // Groups before the open one may have committed (the worker drains up to 256 per group).
        long committedOpenRows = CountProfiles(crashed, "open-group-%");
        output.WriteLine($"wal copied={walCopied} bytes={walBytes}; open-group rows visible after recovery={committedOpenRows} of {GroupRows}");
        Assert.Equal(2L * PersistenceWriteQueue.MaxWritesPerGroup, committedOpenRows);
        using (SqliteConnection connection = crashed.CreateOpenConnection())
        {
            Assert.Equal(0L, Convert.ToInt64(Scalar(connection, "SELECT COUNT(*) FROM crash_filler;")));
        }

        // Replay on the recovered copy.
        using (PersistenceWriteQueue replay = PersistenceWriteQueue.Background())
        {
            for (int i = 0; i < GroupRows; i++)
            {
                PersistedPlayerProfile profile = Profile($"open-group-{i:D4}", experience: i);
                replay.Enqueue(() => recovered.Save(profile));
            }

            Assert.True(replay.Flush(Deadline));
            Assert.Equal(0, replay.FailedWrites);
        }

        Assert.Equal(GroupRows, CountProfiles(crashed, "open-group-%"));
        SqliteConnection.ClearAllPools();
    }

    // A queued read (the join prefetch, WorldSessionRuntime.BeginPersistedPlayerPrefetch) used to
    // run inside the open group's transaction, so it saw that group's uncommitted rows, and when the
    // group's commit failed PersistedPlayerPrefetch kept that dirty result ("first result wins"):
    // the joining player was seated with a profile the database never held (XP 77). A read now ends
    // the group: the writes before it commit (or fail) first, and the read runs outside any
    // transaction, so it only sees committed rows.
    [Fact]
    public void PrefetchAfterAGroupWhoseCommitFails_ReadsOnlyCommittedRows()
    {
        using PersistenceTestDatabase database = new();
        SqlitePlayerProfileRepository profiles = new(database.Schema);
        SqliteWorldPersistenceRepository world = new(database.Schema);
        using PersistenceWriteQueue writes = PersistenceWriteQueue.Background();
        using ManualResetEventSlim gate = new(initialState: false);
        PersistedPlayerPrefetch prefetch = new("ghost");

        writes.Enqueue(() => gate.Wait(Deadline));
        // Saves the ghost's profile, then defers a foreign-key violation to COMMIT: the savepoint
        // passes, the group's commit fails, and the replay of this write alone fails again.
        writes.Enqueue(() =>
        {
            profiles.Save(Profile("ghost", experience: 77));
            using (SqliteConnectionLease lease = database.Schema.OpenConnection())
            using (SqliteCommand defer = lease.Connection.CreateCommand())
            {
                defer.CommandText = "PRAGMA defer_foreign_keys=ON;";
                defer.ExecuteNonQuery();
            }

            world.SavePlacedCube(Cube(cubeId: 21, owner: "nobody"));
        });
        // Read-only, like the join prefetch.
        writes.EnqueueRead(() => prefetch.Complete(profiles.TryGet("ghost"), world.TryGetPlayerWorldState("ghost")));
        gate.Set();
        Assert.True(writes.Flush(Deadline));

        Assert.True(prefetch.TryGetLoadResult(out PersistedPlayerProfile? seen, out _));
        PersistedPlayerProfile? stored = profiles.TryGet("ghost");
        output.WriteLine($"prefetch saw {(seen.HasValue ? $"XP {seen.Value.Stats.Experience}" : "no row")}; database holds {(stored.HasValue ? $"XP {stored.Value.Stats.Experience}" : "no row")}; failed writes={writes.FailedWrites}");
        // The ghost's write was rolled back (its group's commit and its replay both failed), so
        // neither the database nor the prefetch holds it.
        Assert.True(writes.FailedWrites >= 1);
        Assert.Null(stored);
        Assert.Null(seen);
        SqliteConnection.ClearAllPools();
    }

    // A join that arrives while 1000 writes are queued: its prefetch read waits behind all of them
    // (by design, so it sees this player's latest save). The receive thread no longer waits for it
    // (the host defers the join on the tick instead), so this measures how long the join is
    // deferred. Measured on the development machine: well under 5 s (see the output).
    [Theory]
    [InlineData("profiles")]
    [InlineData("placed-cubes")]
    public void JoinPrefetch_Behind1000QueuedWrites_CompletesWellInsideTheReceiveWait(string kind)
    {
        using PersistenceTestDatabase database = new();
        SqlitePlayerProfileRepository profiles = new(database.Schema);
        SqliteWorldPersistenceRepository world = new(database.Schema);
        profiles.Save(Profile("owner", experience: 0));
        using PersistenceWriteQueue writes = PersistenceWriteQueue.Background();
        using ManualResetEventSlim gate = new(initialState: false);

        writes.Enqueue(() => gate.Wait(Deadline));
        for (int i = 0; i < 1000; i++)
        {
            int index = i;
            if (kind == "profiles")
            {
                PersistedPlayerProfile profile = Profile($"queued-{index:D4}", experience: index);
                writes.Enqueue(() => profiles.Save(profile));
            }
            else
            {
                writes.Enqueue(() => world.SavePlacedCube(Cube(cubeId: 1000 + index, owner: "owner") with
                {
                    CellPosition = new Vector3(index % 50, 1, index / 50)
                }));
            }
        }

        PersistedPlayerPrefetch prefetch = new("joiner");
        Stopwatch wait = new();
        writes.EnqueueRead(() => prefetch.Complete(profiles.GetOrCreate("joiner"), world.TryGetPlayerWorldState("joiner")));
        wait.Start();
        gate.Set();
        Assert.True(prefetch.Wait(TimeSpan.FromSeconds(30)), "the prefetch never completed");
        wait.Stop();
        Assert.True(writes.Flush(Deadline));

        output.WriteLine($"{kind}: prefetch behind 1000 queued writes completed in {wait.Elapsed.TotalMilliseconds:0} ms, groups={writes.CommittedGroups}, failed={writes.FailedWrites}");
        Assert.Equal(0, writes.FailedWrites);
        Assert.True(wait.Elapsed < TimeSpan.FromSeconds(5), $"{wait.Elapsed.TotalMilliseconds:0} ms");
        SqliteConnection.ClearAllPools();
    }

    private static bool CopyShared(string source, string destination)
    {
        if (!File.Exists(source))
        {
            return false;
        }

        using FileStream input = new(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using FileStream outputStream = new(destination, FileMode.Create, FileAccess.Write);
        input.CopyTo(outputStream);
        return true;
    }

    private static long CountProfiles(SqliteSchemaBootstrapper schema, string pattern)
    {
        using SqliteConnection connection = schema.CreateOpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM player_profiles WHERE player_name LIKE $pattern;";
        command.Parameters.AddWithValue("$pattern", pattern);
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static PersistedPlayerProfile Profile(string name, int experience)
    {
        DateTime now = DateTime.UtcNow;
        return new PersistedPlayerProfile(
            name,
            name,
            PlayerInteractionMode.Building,
            new PersistedCreatureStats(100, 100, 10, 10, 10, 10, 6f, 2f, 1, experience),
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
}
