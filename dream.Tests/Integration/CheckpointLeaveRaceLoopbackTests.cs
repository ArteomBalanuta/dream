using System.Diagnostics;
using Microsoft.Data.Sqlite;
using OpenTK.Mathematics;
using Xunit;
using Xunit.Abstractions;
using Dream.Server;
using Dream.Server.Persistence;
using Dream.Simulation.Players;
using Dream.Simulation.World;
using static Dream.Tests.Integration.PersistentWorldSeedDeterminismIntegrationTests;

namespace Dream.Tests.Integration;

// Tester, persistence wave A (A4) end to end: the production persistent host, its periodic
// checkpoint (captured on the tick thread, written by BackgroundCheckpointWriter) and its leave path
// (written by the write queue), with two wire clients. Client A changes position and mode right
// after a checkpoint is captured and then leaves. A test connection holds the database's write lock
// from just before the capture until after the leave was queued, so the stale checkpoint and the
// leave's writes race for the lock when it is released. Whichever lands first, A's row must end
// with the leave's values; the order is read back from audit triggers and reported. After A
// rejoins, the next checkpoint writes A's rows again.
public sealed class CheckpointLeaveRaceLoopbackTests(ITestOutputHelper output)
{
    private const string SessionA = "race-a";
    private const string SessionB = "race-b";
    private const string PlayerA = "Name race-a";
    private const string PlayerB = "Name race-b";

