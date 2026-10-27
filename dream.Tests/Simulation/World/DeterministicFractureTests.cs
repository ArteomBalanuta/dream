using Dream.Client.Replication;
using Dream.Simulation.Physics;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Simulation.World;

// Rubble used to come from Random.Shared, so the client's piece-less fallback never matched the
// server's pieces.
public class DeterministicFractureTests
{
    [Fact]
    public void CreateFirstPassRubble_SameCellGivesIdenticalPieces()
    {
        GeometryCellKey cell = new(3, 1, 3);

        IReadOnlyList<GeometryFracturePiece> first = CubeFracturePattern.CreateFirstPassRubble(cell);
        IReadOnlyList<GeometryFracturePiece> second = CubeFracturePattern.CreateFirstPassRubble(cell);

        Assert.Equal(first, second);
    }

    [Fact]
    public void CreateFirstPassRubble_AdjacentCellsGiveDifferentPieces()
    {
        GeometryCellKey cell = new(3, 1, 3);
        GeometryFracturePiece[] origin = ToLocalPieces(cell, CubeFracturePattern.CreateFirstPassRubble(cell));

        GeometryCellKey[] neighbors =
        [
            new(4, 1, 3),
            new(2, 1, 3),
            new(3, 2, 3),
            new(3, 1, 4),
            new(3, 1, 2)
        ];
        foreach (GeometryCellKey neighbor in neighbors)
        {
            GeometryFracturePiece[] local = ToLocalPieces(neighbor, CubeFracturePattern.CreateFirstPassRubble(neighbor));
            Assert.NotEqual(origin, local);
        }
    }

    [Fact]
    public void ServerFractureAndClientPiecelessFallback_ProduceIdenticalRubble()
    {
        GeometryCellKey cell = new(3, 1, 3);
        using PhysicsIntegrator serverPhysics = new();
        Map server = new(serverPhysics, width: 10, depth: 10);
        Assert.True(server.AddCube(new Vector3(3f, 1f, 3f), materialId: GeometryMaterialId.Stone));
        Assert.True(server.FractureCubeIntoPieces(cell));

        Map client = new(width: 10, depth: 10);
        Assert.True(client.AddCube(new Vector3(3f, 1f, 3f), withPhysics: false, materialId: GeometryMaterialId.Stone));
        RemoteWorldMutationApplier.ApplyWorldMutations(
            client,
            [new WorldMutationDelta(WorldMutationKind.CubeFractured, new Vector3(3f, 1f, 3f), Sequence: 1)]);

        GeometryCellRecord serverRubble = Assert.IsType<GeometryCellRecord>(server.TryGetRubbleAtCell(cell));
        GeometryCellRecord clientRubble = Assert.IsType<GeometryCellRecord>(client.TryGetRubbleAtCell(cell));
        Assert.NotNull(serverRubble.FracturePieces);
        Assert.Equal(serverRubble.FracturePieces, clientRubble.FracturePieces);
        Assert.Equal(GeometryMaterialId.Stone, clientRubble.Definition.MaterialId);
        Assert.Null(client.TryGetCubeAtCell(3, 1, 3));
    }

    [Fact]
    public void PiecelessFallbackWithoutLocalCube_UsesMutationMaterialAndServerPieces()
    {
        GeometryCellKey cell = new(4, 2, 5);
        Map server = new(width: 10, depth: 10);
        Assert.True(server.AddCube(new Vector3(4f, 2f, 5f), withPhysics: false, materialId: GeometryMaterialId.Sand));
        Assert.True(server.FractureCubeIntoPieces(cell));

        Map client = new(width: 10, depth: 10);
        RemoteWorldMutationApplier.ApplyWorldMutations(
            client,
            [new WorldMutationDelta(WorldMutationKind.CubeFractured, new Vector3(4f, 2f, 5f), MaterialId: GeometryMaterialId.Sand)]);

        GeometryCellRecord clientRubble = Assert.IsType<GeometryCellRecord>(client.TryGetRubbleAtCell(cell));
        Assert.Equal(server.TryGetRubbleAtCell(cell)!.FracturePieces, clientRubble.FracturePieces);
        Assert.Equal(GeometryMaterialId.Sand, clientRubble.Definition.MaterialId);
    }

