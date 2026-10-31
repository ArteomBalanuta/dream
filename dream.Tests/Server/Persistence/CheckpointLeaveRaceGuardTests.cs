using Microsoft.Data.Sqlite;
using OpenTK.Mathematics;
using Xunit;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Server;
using Dream.Server.Persistence;
using Dream.Simulation.Persistence;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;

namespace Dream.Tests.Server.Persistence;

// Persistence wave A (A4): the updated_utc guard on checkpoint writes. A checkpoint captured before
// a player leaves can be written after the leave saved the player's world state and profile
// (through the write queue); a checkpoint row carries its capture time and only replaces a stored
// row that is strictly older, so the leave's values survive. Before the guard, the late checkpoint
// write stamped every row with its write time and overwrote the leave.
public sealed class CheckpointLeaveRaceGuardTests
{
    private const string Session = "a4-guard";
    private const string PlayerName = "Name a4-guard";

    // Values no real capture holds, so a row that took them was written from the stale checkpoint.
    private static readonly Vector3 StalePosition = new(123f, 45f, 67f);
    private const int StaleHealth = 1;
    private const int StaleMaxHealth = 999;

    // 1. Capture, then the player leaves (the queue writes the world state and the profile), then
    // the stale checkpoint is written: the leave's world-state row and vitals survive.
    [Fact]
    public void StaleCheckpointWrittenAfterTheLeave_KeepsTheLeavesWorldStateAndVitals()
    {
        using PersistenceTestDatabase database = new();
        using DedicatedServerHost host = CreateHost(database);
        WorldSessionRuntime runtime = host.Runtime!;
        Join(host, $"connection-{Session}");

        PersistenceCheckpoint stale = Assert.IsType<PersistenceCheckpoint>(runtime.CapturePersistenceCheckpoint(full: true));
        PersistedPlayerWorldState capturedState = Assert.Single(stale.PlayerWorldStates, state => state.PlayerName == PlayerName);
        PlayerCheckpointVitals capturedVitals = Assert.Single(stale.PlayerVitals, vitals => vitals.PlayerName == PlayerName);
        Assert.NotEqual(default, capturedVitals.CapturedUtc);
        Assert.Equal(capturedState.UpdatedUtc, capturedVitals.CapturedUtc);

        Leave(host, $"connection-{Session}");
        WorldStateRow leftState = ReadWorldState(database);
        ProfileRow leftProfile = ReadProfile(database);
        Assert.True(string.CompareOrdinal(leftState.UpdatedUtc, Format(capturedState.UpdatedUtc)) > 0, "the leave did not save the world state after the capture");
        Assert.True(string.CompareOrdinal(leftProfile.UpdatedUtc, Format(capturedVitals.CapturedUtc)) > 0, "the leave did not save the profile after the capture");

        new SqlitePersistenceCheckpointWriter(database.Schema).Write(WithStaleValues(stale));

        Assert.Equal(leftState, ReadWorldState(database));
        Assert.Equal(leftProfile, ReadProfile(database));
    }

    // 2. A checkpoint captured after a rejoin does overwrite; the earlier stale one still does not.
    [Fact]
    public void CheckpointCapturedAfterARejoin_Overwrites()
    {
        using PersistenceTestDatabase database = new();
        using DedicatedServerHost host = CreateHost(database);
        WorldSessionRuntime runtime = host.Runtime!;
        Join(host, "connection-first");
        PersistenceCheckpoint stale = Assert.IsType<PersistenceCheckpoint>(runtime.CapturePersistenceCheckpoint(full: true));
        Leave(host, "connection-first");
        Join(host, "connection-second");

        PersistenceCheckpoint fresh = Assert.IsType<PersistenceCheckpoint>(runtime.CapturePersistenceCheckpoint(full: true));
        PersistedPlayerWorldState capturedState = Assert.Single(fresh.PlayerWorldStates, state => state.PlayerName == PlayerName);
        new SqlitePersistenceCheckpointWriter(database.Schema).Write(WithStaleValues(fresh));

        WorldStateRow state = ReadWorldState(database);
        ProfileRow profile = ReadProfile(database);
        Assert.Equal(StalePosition.X, state.PositionX);
        Assert.Equal(StaleHealth, state.CurrentHp);
        Assert.Equal(Format(capturedState.UpdatedUtc), state.UpdatedUtc);
        Assert.Equal(StaleHealth, profile.CurrentHp);
        Assert.Equal(StaleMaxHealth, profile.MaxHp);

        // Written late, the capture from before the leave changes nothing.
        new SqlitePersistenceCheckpointWriter(database.Schema).Write(stale);
        Assert.Equal(state, ReadWorldState(database));
        Assert.Equal(profile, ReadProfile(database));
    }

