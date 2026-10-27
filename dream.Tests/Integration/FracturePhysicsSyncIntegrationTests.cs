using BulletSharp;
using Dream.Client.Replication;
using Dream.Simulation.Physics;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using OpenTK.Mathematics;
using Xunit;
using Xunit.Abstractions;

namespace Dream.Tests.Integration;

// NET-2 made fracture rubble deterministic per cell, so a client that receives a piece-less
// CubeFractured rebuilds the server's pieces. MEM-2 rewrote PhysicsSyncSystem without
// allocations. This runs both together: physics-backed server and client maps fracture the same
// cube by their two different paths, then step physics and run sync ticks side by side.
public class FracturePhysicsSyncIntegrationTests(ITestOutputHelper output)
{
    private const int MapSize = 10;
    private const int Ticks = 180;
    private const float FixedDelta = 1f / 60f;

    private static readonly GeometryCellKey[] FracturedCells = [new(3, 1, 3), new(6, 1, 4), new(4, 1, 7)];

    [Fact]
    public void ServerFractureAndPiecelessClientFracture_MatchAndStayInSyncUnderPhysicsSync()
    {
        using PhysicsIntegrator serverPhysics = new();
        using PhysicsIntegrator clientPhysics = new();
        Map server = new(serverPhysics, width: MapSize, depth: MapSize);
        Map client = new(clientPhysics, width: MapSize, depth: MapSize);
        GeometryMaterialId[] materials = [GeometryMaterialId.Stone, GeometryMaterialId.Dirt, GeometryMaterialId.Sand];

        // A surviving dynamic cube next to the rubble keeps the cube sync path busy as well.
        Assert.True(server.AddCube(new Vector3(5f, 1f, 5f), withPhysics: true));
        Assert.True(client.AddCube(new Vector3(5f, 1f, 5f), withPhysics: true));
        List<WorldMutationDelta> mutations = [];
        for (int i = 0; i < FracturedCells.Length; i++)
        {
            GeometryCellKey cell = FracturedCells[i];
            Vector3 position = new(cell.X, cell.Y, cell.Z);
            Assert.True(server.AddCube(position, withPhysics: true, materialId: materials[i]));
            Assert.True(client.AddCube(position, withPhysics: true, materialId: materials[i]));
            Assert.True(server.FractureCubeIntoPieces(cell));
            mutations.Add(new WorldMutationDelta(WorldMutationKind.CubeFractured, position, Sequence: (ulong)(i + 1)));
        }

        RemoteWorldMutationApplier.ApplyWorldMutations(client, mutations);

        for (int i = 0; i < FracturedCells.Length; i++)
        {
            GeometryCellRecord serverRubble = Assert.IsType<GeometryCellRecord>(server.TryGetRubbleAtCell(FracturedCells[i]));
            GeometryCellRecord clientRubble = Assert.IsType<GeometryCellRecord>(client.TryGetRubbleAtCell(FracturedCells[i]));
            Assert.NotNull(serverRubble.FracturePieces);
            Assert.Equal(serverRubble.FracturePieces, clientRubble.FracturePieces);
            Assert.Equal(materials[i], clientRubble.Definition.MaterialId);
            Assert.Null(client.TryGetCubeAtCell(FracturedCells[i].X, FracturedCells[i].Y, FracturedCells[i].Z));
        }

        Assert.Equal(CountRubbleBodies(serverPhysics), CountRubbleBodies(clientPhysics));
        Assert.True(CountRubbleBodies(serverPhysics) > 0);
        int[] pieceCounts = FracturedCells.Select(cell => server.TryGetRubbleAtCell(cell)!.FracturePieces!.Count).ToArray();

        PhysicsSyncSystem serverSync = new();
        PhysicsSyncSystem clientSync = new();
        int serverMoves = 0;
        int clientMoves = 0;
        for (int tick = 0; tick < Ticks; tick++)
        {
            serverPhysics.StepSimulationFixed(FixedDelta);
            clientPhysics.StepSimulationFixed(FixedDelta);
            serverMoves += serverSync.SyncDynamicMapBodies(server, serverPhysics.DynamicMapCubeBodies).MovedCubes.Count;
            clientMoves += clientSync.SyncDynamicMapBodies(client, clientPhysics.DynamicMapCubeBodies).MovedCubes.Count;

            for (int i = 0; i < FracturedCells.Length; i++)
            {
                IReadOnlyList<GeometryFracturePiece> serverPieces = server.TryGetRubbleAtCell(FracturedCells[i])!.FracturePieces!;
                IReadOnlyList<GeometryFracturePiece> clientPieces = client.TryGetRubbleAtCell(FracturedCells[i])!.FracturePieces!;
                Assert.Equal(pieceCounts[i], serverPieces.Count);
                Assert.Equal(pieceCounts[i], clientPieces.Count);
                Assert.All(serverPieces, piece => Assert.True(piece.IsValid(), $"tick {tick}: invalid server piece {piece}"));
                // Identical inputs into identical Bullet worlds: the replicas must not drift apart.
                Assert.True(
                    serverPieces.SequenceEqual(clientPieces),
                    $"tick {tick} cell {FracturedCells[i]}: server and client rubble diverged.");
            }

            Assert.Empty(server.FindInvariantViolations());
            Assert.Empty(client.FindInvariantViolations());
        }

        Assert.Equal(serverMoves, clientMoves);

        // Settled: further sync ticks change nothing.
        GeometryFracturePiece[][] settled = FracturedCells
            .Select(cell => server.TryGetRubbleAtCell(cell)!.FracturePieces!.ToArray())
            .ToArray();
        for (int tick = 0; tick < 30; tick++)
        {
            serverPhysics.StepSimulationFixed(FixedDelta);
            PhysicsSyncResult result = serverSync.SyncDynamicMapBodies(server, serverPhysics.DynamicMapCubeBodies);
            Assert.Empty(result.MovedCubes);
        }

        for (int i = 0; i < FracturedCells.Length; i++)
        {
            GeometryFracturePiece[] after = server.TryGetRubbleAtCell(FracturedCells[i])!.FracturePieces!.ToArray();
            for (int p = 0; p < after.Length; p++)
            {
                Assert.True(
                    (after[p].Center - settled[i][p].Center).Length < 0.01f,
                    $"cell {FracturedCells[i]} piece {p} kept moving after settling: {settled[i][p].Center} -> {after[p].Center}");
            }
        }

        output.WriteLine(
            $"rubble bodies={CountRubbleBodies(serverPhysics)} pieces=[{string.Join(",", pieceCounts)}] " +
            $"cube moves server={serverMoves} client={clientMoves}");
    }