    [Fact]
    public async Task LeaveRightAfterACapture_KeepsTheLeavesState_AndTheNextCheckpointAfterARejoinWrites()
    {
        using TempDatabase database = new();
        ServerPersistenceBundle bundle = database.OpenBundle();
        InstallAuditTriggers(database);
        DedicatedServerHost host = ServerBootstrap.CreateAuthoritativeHost(bundle);
        await using WireLoopbackServer server = await WireLoopbackServer.StartAsync(host);
        BackgroundCheckpointWriter checkpoints = Assert.IsType<BackgroundCheckpointWriter>(host.CheckpointWrites);

        WireLoopbackClient a = await server.ConnectAndJoinAsync(SessionA);
        await using WireLoopbackClient b = await server.ConnectAndJoinAsync(SessionB);
        await a.SendCommandAsync(PlayerInteractionMode.Building, PlayerMoveIntent.None, Vector3.UnitZ, attack: false);
        await TickAsync(server, 4);

        // Let one ordinary checkpoint through first, so both players have rows.
        await TickUntilCheckpointAsync(server, checkpoints);
        Assert.True(checkpoints.Flush(TimeSpan.FromSeconds(30)));
        Assert.True(host.Runtime!.PersistenceWrites?.Flush(TimeSpan.FromSeconds(30)) ?? true);
        Assert.Equal("Building", ReadProfile(database, PlayerA).Mode);

        // Hold the write lock across the next capture and A's leave.
        long auditBefore;
        ActorSnapshot leaving;
        Stopwatch held = Stopwatch.StartNew();
        using (SqliteConnection locker = OpenPlain(database))
        {
            Execute(locker, "BEGIN IMMEDIATE;");
            auditBefore = (long)Scalar(locker, "SELECT COALESCE(MAX(id), 0) FROM race_audit;")!;
            ActorSnapshot atCapture = Actor(server, SessionA);
            await TickUntilCheckpointAsync(server, checkpoints);
            output.WriteLine($"captured at server tick {server.Runtime.CurrentServerTick}, A at {atCapture.Position} hp {atCapture.CurrentHealth} {atCapture.InteractionMode}");

            // A moves and switches to Combat after the capture, then stops and leaves.
            for (int i = 0; i < 12; i++)
            {
                await a.SendCommandAsync(PlayerInteractionMode.Combat, new PlayerMoveIntent(1f, 0f), Vector3.UnitZ, attack: false);
                await TickAsync(server, 1);
            }

            await a.SendCommandAsync(PlayerInteractionMode.Combat, PlayerMoveIntent.None, Vector3.UnitZ, attack: false);
            await TickAsync(server, 3);
            leaving = Actor(server, SessionA);
            Assert.Equal(PlayerInteractionMode.Combat, leaving.InteractionMode);
            // 12 ticks is a short move now that the motor ramps: at 18 m/s^2 the run cruise is
            // 15.3 ticks away, so these 12 never reach it and cover about 0.34 m, not the 1.13 m
            // a flat 6 m/s used to give. The assert only has to prove the move command landed, so
            // it keeps the tick count - more ticks under the held write lock would move the race
            // this test is about - and asks for a quarter metre instead.
            Assert.True((leaving.Position - atCapture.Position).Length > 0.25f, $"A did not move: {atCapture.Position} -> {leaving.Position}");
            await server.DisconnectAsync(a);
            await TickAsync(server, 2);
            await Task.Delay(150);
            Execute(locker, "COMMIT;");
        }

        held.Stop();
        output.WriteLine($"write lock held {held.ElapsedMilliseconds} ms");
        Assert.True(held.Elapsed < TimeSpan.FromSeconds(4), "the lock outlasted busy_timeout; the race was not exercised");
        Assert.True(checkpoints.Flush(TimeSpan.FromSeconds(30)));
        Assert.True(host.Runtime.PersistenceWrites?.Flush(TimeSpan.FromSeconds(30)) ?? true);
        Assert.Equal(0, checkpoints.FailedWrites);

        List<AuditRow> raced = ReadAudit(database, auditBefore);
        foreach (AuditRow row in raced)
        {
            output.WriteLine($"  audit {row}");
        }

        WorldStateRow state = ReadWorldState(database, PlayerA);
        ProfileRow profile = ReadProfile(database, PlayerA);
        Assert.Equal(leaving.Position.X, state.X, 3);
        Assert.Equal(leaving.Position.Z, state.Z, 3);
        Assert.Equal(leaving.CurrentHealth, state.Hp);
        Assert.Equal("Combat", profile.Mode);
        Assert.Equal(leaving.CurrentHealth, profile.Hp);
        // The stale checkpoint did reach the database: B's row was written from it.
        Assert.Contains(raced, row => row.Table == "player_world_state" && row.Player == PlayerB);
        int leaveIndex = raced.FindIndex(row => row.Table == "player_world_state" && row.Player == PlayerA);
        int staleIndex = raced.FindIndex(row => row.Table == "player_world_state" && row.Player == PlayerB);
        output.WriteLine(leaveIndex < staleIndex
            ? "order: the leave won the lock; the stale checkpoint was written after it (guard exercised)"
            : "order: the stale checkpoint was written first; the leave overwrote it (guard not needed)");

        // A rejoins; the next checkpoint writes A's rows again with its newer capture time.
        await using WireLoopbackClient again = await server.ConnectAndJoinAsync(SessionA);
        await TickAsync(server, 4);
        long auditRejoin = (long)Scalar(database, "SELECT COALESCE(MAX(id), 0) FROM race_audit;")!;
        await TickUntilCheckpointAsync(server, checkpoints);
        ActorSnapshot rejoined = Actor(server, SessionA);
        Assert.True(checkpoints.Flush(TimeSpan.FromSeconds(30)));
        Assert.True(host.Runtime.PersistenceWrites?.Flush(TimeSpan.FromSeconds(30)) ?? true);

        List<AuditRow> after = ReadAudit(database, auditRejoin);
        Assert.Contains(after, row => row.Table == "player_world_state" && row.Player == PlayerA);
        Assert.Contains(after, row => row.Table == "player_profiles" && row.Player == PlayerA);
        WorldStateRow rewritten = ReadWorldState(database, PlayerA);
        Assert.True(string.CompareOrdinal(rewritten.UpdatedUtc, state.UpdatedUtc) > 0, $"{rewritten.UpdatedUtc} is not newer than {state.UpdatedUtc}");
        Assert.Equal(rejoined.Position.X, rewritten.X, 3);
        Assert.Equal(rejoined.Position.Z, rewritten.Z, 3);
    }

    private static async Task TickUntilCheckpointAsync(WireLoopbackServer server, BackgroundCheckpointWriter checkpoints)
    {
        long before = checkpoints.EnqueuedCheckpoints;
        for (int i = 0; i < 400 && checkpoints.EnqueuedCheckpoints == before; i++)
        {
            await TickAsync(server, 1);
        }

        Assert.Equal(before + 1, checkpoints.EnqueuedCheckpoints);
    }