    // 3. With no stored world-state row, the checkpoint still inserts one, stamped with its capture
    // time (and still only for a player with a profile).
    [Fact]
    public void FirstCheckpoint_WithNoStoredRow_Inserts()
    {
        using PersistenceTestDatabase database = new();
        new SqlitePlayerProfileRepository(database.Schema).GetOrCreate(PlayerName);
        DateTime captured = DateTime.UtcNow.AddMinutes(-5);

        new SqlitePersistenceCheckpointWriter(database.Schema).Write(PersistenceCheckpoint.Empty with
        {
            PlayerWorldStates =
            [
                new PersistedPlayerWorldState(PlayerName, new Vector3(4f, 1.5f, 6f), Vector3.UnitX, 70, 0, captured),
                new PersistedPlayerWorldState("no profile", new Vector3(4f, 1.5f, 6f), Vector3.UnitX, 70, 0, captured)
            ]
        });

        WorldStateRow row = ReadWorldState(database);
        Assert.Equal(4f, row.PositionX);
        Assert.Equal(70, row.CurrentHp);
        Assert.Equal(Format(captured), row.UpdatedUtc);
        Assert.Equal(1, database.Count("SELECT COUNT(*) FROM player_world_state;"));
    }

    // 4. Merging queued checkpoints keeps whole rows, so each row keeps its own capture time, and
    // the writer guards each row by it.
    [Fact]
    public void MergedCheckpoints_KeepEachRowsCaptureTime()
    {
        using PersistenceTestDatabase database = new();
        SqlitePlayerProfileRepository profiles = new(database.Schema);
        profiles.GetOrCreate("alice");
        profiles.GetOrCreate("bob");
        DateTime t1 = DateTime.UtcNow.AddMinutes(1);
        DateTime t2 = t1.AddSeconds(5);
        DateTime t3 = t2.AddSeconds(5);
        PersistenceCheckpoint older = PersistenceCheckpoint.Empty with
        {
            PlayerWorldStates = [State("alice", 10f, t1), State("bob", 10f, t1)],
            PlayerVitals = [Vitals("alice", 10, t1), Vitals("bob", 10, t1)]
        };
        PersistenceCheckpoint newer = PersistenceCheckpoint.Empty with
        {
            IsIncremental = true,
            PlayerWorldStates = [State("bob", 30f, t3)],
            PlayerVitals = [Vitals("bob", 30, t3)]
        };

        PersistenceCheckpoint merged = PersistenceCheckpoint.Merge(older, newer);

        Assert.Equal(t1, Assert.Single(merged.PlayerWorldStates, state => state.PlayerName == "alice").UpdatedUtc);
        Assert.Equal(t3, Assert.Single(merged.PlayerWorldStates, state => state.PlayerName == "bob").UpdatedUtc);
        Assert.Equal(t1, Assert.Single(merged.PlayerVitals, vitals => vitals.PlayerName == "alice").CapturedUtc);
        Assert.Equal(t3, Assert.Single(merged.PlayerVitals, vitals => vitals.PlayerName == "bob").CapturedUtc);

        // Both players' rows were saved at t2 (as a leave would), between the two captures.
        new SqlitePersistenceCheckpointWriter(database.Schema).Write(PersistenceCheckpoint.Empty with
        {
            PlayerWorldStates = [State("alice", 20f, t2), State("bob", 20f, t2)],
            PlayerVitals = [Vitals("alice", 20, t2), Vitals("bob", 20, t2)]
        });

        new SqlitePersistenceCheckpointWriter(database.Schema).Write(merged);

        Assert.Equal(20f, ReadWorldState(database, "alice").PositionX);
        Assert.Equal(Format(t2), ReadWorldState(database, "alice").UpdatedUtc);
        Assert.Equal(20, ReadProfile(database, "alice").CurrentHp);
        Assert.Equal(30f, ReadWorldState(database, "bob").PositionX);
        Assert.Equal(Format(t3), ReadWorldState(database, "bob").UpdatedUtc);
        Assert.Equal(30, ReadProfile(database, "bob").CurrentHp);
    }

