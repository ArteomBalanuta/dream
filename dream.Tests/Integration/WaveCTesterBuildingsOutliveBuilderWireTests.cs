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
using Dream.Simulation.Persistence;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using static Dream.Tests.Integration.ObjectIdTestSupport;
using static Dream.Tests.Integration.PersistentWorldSeedDeterminismIntegrationTests;

namespace Dream.Tests.Integration;

// Wave C tester: buildings outlive their builder, end to end over loopback TCP. Two clients, A
// builds two cubes and fractures one; B is present. The server stops, A's profile row is deleted,
// and a new server starts. B (who joins the new server) must see both A's standing cube and A's
// rubble, with identical cells and materials, can damage and break the cube, the damage survives
// another restart, and no checkpoint skips the ownerless rows.
public sealed class WaveCTesterBuildingsOutliveBuilderWireTests(ITestOutputHelper output)
{
    private const string SessionA = "tb-a";
    private const string SessionB = "tb-b";

    [Fact]
    public async Task ABuildsAndFractures_AIsDeleted_BSeesEverything_BDamagesAndBreaksIt_AcrossRestarts()
    {
        using TempDatabase database = new();

        // Phase 1: A builds two cubes and breaks the first; B stands by.
        GeometryCellKey brokenCell;
        GeometryCellKey standingCell;
        string ownerA;
        await using (WireLoopbackServer server = await StartAsync(database, seedBaseline: true))
        {
            server.Runtime.Enemies.Clear();
            await using WireLoopbackClient a = await server.ConnectAndJoinAsync(SessionA);
            await using WireLoopbackClient b = await server.ConnectAndJoinAsync(SessionB);
            Vector3 position = Position(server, SessionA);
            output.WriteLine($"A at {position}, B at {Position(server, SessionB)}");
            Vector3 cellOne = new(MathF.Floor(position.X) + 1f, 1f, MathF.Floor(position.Z));
            Vector3 cellTwo = new(MathF.Floor(position.X), 1f, MathF.Floor(position.Z) + 1f);
            brokenCell = new GeometryCellKey((int)cellOne.X, 1, (int)cellOne.Z);
            standingCell = new GeometryCellKey((int)cellTwo.X, 1, (int)cellTwo.Z);
            // A join restores the mode from the profile, and a new character now starts in Combat. A builds
            // here, so it requests Building the way a real client does, and lets the tick apply it.
            await a.SendCommandAsync(PlayerInteractionMode.Building, PlayerMoveIntent.None, Vector3.UnitZ, attack: false);
            await TickAsync(server, 2);
            Assert.True(server.Host.TryEnqueueServerAction(new PlayerActionMessage(new PlayerSessionId(SessionA), 9001, new BuildUseIntent(cellOne))));
            Assert.True(server.Host.TryEnqueueServerAction(new PlayerActionMessage(new PlayerSessionId(SessionA), 9002, new BuildUseIntent(cellTwo))));
            await TickAsync(server, 3);
            Assert.NotNull(server.Runtime.Map.TryGetCubeAtCell(brokenCell.X, brokenCell.Y, brokenCell.Z));
            Assert.NotNull(server.Runtime.Map.TryGetCubeAtCell(standingCell.X, standingCell.Y, standingCell.Z));

            int ticks = 0;
            while (server.Runtime.Map.TryGetRubbleAtCell(brokenCell) == null && ticks++ < 1500)
            {
                await a.SendCommandAsync(PlayerInteractionMode.Combat, PlayerMoveIntent.None, Vector3.UnitX, attack: true);
                await server.TickAsync();
                Assert.True(await server.FlushAllAsync());
            }

            Assert.NotNull(server.Runtime.Map.TryGetRubbleAtCell(brokenCell));
            Assert.NotNull(server.Runtime.Map.TryGetCubeAtCell(standingCell.X, standingCell.Y, standingCell.Z));
            await a.SendCommandAsync(PlayerInteractionMode.Building, PlayerMoveIntent.None, Vector3.UnitZ, attack: false);
            await TickAsync(server, 2);
            await server.DisconnectAsync(a);
            await server.DisconnectAsync(b);
            await TickAsync(server, 2);
            Assert.True(server.Runtime.PersistenceWrites?.Flush(TimeSpan.FromSeconds(30)) ?? true);
        }

        SqlitePools.Release(database.DatabasePath);
        PersistedIds built = ReadIds(database.DatabasePath);
        output.WriteLine($"phase 1: cubes {built.Cubes.Count} pieces {built.Pieces.Count} origins {built.Origins.Count} next {built.NextObjectId}");
        Assert.Single(built.Cubes);
        Assert.NotEmpty(built.Pieces);
        ownerA = Assert.IsType<string>(ScalarObject(database.DatabasePath, "SELECT owner_player_name FROM placed_cube_instances;"));
        DeleteProfile(database.DatabasePath, ownerA);
        Assert.Equal(1L, Scalar(database.DatabasePath, "SELECT COUNT(*) FROM placed_cube_instances WHERE owner_player_name IS NULL;"));
        Assert.Equal(built.Pieces.Count, (int)Scalar(database.DatabasePath, "SELECT COUNT(*) FROM cube_fracture_pieces WHERE owner_player_name IS NULL;"));
        long cubeObjectId = built.Cubes[0];
        int maxHealth = (int)Scalar(database.DatabasePath, "SELECT max_health FROM placed_cube_instances;");

        // Phase 2: restart. B alone joins; its client world must show A's cube and rubble.
        await using (WireLoopbackServer server = await StartAsync(database))
        {
            Map map = server.Runtime.Map;
            Assert.NotNull(map.TryGetCubeAtCell(standingCell.X, standingCell.Y, standingCell.Z));
            Assert.NotNull(map.TryGetRubbleAtCell(brokenCell));

            // Straight after the restore (no tick yet) the server's rubble is exactly the rows.
            var restoredPieces = map.CaptureFracturePieces().ToList();
            Assert.Equal(built.Pieces.Count, restoredPieces.Count);
            Assert.All(restoredPieces, piece => Assert.Equal(brokenCell, piece.Cell));
            using (SqliteConnection connection = Open(database.DatabasePath))
            {
                using SqliteCommand command = connection.CreateCommand();
                command.CommandText = "SELECT piece_index, center_x, center_y, center_z, side_length, material_id FROM cube_fracture_pieces ORDER BY origin_object_id, piece_index;";
                using SqliteDataReader reader = command.ExecuteReader();
                int row = 0;
                while (reader.Read())
                {
                    Assert.Equal(row, reader.GetInt32(0));
                    Assert.Equal(new Vector3((float)reader.GetDouble(1), (float)reader.GetDouble(2), (float)reader.GetDouble(3)), restoredPieces[row].Piece.Center);
                    Assert.Equal((float)reader.GetDouble(4), restoredPieces[row].Piece.SideLength);
                    Assert.Equal(reader.GetString(5), restoredPieces[row].MaterialId.ToString());
                    row++;
                }

                Assert.Equal(restoredPieces.Count, row);
            }

            await using WireLoopbackClient b = await server.ConnectAndJoinAsync(SessionB);
            using ClientWorldSession world = new(map.Width, new Vector3(5f, 1.5f, 5f), new SceneEventHub());
            await ApplyJoinWorldAsync(server, b, world);
            AssertMapsMatch(map, world.Map, standingCell, brokenCell);

            // The checkpoint holds the ownerless rows: one cube, every piece.
            PersistenceCheckpoint checkpoint = server.Runtime.CapturePersistenceCheckpoint(full: true)!;
            PersistedPlacedCube cube = Assert.Single(checkpoint.PlacedCubes);
            Assert.Null(cube.OwnerPlayerName);
            Assert.Equal(cubeObjectId, cube.ObjectId.Value);
            Assert.Equal(built.Pieces.Count, checkpoint.FracturePieces.Count);
            Assert.All(checkpoint.FracturePieces, piece => Assert.Null(piece.OwnerPlayerName));

            // B walks into reach (it may spawn away from A's cell), then hits the cube just
            // until it is damaged, not broken.
            await AimAndAttackUntilAsync(server, b, standingCell, () => HealthOf(server.Runtime, cubeObjectId) is { } h && h < maxHealth && h > 0, maxTicks: 900);
            int damagedHealth = HealthOf(server.Runtime, cubeObjectId) ?? 0;
            output.WriteLine($"phase 2: cube health {damagedHealth}/{maxHealth}");
            Assert.InRange(damagedHealth, 1, maxHealth - 1);
            await b.SendCommandAsync(PlayerInteractionMode.Building, PlayerMoveIntent.None, Vector3.UnitZ, attack: false);
            await TickAsync(server, 3);
            await server.DisconnectAsync(b);
            await TickAsync(server, 2);
        }

        SqlitePools.Release(database.DatabasePath);
        int dbHealth = (int)Scalar(database.DatabasePath, $"SELECT current_health FROM placed_cube_instances WHERE object_id = {cubeObjectId};");
        Assert.InRange(dbHealth, 1, maxHealth - 1);
        Assert.Equal(1L, Scalar(database.DatabasePath, "SELECT COUNT(*) FROM placed_cube_instances WHERE owner_player_name IS NULL;"));

        // Phase 3: restart again; the damage persisted. B breaks the cube.
        long rubbleBefore = Scalar(database.DatabasePath, "SELECT COUNT(*) FROM cube_fracture_pieces;");
        await using (WireLoopbackServer server = await StartAsync(database))
        {
            Assert.Equal(dbHealth, HealthOf(server.Runtime, cubeObjectId));
            await using WireLoopbackClient b = await server.ConnectAndJoinAsync(SessionB);
            await AimAndAttackUntilAsync(server, b, standingCell, () => server.Runtime.Map.TryGetRubbleAtCell(standingCell) != null, maxTicks: 1500);
            Assert.NotNull(server.Runtime.Map.TryGetRubbleAtCell(standingCell));
            Assert.Null(server.Runtime.Map.TryGetCubeAtCell(standingCell.X, standingCell.Y, standingCell.Z));
            await b.SendCommandAsync(PlayerInteractionMode.Building, PlayerMoveIntent.None, Vector3.UnitZ, attack: false);
            await TickAsync(server, 3);
            await server.DisconnectAsync(b);
            await TickAsync(server, 2);
        }

        SqlitePools.Release(database.DatabasePath);
        PersistedIds broken = ReadIds(database.DatabasePath);
        output.WriteLine($"phase 3: cubes {broken.Cubes.Count} pieces {broken.Pieces.Count} (was {rubbleBefore}) origins {string.Join(",", broken.Origins)} next {broken.NextObjectId}");
        Assert.Empty(broken.Cubes);
        Assert.True(broken.Pieces.Count > rubbleBefore);
        Assert.Contains(cubeObjectId, broken.Origins);
        Assert.Equal(0L, Scalar(database.DatabasePath, "SELECT COUNT(*) FROM cube_fracture_pieces WHERE owner_player_name IS NOT NULL;"));
        Assert.Equal(broken.AllObjects.Count, broken.AllObjects.Distinct().Count());
        Assert.Empty(broken.Origins.Intersect(broken.AllObjects));
        Assert.All(broken.AllObjects.Concat(broken.Origins), id => Assert.True(id < broken.NextObjectId));

        // Phase 4: both rubble sets come back after the last restart, ownerless and identical.
        List<(GeometryCellKey Cell, int PieceIndex, GeometryFracturePiece Piece, GeometryMaterialId MaterialId)> expected;
        await using (WireLoopbackServer server = await StartAsync(database))
        {
            expected = [.. server.Runtime.Map.CaptureFracturePieces()];
            Assert.Equal(broken.Pieces.Count, expected.Count);
            Assert.Contains(expected, piece => piece.Cell == brokenCell);
            Assert.Contains(expected, piece => piece.Cell == standingCell);
            await using WireLoopbackClient b = await server.ConnectAndJoinAsync(SessionB);
            using ClientWorldSession world = new(server.Runtime.Map.Width, new Vector3(5f, 1.5f, 5f), new SceneEventHub());
            await ApplyJoinWorldAsync(server, b, world);
            AssertMapsMatch(server.Runtime.Map, world.Map, null, brokenCell, standingCell);
            await server.DisconnectAsync(b);
            await TickAsync(server, 2);
        }
    }

