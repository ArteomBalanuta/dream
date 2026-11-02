using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using OpenTK.Mathematics;
using Xunit;
using Xunit.Abstractions;
using Dream.Client.Replication;
using Dream.Client.World;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Server;
using Dream.Server.Persistence;
using Dream.Server.Worlds;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using Dream.Tests.Server.Persistence.Migrations;

namespace Dream.Tests.Integration;

// Tester, persistence wave B: end-to-end checks that reach the world through the production
// bootstrap (ServerBootstrap.CreatePersistenceBundle with ServerStartOptions, as --server --world
// does) and over a real wire. (a) A world selected by name ("foo", size 20) is created, served to a
// client built for a 56 floor, takes a lamp, and a restart restores the lamp in foo only. (b) Two
// hosts on the same database file, one per world, run 600 ticks at once with checkpoints and
// joins: no write is lost, no write fails, and each world keeps exactly its own rows. (c) The
// protocol numbers of wave B: the descriptor costs about 130 B once at join, snapshots do not grow.
public sealed class WaveBTesterWorldsLoopbackTests(ITestOutputHelper output)
{
    private const string Session = "foo-player";

    [Fact]
    public async Task ServerStartedOnWorldFoo_Size20_ServesA20WideMap_ToAFloor56Client_AndRestoresItsLampOnlyInFoo()
    {
        using MigrationTestDatabase database = new();
        ServerStartOptions start = new(WorldSelector: "foo", NewWorldSize: 20);
        List<string> log = [];

        Vector3 lampCell;
        await using (WireLoopbackServer server = await WireLoopbackServer.StartAsync(ServerBootstrap.CreateAuthoritativeHost(
            ServerBootstrap.CreatePersistenceBundle(database.DatabasePath, MigrationTestDatabase.InitScriptPath, log: log.Add, start: start))))
        {
            output.WriteLine(string.Join(" / ", log));
            Assert.Contains(log, line => line.Contains("World 'foo' (id 2) did not exist and was created (size 20).", StringComparison.Ordinal));
            Assert.Equal(20, server.Runtime.Map.Width);
            Assert.Equal(20, server.Runtime.Map.Depth);
            await using WireLoopbackClient wire = await server.ConnectAndJoinAsync(Session);
            JoinAcceptedMessage join = wire.Frames.Select(frame => frame.Decoded.Message).OfType<JoinAcceptedMessage>().First();
            Assert.Equal(new WorldDescriptorWire(2, "foo", 20, 8, 20, "generated", "default", 1), join.World);

            using ClientWorldSession client = new(WorldDefaults.StartupFloorSize, new Vector3(5f, 1.5f, 5f), new SceneEventHub());
            Assert.Equal(56, client.Map.Width);
            ClientJoinAcceptedApplier.ApplyToRuntime(client, join);
            Assert.Equal(20, client.Map.Width);
            Assert.Equal(20, client.Map.Depth);

            lampCell = await CrossWaveT2PersistenceLoopbackTests.PlaceLampAsync(server, wire);
            await server.DisconnectAsync(wire);
            await server.TickAsync();
            Assert.True(await server.FlushAllAsync());
            Assert.True(server.Runtime.PersistenceWrites?.Flush(TimeSpan.FromSeconds(30)) ?? true);
        }

        SqliteConnection.ClearAllPools();
        Assert.Equal(1L, WorldBundles.Count(database, "placed_cube_instances", new WorldId(2)));
        Assert.Equal(0L, WorldBundles.Count(database, "placed_cube_instances", WorldId.Default));
        Assert.Equal(2L, database.Scalar("SELECT COUNT(*) FROM worlds;"));

        // Restart on "foo": it is opened (not created again), the size argument is ignored for an existing world.
        log.Clear();
        using (ServerPersistenceBundle bundle = ServerBootstrap.CreatePersistenceBundle(
            database.DatabasePath, MigrationTestDatabase.InitScriptPath, log: log.Add, start: new ServerStartOptions("FOO", 33)))
        using (DedicatedServerHost host = ServerBootstrap.CreateAuthoritativeHost(bundle))
        {
            host.Runtime!.WriteCheckpointOnDispose = false;
            Assert.Contains(log, line => line.Contains("World 'foo' (id 2) opened.", StringComparison.Ordinal));
            Assert.Equal(20, host.Runtime.Map.Width);
            Assert.Equal(
                GeometryMaterialId.Lamp,
                host.Runtime.Map.TryGetCubeAtCell((int)lampCell.X, (int)lampCell.Y, (int)lampCell.Z)?.MaterialId);
        }

        // World 1 (default selector) is its own 56 map without the lamp.
        SqliteConnection.ClearAllPools();
        using (ServerPersistenceBundle bundle = ServerBootstrap.CreatePersistenceBundle(
            database.DatabasePath, MigrationTestDatabase.InitScriptPath, start: ServerStartOptions.Default))
        using (DedicatedServerHost host = ServerBootstrap.CreateAuthoritativeHost(bundle))
        {
            host.Runtime!.WriteCheckpointOnDispose = false;
            Assert.Equal(WorldId.Default, bundle.WorldId);
            Assert.Equal(56, host.Runtime.Map.Width);
            Assert.NotEqual(
                GeometryMaterialId.Lamp,
                host.Runtime.Map.TryGetCubeAtCell((int)lampCell.X, (int)lampCell.Y, (int)lampCell.Z)?.MaterialId ?? GeometryMaterialId.Stone);
        }
    }