    // 5. Vitals without a capture time (the default) are unguarded, as before: they overwrite even
    // when the stored world state is newer, and are stamped with the write time.
    [Fact]
    public void VitalsWithTheDefaultCaptureTime_AreUnguarded()
    {
        using PersistenceTestDatabase database = new();
        new SqlitePlayerProfileRepository(database.Schema).GetOrCreate(PlayerName);
        new SqlitePersistenceCheckpointWriter(database.Schema).Write(PersistenceCheckpoint.Empty with
        {
            PlayerWorldStates = [State(PlayerName, 1f, DateTime.UtcNow.AddDays(1))]
        });

        DateTime before = DateTime.UtcNow;
        new SqlitePersistenceCheckpointWriter(database.Schema).Write(PersistenceCheckpoint.Empty with
        {
            PlayerVitals = [new PlayerCheckpointVitals(PlayerName, PlayerInteractionMode.Combat, 40, 100)]
        });
        DateTime after = DateTime.UtcNow;

        ProfileRow profile = ReadProfile(database);
        Assert.Equal(nameof(PlayerInteractionMode.Combat), profile.InteractionMode);
        Assert.Equal(40, profile.CurrentHp);
        DateTime stamped = SqliteSchemaBootstrapper.ParseUtc(profile.UpdatedUtc);
        Assert.InRange(stamped, before.AddSeconds(-1), after.AddSeconds(1));
    }

    // An ordinary immediate profile save (XP, level, stats) queued before a capture but run after it
    // stamps a later time than the capture; it does not make the capture's vitals skip, so what is
    // persisted never depends on how far the write queue lags the tick. Only a newer world state (a
    // leave) does.
    [Fact]
    public void ProfileSavedAfterTheCapture_DoesNotBlockItsVitals()
    {
        using PersistenceTestDatabase database = new();
        SqlitePlayerProfileRepository profiles = new(database.Schema);
        PersistedPlayerProfile profile = profiles.GetOrCreate(PlayerName);
        DateTime capture = DateTime.UtcNow;
        new SqlitePersistenceCheckpointWriter(database.Schema).Write(PersistenceCheckpoint.Empty with
        {
            PlayerWorldStates = [State(PlayerName, 1f, capture.AddSeconds(-5))]
        });
        profiles.Save(profile with { Stats = profile.Stats with { Level = 2 } });
        SetUpdatedUtc(database, "player_profiles", PlayerName, Format(capture.AddSeconds(1)));

        new SqlitePersistenceCheckpointWriter(database.Schema).Write(PersistenceCheckpoint.Empty with
        {
            PlayerWorldStates = [State(PlayerName, 2f, capture)],
            PlayerVitals = [Vitals(PlayerName, 42, capture)]
        });

        Assert.Equal(42, ReadProfile(database).CurrentHp);
        Assert.Equal(2, database.Count($"SELECT level FROM player_profiles WHERE player_name = '{PlayerName}';"));
        Assert.Equal(2f, ReadWorldState(database).PositionX);
    }