    private static void AssertMapsMatch(Map server, Map client, GeometryCellKey? standingCell, params GeometryCellKey[] rubbleCells)
    {
        if (standingCell is { } cell)
        {
            Cube serverCube = Assert.IsType<Cube>(server.TryGetCubeAtCell(cell.X, cell.Y, cell.Z));
            Cube clientCube = Assert.IsType<Cube>(client.TryGetCubeAtCell(cell.X, cell.Y, cell.Z));
            Assert.Equal(serverCube.MaterialId, clientCube.MaterialId);
        }

        foreach (GeometryCellKey rubbleCell in rubbleCells)
        {
            var serverPieces = server.CaptureFracturePieces().Where(piece => piece.Cell == rubbleCell).ToList();
            var clientPieces = client.CaptureFracturePieces().Where(piece => piece.Cell == rubbleCell).ToList();
            Assert.NotEmpty(serverPieces);
            Assert.Equal(serverPieces.Count, clientPieces.Count);
            for (int i = 0; i < serverPieces.Count; i++)
            {
                Assert.Equal(serverPieces[i].MaterialId, clientPieces[i].MaterialId);
                Assert.Equal(serverPieces[i].PieceIndex, clientPieces[i].PieceIndex);
                // Rubble settles under physics on both sides, so the centres only agree to within a cell.
                Assert.True(
                    (serverPieces[i].Piece.Center - clientPieces[i].Piece.Center).Length < 1f,
                    $"piece {i} of {rubbleCell}: server {serverPieces[i].Piece.Center}, client {clientPieces[i].Piece.Center}");
                Assert.Equal(serverPieces[i].Piece.SideLength, clientPieces[i].Piece.SideLength, 3);
            }
        }
    }