    [Fact]
    public Task TwoHostsOnTheSameDatabaseFile_World1AndWorld2_Run600Ticks_WithCheckpointsAndJoins_AndStayIntact() =>
        RunTwoHostsAsync(checkpointEvery: 25, fullCheckpointInterval: null, lockHoldMilliseconds: 0);

    // Harder: a full checkpoint every 5 ticks on both hosts, and a third connection (another process's
    // stand-in) holds the database write lock for 6.5 s, longer than busy_timeout (5 s). The queued writes
    // of both worlds must back off and retry, not be lost.
    [Fact]
    public Task TwoHostsOnTheSameDatabaseFile_AWriteLockHeldPastBusyTimeout_LosesNoWrites()
    {
        return RunTwoHostsAsync(checkpointEvery: 5, fullCheckpointInterval: 1, lockHoldMilliseconds: 6500);
    }

    private async Task RunTwoHostsAsync(int checkpointEvery, int? fullCheckpointInterval, int lockHoldMilliseconds)
    {
        using MigrationTestDatabase database = new();
        database.Schema.EnsureSchema();
        WorldRecord second = new Dream.Server.Persistence.Worlds.SqliteWorldCatalog(database.Schema)
            .Create(new WorldCreateOptions("second", 40, 99));

        // Two bundles from two separate schema bootstrappers: the way two processes would share the file.
        SqliteSchemaBootstrapper schemaOne = new(database.DatabasePath, MigrationTestDatabase.InitScriptPath);
        SqliteSchemaBootstrapper schemaTwo = new(database.DatabasePath, MigrationTestDatabase.InitScriptPath);
        schemaOne.EnsureSchema();
        schemaTwo.EnsureSchema();
        ServerPersistenceBundle bundleOne = WorldBundles.Bind(schemaOne, new Dream.Server.Persistence.Worlds.SqliteWorldCatalog(schemaOne).TryGet(WorldId.Default)!);
        ServerPersistenceBundle bundleTwo = WorldBundles.Bind(schemaTwo, second);

        const int Ticks = 600;
        long writesOne;
        long writesTwo;
        long retries;
        long failed;
        List<string> sessionsOne = [];
        List<string> sessionsTwo = [];
        await using (WireLoopbackServer one = await WireLoopbackServer.StartAsync(ServerBootstrap.CreateAuthoritativeHost(bundleOne)))
        await using (WireLoopbackServer two = await WireLoopbackServer.StartAsync(ServerBootstrap.CreateAuthoritativeHost(bundleTwo)))
        {
            Assert.Equal(56, one.Runtime.Map.Width);
            Assert.Equal(40, two.Runtime.Map.Width);
            if (fullCheckpointInterval is int interval)
            {
                one.Runtime.FullCheckpointInterval = interval;
                two.Runtime.FullCheckpointInterval = interval;
            }

            Task locker = Task.CompletedTask;
            if (lockHoldMilliseconds > 0)
            {
                using ManualResetEventSlim locked = new();
                locker = Task.Run(() =>
                {
                    using SqliteConnection holder = database.Open();
                    MigrationTestDatabase.Execute(holder, "BEGIN IMMEDIATE;");
                    locked.Set();
                    Thread.Sleep(lockHoldMilliseconds);
                    // On a loaded machine the first write can start so late that its busy_timeout outlasts
                    // the fixed hold, and then nothing retried. Hold on until one write has been refused.
                    DateTime deadline = DateTime.UtcNow.AddSeconds(30);
                    while (one.Runtime.PersistenceWrites!.LockRetries + two.Runtime.PersistenceWrites!.LockRetries == 0 && DateTime.UtcNow < deadline)
                    {
                        Thread.Sleep(50);
                    }

                    MigrationTestDatabase.Execute(holder, "COMMIT;");
                });
                Assert.True(locked.Wait(TimeSpan.FromSeconds(10)));
            }

            // The same player names join both worlds (player_profiles is shared), with different timing.
            Task runOne = Task.Run(() => Drive(one, "alpha", Ticks, joinEvery: 37, checkpointEvery, sessionsOne));
            Task runTwo = Task.Run(() => Drive(two, "alpha", Ticks, joinEvery: 41, checkpointEvery, sessionsTwo));
            await Task.WhenAll(runOne, runTwo);
            await locker;

            Assert.True(one.Runtime.PersistenceWrites!.Flush(TimeSpan.FromSeconds(60)));
            Assert.True(two.Runtime.PersistenceWrites!.Flush(TimeSpan.FromSeconds(60)));
            writesOne = one.Runtime.PersistenceWrites.CompletedWrites;
            writesTwo = two.Runtime.PersistenceWrites.CompletedWrites;
            failed = one.Runtime.PersistenceWrites.FailedWrites + two.Runtime.PersistenceWrites.FailedWrites;
            retries = one.Runtime.PersistenceWrites.LockRetries + two.Runtime.PersistenceWrites.LockRetries;
            output.WriteLine($"checkpoints: world 1 {one.Runtime.FullCheckpointCaptures} full + {one.Runtime.IncrementalCheckpointCaptures} incremental, world 2 {two.Runtime.FullCheckpointCaptures} full + {two.Runtime.IncrementalCheckpointCaptures} incremental");
            Assert.True(one.Runtime.FullCheckpointCaptures + one.Runtime.IncrementalCheckpointCaptures >= 5);
            Assert.True(two.Runtime.FullCheckpointCaptures + two.Runtime.IncrementalCheckpointCaptures >= 5);
        }

        output.WriteLine($"world 1: {writesOne} writes; world 2: {writesTwo} writes; lock retries {retries}; failed writes {failed}");
        Assert.Equal(0L, failed);
        if (lockHoldMilliseconds > 5000)
        {
            Assert.True(retries > 0, "the held write lock should have forced at least one retry");
        }

        Assert.True(writesOne > 0 && writesTwo > 0);

        SqliteConnection.ClearAllPools();
        Assert.Equal("ok", database.Scalar("PRAGMA integrity_check;"));
        using (SqliteConnection connection = database.Open())
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "PRAGMA foreign_key_check;";
            using SqliteDataReader reader = command.ExecuteReader();
            Assert.False(reader.Read(), "foreign_key_check must be clean");
        }