    // 8. The leave queues the world state BEFORE the profile. A stale checkpoint (its own worker)
    // committing between the two finds the leave's world-state row already stored, so its vitals are
    // skipped, and the profile save that follows lands: the leave's HP stands. With the profile
    // queued first, the checkpoint would find no newer row and overwrite the leave's HP.
    [Fact]
    public void StaleCheckpointCommittedBetweenTheLeavesWrites_KeepsTheLeavesHp()
    {
        using PersistenceTestDatabase database = new();
        InterleavingProfileRepository profiles = new(new SqlitePlayerProfileRepository(database.Schema));
        using WorldSessionRuntime runtime = new(
            12,
            new Vector3(5f, 1.5f, 5f),
            new SceneEventHub(),
            playerProfiles: profiles,
            worldPersistence: new SqliteWorldPersistenceRepository(database.Schema),
            persistenceWrites: PersistenceWriteQueue.Inline());
        PlayerSessionId session = new(Session);
        runtime.AttachRemoteSession(session, PlayerName);
        runtime.RestorePersistedPlayerState(session, PlayerName);
        PersistenceCheckpoint captured = Assert.IsType<PersistenceCheckpoint>(runtime.CapturePersistenceCheckpoint(full: true));
        PlayerCheckpointVitals capturedVitals = Assert.Single(captured.PlayerVitals, vitals => vitals.PlayerName == PlayerName);
        PersistenceCheckpoint stale = WithStaleValues(captured);
        int leaveHp = runtime.Player.Combatant.Health.CurrentHealth - 13;
        int leaveMaxHp = runtime.Player.Combatant.Health.MaxHealth;
        runtime.Player.SetHealth(leaveHp, leaveMaxHp);
        Assert.NotEqual(StaleHealth, leaveHp);
        long hpBeforeTheLeave = ReadProfile(database).CurrentHp;

        bool ran = false;
        profiles.BeforeNextSave = () =>
        {
            // The leave's world-state row is committed, newer than the capture.
            WorldStateRow left = ReadWorldState(database);
            Assert.Equal(leaveHp, left.CurrentHp);
            Assert.True(string.CompareOrdinal(left.UpdatedUtc, Format(capturedVitals.CapturedUtc)) > 0);

            new SqlitePersistenceCheckpointWriter(database.Schema).Write(stale);

            // The stale vitals were skipped; the stale world-state row too.
            Assert.Equal(hpBeforeTheLeave, ReadProfile(database).CurrentHp);
            Assert.Equal(left, ReadWorldState(database));
            ran = true;
        };

        runtime.DetachRemoteSession(session);

        Assert.True(ran, "the interleaved checkpoint did not complete before the leave's profile save");
        ProfileRow profile = ReadProfile(database);
        Assert.Equal(leaveHp, profile.CurrentHp);
        Assert.Equal(leaveMaxHp, profile.MaxHp);
        Assert.Equal(leaveHp, ReadWorldState(database).CurrentHp);
    }

    // 9. Why the order matters, on the repositories: world state, stale checkpoint, profile keeps the
    // leave's HP; profile, stale checkpoint, world state (the order before the fix) loses it.
    [Theory]
    [InlineData(true, 20)]
    [InlineData(false, 50)]
    public void LeaveSaveOrder_DecidesWhetherAStaleCheckpointBetweenTheWritesWins(bool worldStateFirst, int expectedHp)
    {
        using PersistenceTestDatabase database = new();
        SqlitePlayerProfileRepository profiles = new(database.Schema);
        SqliteWorldPersistenceRepository world = new(database.Schema);
        PersistedPlayerProfile profile = profiles.GetOrCreate(PlayerName);
        DateTime capture = DateTime.UtcNow.AddSeconds(-1);
        DateTime leave = DateTime.UtcNow;
        PersistenceCheckpoint stale = PersistenceCheckpoint.Empty with
        {
            PlayerWorldStates = [State(PlayerName, 50f, capture)],
            PlayerVitals = [Vitals(PlayerName, 50, capture)]
        };
        Action saveWorldState = () => world.SavePlayerWorldState(State(PlayerName, 20f, leave));
        Action saveProfile = () => profiles.Save(profile with { Stats = profile.Stats with { CurrentHealth = 20 } });

        (worldStateFirst ? saveWorldState : saveProfile)();
        new SqlitePersistenceCheckpointWriter(database.Schema).Write(stale);
        (worldStateFirst ? saveProfile : saveWorldState)();

        Assert.Equal(expectedHp, ReadProfile(database).CurrentHp);
        Assert.Equal(20, ReadWorldState(database).CurrentHp);
        Assert.Equal(Format(leave), ReadWorldState(database).UpdatedUtc);
    }