    // Applies B's join frames (world transfer in chunks) to a client world, as RemoteClientSession does.
    private static async Task ApplyJoinWorldAsync(WireLoopbackServer server, WireLoopbackClient client, ClientWorldSession world)
    {
        WorldTransferReassembler transfers = new();
        List<IServerToClientMessage> delivered = [];
        int frameIndex = 0;
        for (int tick = 0; tick < 40; tick++)
        {
            await client.SendCommandAsync(PlayerInteractionMode.Building, PlayerMoveIntent.None, Vector3.UnitZ, attack: false);
            await server.TickAsync();
            Assert.True(await server.FlushAllAsync());
            await client.WaitForSnapshotTickAsync(server.Runtime.CurrentServerTick);
            IReadOnlyList<WireFrame> frames = client.Frames;
            for (; frameIndex < frames.Count; frameIndex++)
            {
                delivered.Clear();
                transfers.Accept((IServerToClientMessage)frames[frameIndex].Decoded.Message, delivered.Add);
                foreach (IServerToClientMessage message in delivered)
                {
                    switch (message)
                    {
                        case JoinAcceptedMessage accepted:
                            ClientJoinAcceptedApplier.ApplyToRuntime(world, accepted);
                            break;
                        case ServerSnapshotMessage { Snapshot: var snapshot }:
                            RemoteWorldMutationApplier.ApplySnapshotToRuntime(world, snapshot);
                            break;
                    }
                }
            }
        }
    }