        // Each world keeps its own placed cubes (every joiner placed one lamp-sized block), its own players and clock;
        // nothing of world 2 leaked into world 1 and the other way round.
        foreach (WorldId world in new[] { WorldId.Default, new WorldId(2) })
        {
            List<string> sessions = world == WorldId.Default ? sessionsOne : sessionsTwo;
            long players = WorldBundles.Count(database, "player_world_state", world);
            long cubes = WorldBundles.Count(database, "placed_cube_instances", world) + WorldBundles.Count(database, "cube_fracture_pieces", world);
            output.WriteLine($"world {world}: {sessions.Count} joins, {players} player rows, {cubes} cube rows, {WorldBundles.Count(database, "item_instances", world)} items");
            Assert.Equal(sessions.Distinct().Count(), players);
            Assert.True(cubes >= 1, $"world {world} lost its placed cubes");
            Assert.Equal(
                WorldBundles.Count(database, "item_instances", world),
                Convert.ToInt64(database.Scalar($"SELECT COUNT(*) FROM item_instances WHERE world_id = {world} AND owner_player_name IS NOT NULL;")!, CultureInfo.InvariantCulture));
            Assert.Equal(0L, Convert.ToInt64(database.Scalar($"SELECT COUNT(*) FROM player_inventory_slots s LEFT JOIN item_instances i ON i.world_id = s.world_id AND i.item_instance_id = s.item_instance_id WHERE s.world_id = {world} AND i.item_instance_id IS NULL;")!, CultureInfo.InvariantCulture));
            long clock = Convert.ToInt64(database.Scalar($"SELECT world_time_ticks FROM worlds WHERE world_id = {world};")!, CultureInfo.InvariantCulture);
            Assert.True(clock >= 500, $"world {world} clock {clock} did not advance with its ticks");
        }