    // 6. The fallback repository writer follows the same rule.
    [Fact]
    public void RepositoryWriter_FollowsTheSameGuard()
    {
        using PersistenceTestDatabase database = new();
        SqlitePlayerProfileRepository profiles = new(database.Schema);
        SqliteWorldPersistenceRepository world = new(database.Schema);
        RepositoryPersistenceCheckpointWriter writer = new(profiles, world);
        profiles.GetOrCreate(PlayerName);
        DateTime staleCapture = DateTime.UtcNow.AddSeconds(-1);
        world.SavePlayerWorldState(State(PlayerName, 5f, DateTime.UtcNow));
        WorldStateRow leftState = ReadWorldState(database);
        ProfileRow leftProfile = ReadProfile(database);

        // Captured before the saves above: skipped.
        writer.Write(PersistenceCheckpoint.Empty with
        {
            PlayerWorldStates = [State(PlayerName, 50f, staleCapture)],
            PlayerVitals = [Vitals(PlayerName, 50, staleCapture)]
        });
        Assert.Equal(leftState, ReadWorldState(database));
        Assert.Equal(leftProfile, ReadProfile(database));

        // Captured after them: written.
        DateTime freshCapture = DateTime.UtcNow.AddSeconds(1);
        writer.Write(PersistenceCheckpoint.Empty with
        {
            PlayerWorldStates = [State(PlayerName, 60f, freshCapture)],
            PlayerVitals = [Vitals(PlayerName, 60, freshCapture)]
        });
        Assert.Equal(60f, ReadWorldState(database).PositionX);
        Assert.Equal(60, ReadProfile(database).CurrentHp);

        // Default capture time: unguarded, even over a newer world state.
        SetUpdatedUtc(database, "player_world_state", PlayerName, Format(DateTime.UtcNow.AddDays(1)));
        writer.Write(PersistenceCheckpoint.Empty with
        {
            PlayerVitals = [new PlayerCheckpointVitals(PlayerName, PlayerInteractionMode.Combat, 70, 100)]
        });
        Assert.Equal(70, ReadProfile(database).CurrentHp);
    }

    // The fallback writer matches stored pieces by their own ObjectId, not (origin, in-memory index):
    // after restore compaction the index can differ from the stored piece_index.
    [Fact]
    public void RepositoryWriter_MatchesPiecesByObjectId_NotByPieceIndex()
    {
        using PersistenceTestDatabase database = new();
        SqlitePlayerProfileRepository profiles = new(database.Schema);
        SqliteWorldPersistenceRepository world = new(database.Schema);
        RepositoryPersistenceCheckpointWriter writer = new(profiles, world);
        profiles.GetOrCreate(PlayerName);
        world.SavePlacedCube(new PersistedPlacedCube(
            new ObjectId(8), PlayerName, new Vector3(2f, 1f, 2f), GeometryShapeKind.SolidCube,
            GeometryMaterialId.Dirt, 5, 5, DateTime.UtcNow, DateTime.UtcNow, new Vector3(2.5f, 1.5f, 2.5f)));
        PersistedCubeFracturePiece stored = new(
            new ObjectId(500), new ObjectId(8), 3, PlayerName, new Vector3(2f, 1f, 2f),
            new Vector3(2.5f, 1.1f, 2.5f), 0.2f, GeometryMaterialId.Dirt, DateTime.UtcNow, DateTime.UtcNow);
        world.SaveCubeFracturePiece(stored);

        // Compaction moved the piece to in-memory index 0; its ObjectId is unchanged.
        PersistedCubeFracturePiece moved = stored with { PieceIndex = 0, Center = new Vector3(2.6f, 1.1f, 2.6f) };
        writer.Write(PersistenceCheckpoint.Empty with { FracturePieces = [moved] });

        PersistedCubeFracturePiece loaded = Assert.Single(world.LoadCubeFracturePieces());
        Assert.Equal(new ObjectId(500), loaded.ObjectId);
        Assert.Equal(2.6f, loaded.Center.X, 3);
    }

