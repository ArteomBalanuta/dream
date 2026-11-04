using System.Globalization;
using Microsoft.Data.Sqlite;
using OpenTK.Mathematics;
using Xunit;
using Xunit.Abstractions;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Server;
using Dream.Server.Persistence;
using Dream.Simulation.Inventory;
using Dream.Simulation.Persistence;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Tests.Support;
using Dream.Simulation.World.Geometry;
using static Dream.Tests.Integration.PersistentWorldSeedDeterminismIntegrationTests;

namespace Dream.Tests.Integration;

// Tester, wave T2 x persistence wave A: the production persistent host (CreateAuthoritativeHost over
// a migrated SQLite world) and a real wire client. A player whose inventory predates the torch
// receives it through the starter top-up, equips it, places a lamp at night while the night wolf
// spawns; a restart brings back the lamp, the equipped torch and both wolves with no third, rejoins
// never duplicate the torch, and a restart at dawn leaves one wolf and one enemy row.
public sealed class CrossWaveT2PersistenceLoopbackTests(ITestOutputHelper output)
{
    private const string Session = "xw-t2";
    private const string PlayerName = "Name xw-t2";

    [Fact]
    public async Task LampTorchAndNightWolves_SurviveRestarts_AndDawnLeavesOneWolf()
    {
        using TempDatabase database = new();

        // Phase 0: a first join seeds the starter inventory; the torch rows and the starter grant
        // record are then removed so the backpack looks like one written before wave T2 (4 items,
        // hammer equipped, no recorded kit version).
        await using (WireLoopbackServer server = await StartAsync(database))
        {
            await using WireLoopbackClient client = await server.ConnectAndJoinAsync(Session);
            await TickAsync(server, 4);
            // The hammer is worn in the main hand and the torch in the off hand; the other three kit
            // tools are in the bag (the kit potions are not counted here).
            Assert.Equal(5, Inventory(server).AllItems().Count(item => item.ItemKind.StartsWith("tool.", StringComparison.Ordinal)));
            Assert.Equal(3, Inventory(server).Slots.Count(slot => slot.Item?.ItemKind.StartsWith("tool.", StringComparison.Ordinal) == true));
            await server.DisconnectAsync(client);
            await TickAsync(server, 2);
        }

        SqliteConnection.ClearAllPools();
        Exec(database, """
            DELETE FROM player_inventory_slots WHERE item_instance_id IN (SELECT item_instance_id FROM item_instances WHERE item_kind = 'tool.torch');
            DELETE FROM item_instances WHERE item_kind = 'tool.torch';
            DELETE FROM player_starter_grants;
            """);
        Assert.Equal(0L, Scalar(database, "SELECT COUNT(*) FROM item_instances WHERE item_kind = 'tool.torch';"));
        Assert.Equal(4L, Scalar(database, "SELECT COUNT(*) FROM item_instances WHERE item_kind LIKE 'tool.%';"));
        WriteWorldTime(database, WaveT2NightSpawnTests.WorldTicksAt(WorldClockSettings.Default, dayIndex: 1, hour: 23));

        // Phase 1 (night): the top-up grants the torch into slot 4; equip it, place a lamp, and let
        // the night wolf spawn.
        Vector3 lampCell;
        int[] wolves;
        await using (WireLoopbackServer server = await StartAsync(database))
        {
            Assert.Equal(WorldDayPhase.Night, server.Runtime.WorldClock.Phase);
            await using WireLoopbackClient client = await server.ConnectAndJoinAsync(Session);
            PlayerInventorySnapshot inventory = Inventory(server);
            InventorySlotSnapshot torch = Assert.Single(inventory.Slots, slot => slot.Item?.ItemKind == "tool.torch");
            // The hammer is worn, so the top-up's 1x2 torch takes the first fit, the corner at anchor 0.
            Assert.Equal(0, torch.SlotIndex);
            Assert.Equal("tool.builder-hammer", inventory.EquippedSlot?.Item?.ItemKind);

            await client.SendActionAsync(new InventoryEquipIntent(torch.SlotIndex));
            await TickAsync(server, 2);
            Assert.Equal(HeldLightKind.Torch, PlayerActor(server).HeldLight);

            lampCell = await PlaceLampAsync(server, client);
            await TickAsync(server, 8);
            wolves = ActiveWolfIds(server.Runtime);
            Assert.Equal(2, wolves.Length);
            output.WriteLine($"phase 1: lamp at {lampCell}, wolves {string.Join(", ", wolves)}");
            await server.DisconnectAsync(client);
            await TickAsync(server, 2);
        }

        SqliteConnection.ClearAllPools();
        AssertDatabase(database, enemies: 2);

        // Phase 2 (restart, still night): the lamp, both wolves (same ids, no third) and the equipped
        // torch come back. Rejoining several times never tops up a second torch.
        await using (WireLoopbackServer server = await StartAsync(database))
        {
            Assert.Equal(WorldDayPhase.Night, server.Runtime.WorldClock.Phase);
            Assert.Equal(wolves, ActiveWolfIds(server.Runtime));
            Assert.Equal(GeometryMaterialId.Lamp, server.Runtime.Map.TryGetCubeAtCell((int)lampCell.X, (int)lampCell.Y, (int)lampCell.Z)?.MaterialId);
            for (int rejoin = 0; rejoin < 3; rejoin++)
            {
                await using WireLoopbackClient client = await server.ConnectAndJoinAsync(Session);
                await TickAsync(server, 40);
                Assert.Equal(wolves, ActiveWolfIds(server.Runtime));
                PlayerInventorySnapshot inventory = Inventory(server);
                // The torch is worn (no bag row); the hammer it displaced and the other three kit items are
                // in the bag, and nothing was topped up again.
                Assert.DoesNotContain(inventory.Slots, slot => slot.Item?.ItemKind == "tool.torch");
                Assert.Equal(5, inventory.AllItems().Count(item => item.ItemKind.StartsWith("tool.", StringComparison.Ordinal)));
                Assert.Equal(1, inventory.AllItems().Count(item => item.ItemKind == "tool.torch"));
                Assert.Equal("tool.torch", inventory.EquippedSlot?.Item?.ItemKind);
                Assert.Equal(HeldLightKind.Torch, PlayerActor(server).HeldLight);
                await server.DisconnectAsync(client);
                await TickAsync(server, 2);
            }

            Assert.True(server.Runtime.PersistenceWrites?.Flush(TimeSpan.FromSeconds(30)) ?? true);
        }

        SqliteConnection.ClearAllPools();
        AssertDatabase(database, enemies: 2);

        // Phase 3 (restart at dawn): the first tick removes the night wolf (the higher id) and its row.
        WriteWorldTime(database, WaveT2NightSpawnTests.WorldTicksAt(WorldClockSettings.Default, dayIndex: 2, hour: 4) + 3);
        long keptWolfObjectId;
        await using (WireLoopbackServer server = await StartAsync(database))
        {
            // Rows are keyed by ObjectId; a restart gives the wolves fresh actor ids.
            Assert.True(server.Runtime.ObjectIdentities.TryGetObjectId(wolves[0], out ObjectId keptWolf));
            keptWolfObjectId = keptWolf.Value;
            Assert.Equal(WorldDayPhase.Dawn, server.Runtime.WorldClock.Phase);
            Assert.Equal(wolves, ActiveWolfIds(server.Runtime));
            await TickAsync(server, 1);
            Assert.Equal([wolves[0]], ActiveWolfIds(server.Runtime));
            await using WireLoopbackClient client = await server.ConnectAndJoinAsync(Session);
            await TickAsync(server, 32);
            Assert.Equal([wolves[0]], ActiveWolfIds(server.Runtime));
            Assert.Equal(HeldLightKind.Torch, PlayerActor(server).HeldLight);
            await server.DisconnectAsync(client);
            await TickAsync(server, 2);
            Assert.True(server.Runtime.PersistenceWrites?.Flush(TimeSpan.FromSeconds(30)) ?? true);
        }

        SqliteConnection.ClearAllPools();
        AssertDatabase(database, enemies: 1);
        Assert.Equal(keptWolfObjectId, Scalar(database, "SELECT object_id FROM enemy_instances;"));
    }