    // Walks toward the cell, then aims at its centre and attacks until done() holds.
    private static async Task AimAndAttackUntilAsync(WireLoopbackServer server, WireLoopbackClient client, GeometryCellKey cell, Func<bool> done, int maxTicks)
    {
        Vector3 target = new(cell.X + 0.5f, cell.Y + 0.5f, cell.Z + 0.5f);
        for (int tick = 0; tick < maxTicks && !done(); tick++)
        {
            Vector3 eye = Position(server, client.SessionId) + new Vector3(0f, 0.35f, 0f);
            Vector3 toTarget = target - eye;
            float flat = new Vector2(toTarget.X, toTarget.Z).Length;
            bool inReach = flat <= 2.2f;
            Vector3 aim = toTarget.LengthSquared < 0.0001f ? Vector3.UnitX : toTarget.Normalized();
            Vector3 flatDir = flat < 0.001f ? Vector3.UnitX : new Vector3(toTarget.X, 0f, toTarget.Z).Normalized();
            await client.SendCommandAsync(
                PlayerInteractionMode.Combat,
                inReach ? PlayerMoveIntent.None : new PlayerMoveIntent(1f, 0f),
                inReach ? aim : flatDir,
                attack: inReach);
            await server.TickAsync();
            Assert.True(await server.FlushAllAsync());
        }

        Assert.True(done(), "the condition was not reached within the tick budget");
    }

