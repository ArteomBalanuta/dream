using System.Reflection;
using Microsoft.Data.Sqlite;
using OpenTK.Mathematics;
using Xunit;
using Xunit.Abstractions;
using Dream.Client.Replication;
using Dream.Client.World;
using Dream.Multiplayer.Protocol;
using Dream.Server;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using static Dream.Tests.Integration.PersistentWorldSeedDeterminismIntegrationTests;

namespace Dream.Tests.Integration;

// Persistence wave D: destroyed terrain stays destroyed across a restart. A raised terrain cube is
// destroyed by real combat over loopback and a sculpture (subdivided) terrain cell through the same
// persistence calls the combat resolver makes (the combat ray skips sculpture cells). After a
// restart from the same database a client joins, builds the base terrain itself as a real client
// does, applies the join snapshot, and must match the server: world checksum, raised cell keys and
// rubble cells.
public sealed class TerrainPersistenceLoopbackTests(ITestOutputHelper output)
{
    private const string Fighter = "terrain-fighter";
    private const string Joiner = "terrain-joiner";

    [Fact(Skip = "wave D: needs workstream B (terrain override persist and restore), unskip at glue")]
    public async Task DestroyedTerrainCube_AndSculptureCell_StayDestroyed_AfterARestart_AndAJoiningClientMatchesTheServer()
    {
        using TempDatabase database = new();
        GeometryCellKey cubeCell;
        GeometryCellKey sculptureCell;
        ulong checksumBefore;
        HashSet<GeometryCellKey> raisedBefore;
        HashSet<GeometryCellKey> rubbleBefore;
        await using (WireLoopbackServer server = await WireLoopbackServer.StartAsync(
            ServerBootstrap.CreateAuthoritativeHost(database.OpenBundle())))
        {
            await using WireLoopbackClient fighter = await server.ConnectAndJoinAsync(Fighter);
            server.Runtime.Enemies.Clear();
            Map map = server.Runtime.Map;

            cubeCell = await FractureNearestTerrainCubeAsync(server, fighter);
            sculptureCell = FractureSculptureCell(server.Runtime);
            Assert.NotEqual(cubeCell, sculptureCell);
            Assert.NotNull(map.TryGetRubbleAtCell(cubeCell));
            Assert.NotNull(map.TryGetRubbleAtCell(sculptureCell));
            Assert.Null(map.TryGetCubeAtCell(cubeCell.X, cubeCell.Y, cubeCell.Z));
            Assert.Null(map.TryGetCubeAtCell(sculptureCell.X, sculptureCell.Y, sculptureCell.Z));

            await TickAsync(server, 5);
            Assert.True(server.Runtime.PersistenceWrites?.Flush(TimeSpan.FromSeconds(30)) ?? true);
            checksumBefore = map.WorldChecksum;
            raisedBefore = RaisedKeys(map);
            rubbleBefore = RubbleKeys(map);
            await server.DisconnectAsync(fighter);
            await TickAsync(server, 2);
            Assert.True(server.Runtime.PersistenceWrites?.Flush(TimeSpan.FromSeconds(30)) ?? true);
        }

        SqliteConnection.ClearAllPools();
        Assert.Equal(2L, ObjectIdTestSupport.Scalar(database.DatabasePath, "SELECT COUNT(*) FROM terrain_cell_overrides WHERE world_id = 1;"));

        await using (WireLoopbackServer server = await WireLoopbackServer.StartAsync(
            ServerBootstrap.CreateAuthoritativeHost(database.OpenBundle())))
        {
            Map map = server.Runtime.Map;
            Assert.Null(map.TryGetCubeAtCell(cubeCell.X, cubeCell.Y, cubeCell.Z));
            Assert.Null(map.TryGetCubeAtCell(sculptureCell.X, sculptureCell.Y, sculptureCell.Z));
            Assert.NotNull(map.TryGetRubbleAtCell(cubeCell));
            Assert.NotNull(map.TryGetRubbleAtCell(sculptureCell));
            Assert.Equal(raisedBefore, RaisedKeys(map));
            Assert.Equal(rubbleBefore, RubbleKeys(map));
            Assert.Equal(checksumBefore, map.WorldChecksum);
            Assert.Empty(map.FindInvariantViolations());

            await using WireLoopbackClient joiner = await server.ConnectAndJoinAsync(Joiner);
            using ClientWorldSession world = new(map.Width, new Vector3(5f, 1.5f, 5f), new SceneEventHub());
            ApplyAllFrames(joiner, world);

            Assert.Equal(WorldStateChecksum.Compute(map), WorldStateChecksum.Compute(world.Map));
            Assert.Equal(RaisedKeys(map), RaisedKeys(world.Map));
            Assert.Equal(RubbleKeys(map), RubbleKeys(world.Map));
            Assert.NotNull(world.Map.TryGetRubbleAtCell(cubeCell));
            Assert.NotNull(world.Map.TryGetRubbleAtCell(sculptureCell));
            output.WriteLine($"restart kept {cubeCell} and {sculptureCell} destroyed; client matches ({RaisedKeys(map).Count} raised, {RubbleKeys(map).Count} rubble cells)");
            await server.DisconnectAsync(joiner);
            await TickAsync(server, 2);
        }
    }