    // 7. A legacy strftime updated_utc (3 fractional digits) against the 7-digit capture text: an
    // older capture is skipped, one within the same millisecond is skipped too (the conservative
    // direction), and a later one is written.
    [Fact]
    public void LegacyMillisecondUpdatedUtc_ComparesConservatively()
    {
        using PersistenceTestDatabase database = new();
        new SqlitePlayerProfileRepository(database.Schema).GetOrCreate(PlayerName);
        DateTime second = new(2026, 10, 28, 12, 0, 0, DateTimeKind.Utc);
        const string legacy = "2026-10-28T12:00:00.123Z";
        new SqlitePersistenceCheckpointWriter(database.Schema).Write(PersistenceCheckpoint.Empty with
        {
            PlayerWorldStates = [State(PlayerName, 1f, second)]
        });
        SetUpdatedUtc(database, "player_world_state", PlayerName, legacy);
        Assert.Equal(legacy, ReadWorldState(database).UpdatedUtc);

        foreach (long ticks in new long[] { 1_225_000, 1_234_567 })
        {
            DateTime capture = second.AddTicks(ticks);
            new SqlitePersistenceCheckpointWriter(database.Schema).Write(PersistenceCheckpoint.Empty with
            {
                PlayerWorldStates = [State(PlayerName, 2f, capture)],
                PlayerVitals = [Vitals(PlayerName, 2, capture)]
            });
            Assert.Equal(1f, ReadWorldState(database).PositionX);
            Assert.Equal(legacy, ReadWorldState(database).UpdatedUtc);
            Assert.NotEqual(2, ReadProfile(database).CurrentHp);
        }

        DateTime later = second.AddTicks(1_240_000);
        new SqlitePersistenceCheckpointWriter(database.Schema).Write(PersistenceCheckpoint.Empty with
        {
            PlayerWorldStates = [State(PlayerName, 3f, later)],
            PlayerVitals = [Vitals(PlayerName, 3, later)]
        });
        Assert.Equal(3f, ReadWorldState(database).PositionX);
        Assert.Equal("2026-10-28T12:00:00.1240000Z", ReadWorldState(database).UpdatedUtc);
        Assert.Equal(3, ReadProfile(database).CurrentHp);
    }

    // Runs BeforeNextSave (once) just before the next profile save: a checkpoint's worker committing
    // at that point of the leave's queued writes.
    private sealed class InterleavingProfileRepository(IPlayerProfileRepository inner) : IPlayerProfileRepository
    {
        public Action? BeforeNextSave { get; set; }

        public PersistedPlayerProfile GetOrCreate(string playerName) => inner.GetOrCreate(playerName);

        public PersistedPlayerProfile? TryGet(string playerName) => inner.TryGet(playerName);

        public void Save(PersistedPlayerProfile profile)
        {
            Action? before = BeforeNextSave;
            BeforeNextSave = null;
            before?.Invoke();
            inner.Save(profile);
        }
    }

    private static DedicatedServerHost CreateHost(PersistenceTestDatabase database) =>
        ServerBootstrap.CreateAuthoritativeHost(ServerBootstrap.CreatePersistenceBundle(database.DatabasePath, database.SchemaPath));

    // Joins through the protocol path: the host defers the join until the player's saved state is
    // loaded, then applies it on a tick (which saves the profile through the write queue).
    private static void Join(DedicatedServerHost host, string connectionId)
    {
        Assert.Empty(host.HandleClientMessage(connectionId, new ClientJoinRequestMessage(Session, PlayerName, Dream.Tests.Support.TestAccountTokens.For(PlayerName))));
        Flush(host);
        AdvanceOneTick(host);
        List<PendingConnectionReply> replies = [];
        host.DrainPendingJoinRepliesInto(replies);
        Assert.IsType<JoinAcceptedMessage>(Assert.Single(replies).Message);
        host.DrainPendingActionResults();
        Flush(host);
    }