    private static int? HealthOf(WorldSessionRuntime runtime, long cubeObjectId)
    {
        if (!runtime.ObjectIdentities.TryGetActorId(new ObjectId(cubeObjectId), out int actorId) ||
            !runtime.TryResolveCombatActor(actorId, out var handle))
        {
            return null;
        }

        return handle.Damageable.Health.CurrentHealth;
    }

    private static Vector3 Position(WireLoopbackServer server, string session) =>
        Assert.Single(server.Runtime.CaptureActorSnapshots(), actor => actor.SessionId == session).Position;

    // seedBaseline is true only for the first start. A new profile-backed character is created at
    // Strength 10, so player.min_damage (5 + strength * 2) is 25 against a 100 hp cube and it takes several
    // hits (the loop above waits for rubble over up to 1500 ticks of attacks, and the second cube must still
    // be standing). The baseline's Strength 10 gives 25-50 per hit, so seeding is a no-op on a new character
    // and is kept to state the premise. Later starts must not seed: A's profile row is deleted between phases and
    // seeding would bring A back, which is the very thing the test is about.
    private static async Task<WireLoopbackServer> StartAsync(TempDatabase database, bool seedBaseline = false)
    {
        ServerPersistenceBundle bundle = database.OpenBundle();
        if (seedBaseline)
        {
            TestStatBaseline.SeedMissingProfiles(bundle.Schema, [WorldSessionRuntime.ResolveJoinPlayerName(SessionA, $"Name {SessionA}"), WorldSessionRuntime.ResolveJoinPlayerName(SessionB, $"Name {SessionB}")]);
        }

        return await WireLoopbackServer.StartAsync(ServerBootstrap.CreateAuthoritativeHost(bundle));
    }

    private static async Task TickAsync(WireLoopbackServer server, int ticks)
    {
        for (int i = 0; i < ticks; i++)
        {
            await server.TickAsync();
            Assert.True(await server.FlushAllAsync());
        }
    }

    private static object? ScalarObject(string databasePath, string sql)
    {
        using SqliteConnection connection = Open(databasePath);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static void DeleteProfile(string databasePath, string playerName)
    {
        using SqliteConnection connection = Open(databasePath, readOnly: false);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON; DELETE FROM player_profiles WHERE player_name = $name;";
        command.Parameters.AddWithValue("$name", playerName);
        Assert.Equal(1, command.ExecuteNonQuery());
    }
}