    private static HashSet<GeometryCellKey> RaisedKeys(Map map) =>
        [.. map.GeometryCells.Keys.Where(key => key.Y > 0)];

    private static HashSet<GeometryCellKey> RubbleKeys(Map map) =>
        [.. map.RubbleCells.Keys];

    private static async Task TickAsync(WireLoopbackServer server, int ticks)
    {
        for (int i = 0; i < ticks; i++)
        {
            await server.TickAsync();
            Assert.True(await server.FlushAllAsync());
        }
    }

    // Attacks the nearest raised terrain cube from the fighter's eye until it fractures (combat,
    // resolver and persistence hooks included).
    private static async Task<GeometryCellKey> FractureNearestTerrainCubeAsync(WireLoopbackServer server, WireLoopbackClient fighter)
    {
        Map map = server.Runtime.Map;
        Vector3 eye = server.Runtime.Player.Camera.Position;
        Cube target = map.SolidCubes
            .Where(cube => cube.Position.Y > 1f
                && map.TryGetCellForCube(cube, out GeometryCellKey candidate)
                && map.TryGetGeometryCellAtCell(candidate.X, candidate.Y, candidate.Z)?.SubdivisionState == null)
            .OrderBy(cube => (cube.Position - eye).LengthSquared)
            .First();
        Assert.True(map.TryGetCellForCube(target, out GeometryCellKey cell));
        Assert.True(map.IsSourceCell(cell));
        for (int tick = 0; tick < 1200; tick++)
        {
            Vector3 aim = (target.Position - server.Runtime.Player.Camera.Position).Normalized();
            await fighter.SendCommandAsync(PlayerInteractionMode.Combat, PlayerMoveIntent.None, aim, attack: true);
            await server.TickAsync();
            Assert.True(await server.FlushAllAsync());
            if (map.TryGetRubbleAtCell(cell) != null)
            {
                await fighter.SendCommandAsync(PlayerInteractionMode.Building, PlayerMoveIntent.None, Vector3.UnitZ, attack: false);
                return cell;
            }
        }

        throw new Xunit.Sdk.XunitException($"terrain cube at {cell} never fractured");
    }

    // Destroys a sculpture source cell as the combat resolver's fracture branch does, in the same
    // call order: fracture, delete the persisted cube, terrain-removed, fracture-persist.
    private static GeometryCellKey FractureSculptureCell(WorldSessionRuntime runtime)
    {
        Map map = runtime.Map;
        GeometryCellKey cell = map.GeometryCells
            .Where(pair => pair.Key.Y > 0
                && pair.Value.SubdivisionState != null
                && pair.Value.FracturePieces == null
                && map.IsSourceCell(pair.Key)
                && map.Cubes[pair.Key.X, pair.Key.Y, pair.Key.Z] != null)
            .Select(pair => pair.Key)
            .First();
        Cube cube = map.Cubes[cell.X, cell.Y, cell.Z]!;
        const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        object worldObjects = typeof(WorldSessionRuntime).GetField("_worldObjects", Private)!.GetValue(runtime)!;
        WorldObjectRecord record = (WorldObjectRecord)worldObjects.GetType()
            .GetMethod("TryResolveCubeRecord")!
            .Invoke(worldObjects, [map, cube])!;
        Assert.True(map.FractureCubeIntoPieces(cell));
        Invoke(runtime, "DeletePersistedPlacedCube", record.Actor.Id);
        Invoke(runtime, "PersistTerrainCellRemoved", record, cell);
        Invoke(runtime, "PersistFracturePieces", record, cell);
        return cell;
    }

    private static void Invoke(WorldSessionRuntime runtime, string name, params object[] arguments)
    {
        MethodInfo method = typeof(WorldSessionRuntime).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(nameof(WorldSessionRuntime), name);
        method.Invoke(runtime, arguments);
    }

    // Applies every frame the joiner received, in order, the way PlayScene does (the world arrives
    // in chunks that the reassembler hands on).
    private static void ApplyAllFrames(WireLoopbackClient client, ClientWorldSession world)
    {
        WorldTransferReassembler transfers = new();
        List<IServerToClientMessage> delivered = [];
        foreach (WireFrame frame in client.Frames)
        {
            delivered.Clear();
            transfers.Accept((IServerToClientMessage)frame.Decoded.Message, delivered.Add);
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