    // The real leave path: releasing the connection detaches the session, which saves the world
    // state and the profile through the write queue.
    private static void Leave(DedicatedServerHost host, string connectionId)
    {
        host.ReleaseConnection(connectionId);
        AdvanceOneTick(host);
        Flush(host);
        Assert.DoesNotContain(host.Runtime!.CapturePersistenceCheckpoint(full: true)?.PlayerVitals ?? [], vitals => vitals.PlayerName == PlayerName);
    }

    private static void Flush(DedicatedServerHost host) =>
        Assert.True(host.Runtime!.PersistenceWrites?.Flush(TimeSpan.FromSeconds(30)) ?? true);

    private static void AdvanceOneTick(DedicatedServerHost host)
    {
        int executed = host.AdvanceTime(SimulationClock.FixedDeltaSeconds);
        if (executed == 0)
        {
            executed = host.AdvanceTime(SimulationClock.FixedDeltaSeconds * 0.01);
        }

        Assert.Equal(1, executed);
    }

    // The checkpoint with this player's rows replaced by values no capture holds; capture times kept.
    private static PersistenceCheckpoint WithStaleValues(PersistenceCheckpoint checkpoint) => checkpoint with
    {
        PlayerWorldStates = checkpoint.PlayerWorldStates
            .Select(state => state.PlayerName == PlayerName
                ? state with { Position = StalePosition, CurrentHealth = StaleHealth }
                : state)
            .ToArray(),
        PlayerVitals = checkpoint.PlayerVitals
            .Select(vitals => vitals.PlayerName == PlayerName
                ? vitals with { CurrentHealth = StaleHealth, MaxHealth = StaleMaxHealth }
                : vitals)
            .ToArray()
    };

    private static PersistedPlayerWorldState State(string playerName, float x, DateTime capturedUtc) =>
        new(playerName, new Vector3(x, 1.5f, 1f), Vector3.UnitZ, (int)x, 0, capturedUtc);

    private static PlayerCheckpointVitals Vitals(string playerName, int health, DateTime capturedUtc) =>
        new(playerName, PlayerInteractionMode.Building, health, 100) { CapturedUtc = capturedUtc };

    private static string Format(DateTime utc) => SqliteSchemaBootstrapper.FormatUtc(utc);

    private readonly record struct WorldStateRow(double PositionX, double PositionY, double PositionZ, long CurrentHp, string UpdatedUtc);

    private readonly record struct ProfileRow(string InteractionMode, long CurrentHp, long MaxHp, string UpdatedUtc);

    private static WorldStateRow ReadWorldState(PersistenceTestDatabase database, string playerName = PlayerName)
    {
        using SqliteConnection connection = database.Schema.CreateOpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT position_x, position_y, position_z, current_hp, updated_utc
            FROM player_world_state WHERE player_name = $playerName;
            """;
        command.Parameters.AddWithValue("$playerName", playerName);
        using SqliteDataReader reader = command.ExecuteReader();
        Assert.True(reader.Read(), $"no player_world_state row for {playerName}");
        return new WorldStateRow(reader.GetDouble(0), reader.GetDouble(1), reader.GetDouble(2), reader.GetInt64(3), reader.GetString(4));
    }

    private static ProfileRow ReadProfile(PersistenceTestDatabase database, string playerName = PlayerName)
    {
        using SqliteConnection connection = database.Schema.CreateOpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT interaction_mode, current_hp, max_hp, updated_utc
            FROM player_profiles WHERE player_name = $playerName;
            """;
        command.Parameters.AddWithValue("$playerName", playerName);
        using SqliteDataReader reader = command.ExecuteReader();
        Assert.True(reader.Read(), $"no player_profiles row for {playerName}");
        return new ProfileRow(reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetString(3));
    }

    private static void SetUpdatedUtc(PersistenceTestDatabase database, string table, string playerName, string updatedUtc)
    {
        using SqliteConnection connection = database.Schema.CreateOpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"UPDATE {table} SET updated_utc = $updatedUtc WHERE player_name = $playerName;";
        command.Parameters.AddWithValue("$updatedUtc", updatedUtc);
        command.Parameters.AddWithValue("$playerName", playerName);
        Assert.Equal(1, command.ExecuteNonQuery());
    }
}