    [Fact]
    public void PiecelessFallback_MutationMaterialOverridesLocalCubeMaterial()
    {
        GeometryCellKey cell = new(3, 1, 3);
        Map client = new(width: 10, depth: 10);
        Assert.True(client.AddCube(new Vector3(3f, 1f, 3f), withPhysics: false, materialId: GeometryMaterialId.Grass));

        RemoteWorldMutationApplier.ApplyWorldMutations(
            client,
            [new WorldMutationDelta(WorldMutationKind.CubeFractured, new Vector3(3f, 1f, 3f), MaterialId: GeometryMaterialId.Rock)]);

        GeometryCellRecord clientRubble = Assert.IsType<GeometryCellRecord>(client.TryGetRubbleAtCell(cell));
        Assert.Equal(GeometryMaterialId.Rock, clientRubble.Definition.MaterialId);
        Assert.Equal(CubeFracturePattern.CreateFirstPassRubble(cell), clientRubble.FracturePieces);
    }

    [Fact]
    public void FractureWithPiecesButNoMaterial_KeepsPiecesAndLocalCubeMaterial()
    {
        GeometryCellKey cell = new(3, 1, 3);
        GeometryFracturePiece[] serverPieces =
        [
            new(new Vector3(3.2f, 1.1f, 3.2f), 0.2f),
            new(new Vector3(3.7f, 1.1f, 3.7f), 0.18f)
        ];
        Map client = new(width: 10, depth: 10);
        Assert.True(client.AddCube(new Vector3(3f, 1f, 3f), withPhysics: false, materialId: GeometryMaterialId.Stone));

        RemoteWorldMutationApplier.ApplyWorldMutations(
            client,
            [new WorldMutationDelta(WorldMutationKind.CubeFractured, new Vector3(3f, 1f, 3f), FracturePieces: serverPieces)]);

        GeometryCellRecord clientRubble = Assert.IsType<GeometryCellRecord>(client.TryGetRubbleAtCell(cell));
        Assert.Equal(serverPieces, clientRubble.FracturePieces);
        Assert.Equal(GeometryMaterialId.Stone, clientRubble.Definition.MaterialId);
        Assert.Null(client.TryGetCubeAtCell(3, 1, 3));
    }

    [Fact]
    public void LegacySnapshotFracturedCell_KeepsLocalCubeMaterialAndServerPieces()
    {
        GeometryCellKey cell = new(3, 1, 3);
        Map server = new(width: 10, depth: 10);
        Assert.True(server.AddCube(new Vector3(3f, 1f, 3f), withPhysics: false, materialId: GeometryMaterialId.Stone));
        Assert.True(server.FractureCubeIntoPieces(cell));

        Map client = new(width: 10, depth: 10);
        Assert.True(client.AddCube(new Vector3(3f, 1f, 3f), withPhysics: false, materialId: GeometryMaterialId.Stone));
        WorldSnapshot snapshot = new(
            Width: 10,
            Height: client.Height,
            Depth: 10,
            PlacedCubeCells: Array.Empty<Vector3>(),
            FracturedCubeCells: [new Vector3(3f, 1f, 3f)]);

        RemoteWorldMutationApplier.ApplyWorldSnapshot(client, snapshot);

        GeometryCellRecord clientRubble = Assert.IsType<GeometryCellRecord>(client.TryGetRubbleAtCell(cell));
        Assert.Equal(GeometryMaterialId.Stone, clientRubble.Definition.MaterialId);
        Assert.Equal(server.TryGetRubbleAtCell(cell)!.FracturePieces, clientRubble.FracturePieces);
    }

    private static GeometryFracturePiece[] ToLocalPieces(GeometryCellKey cell, IReadOnlyList<GeometryFracturePiece> pieces)
    {
        Vector3 origin = new(cell.X, cell.Y, cell.Z);
        return pieces
            .Select(piece => piece with { Center = piece.Center - origin })
            .ToArray();
    }
}