    private void AssertDatabase(TempDatabase database, long enemies)
    {
        Assert.Equal(enemies, Scalar(database, "SELECT COUNT(*) FROM enemy_instances;"));
        Assert.Equal(1L, Scalar(database, "SELECT COUNT(*) FROM placed_cube_instances WHERE material_id = 'Lamp';"));
        Assert.Equal(1L, Scalar(database, "SELECT COUNT(*) FROM item_instances WHERE item_kind = 'tool.torch';"));
        Assert.Equal(5L, Scalar(database, "SELECT COUNT(*) FROM item_instances WHERE item_kind LIKE 'tool.%';"));
        Assert.Equal("tool.torch", Scalar(database, $"""
            SELECT i.item_kind FROM player_inventory_slots s JOIN item_instances i ON i.item_instance_id = s.item_instance_id
            WHERE s.player_name = '{PlayerName}' AND s.slot_index = {InventoryConstants.EquippedSlotIndex};
            """));
        Assert.Equal("ok", Scalar(database, "PRAGMA integrity_check;"));
    }

    // Like a real client: selects Lamp (the build.material action), switches to Building and
    // presses primary use looking down at the floor in front (BuildUseIntent is not client-sendable;
    // the server raycasts the command's look). Returns the placed lamp's cell.
    internal static async Task<Vector3> PlaceLampAsync(WireLoopbackServer server, WireLoopbackClient client)
    {
        string session = client.SessionId;
        await client.SendActionAsync(new BuildMaterialSelectIntent(GeometryMaterialId.Lamp));
        await client.SendCommandAsync(PlayerInteractionMode.Building, PlayerMoveIntent.None, Vector3.UnitX, attack: false);
        await TickAsync(server, 2);
        Assert.Equal(GeometryMaterialId.Lamp, server.Runtime.ResolveSelectedBuildMaterial(new PlayerSessionId(session)));
        foreach (Vector3 look in new[]
        {
            new Vector3(1f, -0.35f, 0f), new Vector3(-1f, -0.35f, 0f), new Vector3(0f, -0.35f, 1f), new Vector3(0f, -0.35f, -1f),
            new Vector3(1f, -0.5f, 0f), new Vector3(-1f, -0.5f, 0f), new Vector3(0f, -0.5f, 1f), new Vector3(0f, -0.5f, -1f)
        })
        {
            await client.SendCommandAsync(PlayerInteractionMode.Building, PlayerMoveIntent.None, look.Normalized(), attack: true);
            await TickAsync(server, 1);
            await client.SendCommandAsync(PlayerInteractionMode.Building, PlayerMoveIntent.None, look.Normalized(), attack: false);
            await TickAsync(server, 2);
            if (FindLamp(server.Runtime) is { } cell)
            {
                return cell;
            }
        }

        Vector3 position = Assert.Single(server.Runtime.CaptureActorSnapshots(), actor => actor.SessionId == session).Position;
        string results = string.Join(" | ", client.Frames.Select(frame => frame.Decoded.Message).OfType<ServerActionMessage>()
            .Select(reply => $"#{reply.Result.ActionSequence} {(reply.Result.Accepted ? "ok" : reply.Result.Rejection?.Reason)}"));
        Assert.Fail($"no lamp could be placed around {position}; action results: {results}");
        return default;
    }