    [Fact]
    public void PiecelessFractureOnPhysicsClient_ThenSyncTicks_KeepsRubbleBodiesBoundToTheirPieces()
    {
        using PhysicsIntegrator clientPhysics = new();
        Map client = new(clientPhysics, width: MapSize, depth: MapSize);
        GeometryCellKey cell = FracturedCells[0];
        Assert.True(client.AddCube(new Vector3(cell.X, cell.Y, cell.Z), withPhysics: true, materialId: GeometryMaterialId.Stone));
        RemoteWorldMutationApplier.ApplyWorldMutations(
            client,
            [new WorldMutationDelta(WorldMutationKind.CubeFractured, new Vector3(cell.X, cell.Y, cell.Z), Sequence: 1)]);

        PhysicsSyncSystem sync = new();
        for (int tick = 0; tick < Ticks; tick++)
        {
            clientPhysics.StepSimulationFixed(FixedDelta);
            sync.SyncDynamicMapBodies(client, clientPhysics.DynamicMapCubeBodies);
        }

        IReadOnlyList<GeometryFracturePiece> pieces = client.TryGetRubbleAtCell(cell)!.FracturePieces!;
        int bound = 0;
        foreach (RigidBody body in clientPhysics.DynamicMapCubeBodies)
        {
            if (body.UserObject is not GeometryDynamicBodyReference reference)
            {
                continue;
            }

            bound++;
            Assert.Equal(cell, reference.CellKey);
            Assert.InRange(reference.PieceIndex, 0, pieces.Count - 1);
            // The reference the sync stored is the center the map accepted for that piece.
            Assert.Equal(pieces[reference.PieceIndex].Center, reference.InitialCenter);
        }

        Assert.Equal(pieces.Count, bound);
    }

    private static int CountRubbleBodies(PhysicsIntegrator physics)
    {
        return physics.DynamicMapCubeBodies.Count(body => body.UserObject is GeometryDynamicBodyReference);
    }
}