        // Cube rows never cross: every cube's cell is inside its own world's bounds.
        Assert.Equal(0L, database.Scalar("SELECT COUNT(*) FROM placed_cube_instances WHERE world_id = 2 AND (cell_x >= 40 OR cell_z >= 40);"));
        Assert.Equal(2L, database.Scalar("SELECT COUNT(*) FROM worlds;"));
        Assert.Equal("99", database.Scalar("SELECT seed FROM worlds WHERE world_id = 2;"));
    }

    // Plays one world: a resident player moves around, and a fresh player joins every joinEvery ticks,
    // places a block, and leaves two ticks later. A checkpoint is forced every 25 ticks.
    private static async Task Drive(WireLoopbackServer server, string resident, int ticks, int joinEvery, int checkpointEvery, List<string> joined)
    {
        await using WireLoopbackClient home = await server.ConnectAndJoinAsync(resident);
        joined.Add(resident);
        WireLoopbackClient? guest = null;
        int leaveAt = -1;
        int guestNumber = 0;
        for (int tick = 1; tick <= ticks; tick++)
        {
            Vector3 facing = new(MathF.Cos(tick * 0.1f), -0.3f, MathF.Sin(tick * 0.1f));
            await home.SendCommandAsync(PlayerInteractionMode.Building, tick % 10 < 5 ? PlayerMoveIntent.None : PlayerMoveIntent.None, facing.Normalized(), attack: tick % 20 == 0);
            if (tick % joinEvery == 0 && guest == null)
            {
                string name = $"guest-{guestNumber++}";
                guest = await server.ConnectAndJoinAsync(name);
                joined.Add(name);
                await guest.SendActionAsync(new BuildMaterialSelectIntent(GeometryMaterialId.Lamp));
                await guest.SendCommandAsync(PlayerInteractionMode.Building, PlayerMoveIntent.None, new Vector3(1f, -0.4f, 0f).Normalized(), attack: true);
                leaveAt = tick + 2;
            }

            await server.TickAsync();
            Assert.True(await server.FlushAllAsync());
            if (tick % checkpointEvery == 0)
            {
                server.Runtime.PersistCheckpoint();
            }

            if (guest != null && tick >= leaveAt)
            {
                await server.DisconnectAsync(guest);
                await guest.DisposeAsync();
                guest = null;
            }
        }

        if (guest != null)
        {
            await server.DisconnectAsync(guest);
            await guest.DisposeAsync();
        }

        await server.DisconnectAsync(home);
        await server.TickAsync();
        Assert.True(await server.FlushAllAsync());
    }

    [Fact]
    public async Task JoinAccepted_GrowsByTheDescriptor_Only_AndTheSnapshotBytesAreUnchanged()
    {
        // Join-accepted: the real frame with and without the descriptor.
        using MigrationTestDatabase database = new();
        WorldRecord world = WorldBundles.OpenCatalog(database).Create(new WorldCreateOptions("bytes", 56, 5));
        await using WireLoopbackServer server = await WireLoopbackServer.StartAsync(
            ServerBootstrap.CreateAuthoritativeHost(WorldBundles.Bind(database.Schema, world)));
        await using WireLoopbackClient wire = await server.ConnectAndJoinAsync("bytes");
        WireFrame frame = wire.Frames.First(f => f.Decoded.Message is JoinAcceptedMessage);
        JoinAcceptedMessage join = (JoinAcceptedMessage)frame.Decoded.Message;
        Assert.NotNull(join.World);

        TransportJsonCodec codec = new();
        TransportEnvelope envelope = frame.Decoded.Envelope;
        int with = codec.Encode(envelope, join).Length;
        int without = codec.Encode(envelope, join with { World = null }).Length;
        string descriptorJson = JsonSerializer.Serialize(join.World, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        output.WriteLine($"join-accepted {without} B -> {with} B (+{with - without}); descriptor json {descriptorJson}");
        Assert.InRange(with - without, 100, 160);
        Assert.Equal(frame.Payload.Length, with);
        // The seed is server-only: not in the join frame.
        Assert.DoesNotContain("seed", Encoding.UTF8.GetString(frame.Payload), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("5\"", descriptorJson, StringComparison.Ordinal);

        // Snapshots: the 56 world built through the descriptor constructor gives the wave T1 bytes.
        WorldSessionRuntime runtime = new(WorldDescriptor.Default, new Vector3(5f, 1.5f, 5f), new SceneEventHub());
        runtime.AttachRemoteSession(new PlayerSessionId("session-a"), "session-a");
        runtime.AttachRemoteSession(new PlayerSessionId("session-b"), "session-b");
        long total = 0;
        for (ulong tick = 1; tick <= 200; tick++)
        {
            runtime.AdvanceAuthoritativeTick(tick, SimulationClock.FixedDeltaSeconds, default(PlayerCommandFrame), PlayerSimulationTuning.AuthoritativeDefault);
            ServerSnapshotMessage message = new(runtime.ExportServerSnapshotEnvelope());
            total += codec.Encode(new TransportEnvelope(TransportEnvelope.CurrentProtocolVersion, message.MessageKind, tick), message).Length;
        }

        runtime.Dispose();
        output.WriteLine($"snapshots: {total} B over 200 ticks");
        // 58,107 B at protocol v9 plus one digit per frame is 58,307 B; the wolf bite change (a chasing wolf stops
        // in bite range and bites) moved it to 58,827 B; the last fix loop (far wolf spawn, join protection: the
        // wolf stands still and nobody is bitten in 200 ticks) moves it to 58,140 B.
        // The lowered experience curve (100 + level * 50, M2) shortens experienceToNextLevel at level 1 from 1023 to 150,
        // one byte per progression it carries (8 in all): 58,132 B.
        Assert.Equal(58_132L, total);
    }

    [Fact]
    public void ProtocolV6Envelope_IsRefused_AndV7IsAccepted()
    {
        TransportJsonCodec codec = new();
        ClientHelloMessage hello = new("tester");
        byte[] v7 = codec.Encode(new TransportEnvelope(TransportEnvelope.CurrentProtocolVersion, hello.MessageKind, 1), hello);
        Assert.Equal(hello.MessageKind, codec.DecodeFromClient(v7).Envelope.MessageKind);

        byte[] v6 = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(v7).Replace("\"protocolVersion\":19", "\"protocolVersion\":6", StringComparison.Ordinal));
        Assert.NotEqual(v7, v6);
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => codec.DecodeFromClient(v6));
        Assert.Equal("Unsupported protocol version 6. Supported version: 19.", error.Message);
    }
}