    private static async Task TickAsync(WireLoopbackServer server, int ticks)
    {
        for (int i = 0; i < ticks; i++)
        {
            await server.TickAsync();
            Assert.True(await server.FlushAllAsync());
        }
    }

    private static ActorSnapshot Actor(WireLoopbackServer server, string session) =>
        Assert.Single(server.Runtime.CaptureActorSnapshots(), actor => actor.SessionId == session);

    private static void InstallAuditTriggers(TempDatabase database)
    {
        using SqliteConnection connection = OpenPlain(database);
        Execute(connection, """
            CREATE TABLE race_audit (id INTEGER PRIMARY KEY AUTOINCREMENT, tbl TEXT NOT NULL, player TEXT NOT NULL, updated_utc TEXT, hp INTEGER, x REAL, mode TEXT);
            CREATE TRIGGER race_audit_pws_insert AFTER INSERT ON player_world_state BEGIN
                INSERT INTO race_audit (tbl, player, updated_utc, hp, x) VALUES ('player_world_state', NEW.player_name, NEW.updated_utc, NEW.current_hp, NEW.position_x);
            END;
            CREATE TRIGGER race_audit_pws_update AFTER UPDATE ON player_world_state BEGIN
                INSERT INTO race_audit (tbl, player, updated_utc, hp, x) VALUES ('player_world_state', NEW.player_name, NEW.updated_utc, NEW.current_hp, NEW.position_x);
            END;
            CREATE TRIGGER race_audit_profile_update AFTER UPDATE ON player_profiles BEGIN
                INSERT INTO race_audit (tbl, player, updated_utc, hp, mode) VALUES ('player_profiles', NEW.player_name, NEW.updated_utc, NEW.current_hp, NEW.interaction_mode);
            END;
            """);
    }

    private sealed record AuditRow(long Id, string Table, string Player, string? UpdatedUtc, long? Hp, double? X, string? Mode);

    private readonly record struct WorldStateRow(double X, double Z, long Hp, string UpdatedUtc);

    private readonly record struct ProfileRow(string Mode, long Hp, string UpdatedUtc);

    private static List<AuditRow> ReadAudit(TempDatabase database, long afterId)
    {
        using SqliteConnection connection = OpenPlain(database);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT id, tbl, player, updated_utc, hp, x, mode FROM race_audit WHERE id > $id ORDER BY id;";
        command.Parameters.AddWithValue("$id", afterId);
        using SqliteDataReader reader = command.ExecuteReader();
        List<AuditRow> rows = [];
        while (reader.Read())
        {
            rows.Add(new AuditRow(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetInt64(4),
                reader.IsDBNull(5) ? null : reader.GetDouble(5),
                reader.IsDBNull(6) ? null : reader.GetString(6)));
        }

        return rows;
    }

    private static WorldStateRow ReadWorldState(TempDatabase database, string player)
    {
        using SqliteConnection connection = OpenPlain(database);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT position_x, position_z, current_hp, updated_utc FROM player_world_state WHERE player_name = $p;";
        command.Parameters.AddWithValue("$p", player);
        using SqliteDataReader reader = command.ExecuteReader();
        Assert.True(reader.Read(), $"no player_world_state row for {player}");
        return new WorldStateRow(reader.GetDouble(0), reader.GetDouble(1), reader.GetInt64(2), reader.GetString(3));
    }

    private static ProfileRow ReadProfile(TempDatabase database, string player)
    {
        using SqliteConnection connection = OpenPlain(database);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT interaction_mode, current_hp, updated_utc FROM player_profiles WHERE player_name = $p;";
        command.Parameters.AddWithValue("$p", player);
        using SqliteDataReader reader = command.ExecuteReader();
        Assert.True(reader.Read(), $"no player_profiles row for {player}");
        return new ProfileRow(reader.GetString(0), reader.GetInt64(1), reader.GetString(2));
    }

    private static object? Scalar(TempDatabase database, string sql)
    {
        using SqliteConnection connection = OpenPlain(database);
        return Scalar(connection, sql);
    }

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static SqliteConnection OpenPlain(TempDatabase database)
    {
        SqliteConnection connection = new(new SqliteConnectionStringBuilder { DataSource = database.DatabasePath, Pooling = false }.ToString());
        connection.Open();
        using SqliteCommand pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA busy_timeout=5000;";
        pragma.ExecuteNonQuery();
        return connection;
    }
}
