using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Server.Persistence;
using Dream.Simulation.Combat;
using Dream.Simulation.Persistence;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using Dream.Tests.Server.Persistence;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Simulation.Runtime;

// Review S2 (wave 13): the combat resolver and the persistence bridge read the map through the
// runtime (IAuthoritativeCombatHost / IRuntimePersistenceHost.Map) instead of holding it, so after
// RebuildMapForFloorSize a kill must fracture the cube on the new map, persist its pieces from
// that map, and the next checkpoint must be a full sweep (the tracked map changed).
public sealed class RebuildMapCombatCheckpointTests
{
    private const int FloorSize = 12;

    [Fact]
    public void AfterAMapRebuild_AKillFracturesOnTheNewMap_PersistsItsPieces_AndTheNextCheckpointIsFull()
    {
        using PersistenceTestDatabase database = new();
        using WorldSessionRuntime runtime = new(
            FloorSize,
            new Vector3(5f, 1.5f, 5f),
            new SceneEventHub(),
            playerProfiles: new SqlitePlayerProfileRepository(database.Schema),
            worldPersistence: new SqliteWorldPersistenceRepository(database.Schema),
            persistenceWrites: PersistenceWriteQueue.Inline());
        using RuntimeAuthoritativeSessionHost host = new(runtime);
        PlayerSessionId session = new("session-a");
        host.JoinSession(session.Value, "Player A");
        runtime.Enemies.Clear();

        // Checkpoint tracking is set up on the startup map, and a steady capture is incremental.
        Assert.NotNull(runtime.CapturePersistenceCheckpoint());
        runtime.CapturePersistenceCheckpoint();
        long fullBefore = runtime.FullCheckpointCaptures;
        long incrementalBefore = runtime.IncrementalCheckpointCaptures;
        Assert.Equal(1, fullBefore);

        Map startupMap = runtime.Map;
        runtime.RebuildMapForFloorSize(FloorSize + 2);
        Assert.NotSame(startupMap, runtime.Map);

        ulong sequence = 1;
        Assert.True(runtime.ProcessAuthoritativeAction(new PlayerActionMessage(session, sequence++, new BuildUseIntent(new Vector3(7f, 1f, 7f)))).Accepted);
        Cube cube = Assert.IsType<Cube>(runtime.Map.TryGetCubeAtCell(7, 1, 7));
        Assert.Null(startupMap.TryGetCubeAtCell(7, 1, 7));
        Assert.True(runtime.Map.TryGetReplicatedId(cube, out int cubeId));
        Assert.Equal(1, database.Count($"SELECT COUNT(*) FROM placed_cube_instances WHERE cube_id = {cubeId};"));

        runtime.SetAuthoritativeInteractionMode(PlayerInteractionMode.Combat);
        Vector3 origin = new(7.5f, 1.5f, 9.5f);
        runtime.Player.SetWorldPosition(origin);
        GeometryCellKey cell = new(7, 1, 7);
        for (int i = 0; i < 100 && runtime.Map.TryGetRubbleAtCell(cell) == null; i++)
        {
            runtime.Player.Combatant.AdvanceCombat(5f);
            AuthoritativeActionResult hit = runtime.ProcessAuthoritativeAction(
                new PlayerActionMessage(session, sequence++, new CombatUseIntent(origin, -Vector3.UnitZ)));
            Assert.True(hit.Accepted, hit.Rejection?.ToString());
        }

        Assert.NotNull(runtime.Map.TryGetRubbleAtCell(cell));
        Assert.Null(startupMap.TryGetRubbleAtCell(cell));
        Assert.Null(runtime.Map.TryGetCubeAtCell(7, 1, 7));
        int livePieces = runtime.Map.CaptureFracturePieces().Count(piece => piece.Cell == cell);
        Assert.True(livePieces > 0);
        Assert.Equal(livePieces, database.Count($"SELECT COUNT(*) FROM cube_fracture_pieces WHERE source_cube_id = {cubeId};"));
        Assert.Equal(0, database.Count($"SELECT COUNT(*) FROM placed_cube_instances WHERE cube_id = {cubeId};"));

        PersistenceCheckpoint checkpoint = Assert.IsType<PersistenceCheckpoint>(runtime.CapturePersistenceCheckpoint());
        Assert.False(checkpoint.IsIncremental);
        Assert.Equal(fullBefore + 1, runtime.FullCheckpointCaptures);
        Assert.Equal(incrementalBefore, runtime.IncrementalCheckpointCaptures);
        Assert.Equal(livePieces, checkpoint.FracturePieces.Count(piece => piece.OriginObjectId.AsInt() == cubeId));
    }
}