    internal static Vector3? FindLamp(WorldSessionRuntime runtime)
    {
        foreach (var cube in runtime.Map.SolidCubes)
        {
            if (cube.MaterialId == GeometryMaterialId.Lamp)
            {
                return new Vector3(MathF.Floor(cube.Position.X), MathF.Floor(cube.Position.Y), MathF.Floor(cube.Position.Z));
            }
        }

        return null;
    }

    private static async Task<WireLoopbackServer> StartAsync(TempDatabase database)
    {
        ServerPersistenceBundle bundle = database.OpenBundle();
        long worldTicks = bundle.WorldTimeTicks;
        DedicatedServerHost host = ServerBootstrap.CreateAuthoritativeHost(bundle);
        // DREAM_DAY_LENGTH_MINUTES may be set by a test in another collection; pin the default day.
        host.Runtime!.ConfigureWorldClock(WorldClockSettings.Default, worldTicks);
        return await WireLoopbackServer.StartAsync(host);
    }

    private static async Task TickAsync(WireLoopbackServer server, int ticks)
    {
        for (int i = 0; i < ticks; i++)
        {
            await server.TickAsync();
            Assert.True(await server.FlushAllAsync());
        }
    }

    private static PlayerInventorySnapshot Inventory(WireLoopbackServer server) =>
        server.Runtime.TryCaptureInventorySnapshot(new PlayerSessionId(Session))!.Value;

    private static ActorSnapshot PlayerActor(WireLoopbackServer server) =>
        Assert.Single(server.Runtime.CaptureActorSnapshots(), actor => actor.SessionId == Session);

    private static int[] ActiveWolfIds(WorldSessionRuntime runtime) =>
        runtime.Enemies.ActiveActors.Where(actor => actor.IsActive).Select(actor => actor.Combatant.Actor.Id).ToArray();

    private static void WriteWorldTime(TempDatabase database, long ticks)
    {
        using (ServerPersistenceBundle bundle = database.OpenBundle())
        {
            new SqlitePersistenceCheckpointWriter(bundle.Schema).Write(PersistenceCheckpoint.Empty with { WorldTimeTicks = ticks });
        }

        SqliteConnection.ClearAllPools();
    }

    internal static object? Scalar(TempDatabase database, string sql)
    {
        using SqliteConnection connection = Open(database);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        object? value = command.ExecuteScalar();
        return value is DBNull ? null : value;
    }

    internal static void Exec(TempDatabase database, string sql)
    {
        using SqliteConnection connection = Open(database);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static SqliteConnection Open(TempDatabase database)
    {
        SqliteConnection connection = new(new SqliteConnectionStringBuilder { DataSource = database.DatabasePath, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }
}
