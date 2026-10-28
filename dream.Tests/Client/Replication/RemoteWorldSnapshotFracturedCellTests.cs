using OpenTK.Mathematics;
using Xunit;
using Dream.Client.Replication;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;

namespace Dream.Tests.Client.Replication;

// A full world that lists a cell as fractured decides whether that cell keeps a cube: only when
// the same world also lists a placed cube there (a cube built over old rubble).
public class RemoteWorldSnapshotFracturedCellTests
{
    private static readonly GeometryFracturePiece[] Pieces =
    [
        new(new Vector3(3.2f, 1.2f, 3.2f), 0.2f),
        new(new Vector3(3.7f, 1.3f, 3.65f), 0.24f)
    ];

    [Fact]
    public void ApplyWorldSnapshot_RemovesTheLocalIntactCube_AtACellTheSnapshotListsAsFractured()
    {
        Map map = new(width: 10, depth: 10);
        Assert.NotNull(map.TryAddCube(new Vector3(3f, 1f, 3f), withPhysics: false, materialId: GeometryMaterialId.Stone));

        RemoteWorldMutationApplier.ApplyWorldSnapshot(map, FracturedWorld(placedCubes: []));

        Assert.Null(map.TryGetCubeAtCell(3, 1, 3));
        Assert.NotNull(map.TryGetRubbleAtCell(new GeometryCellKey(3, 1, 3)));

        // A resync of the same world keeps it that way.
        RemoteWorldMutationApplier.ApplyWorldSnapshot(map, FracturedWorld(placedCubes: []));
        Assert.Null(map.TryGetCubeAtCell(3, 1, 3));
        Assert.NotNull(map.TryGetRubbleAtCell(new GeometryCellKey(3, 1, 3)));
    }

    [Fact]
    public void ApplyWorldSnapshot_KeepsAPlacedCube_BuiltOverOldRubble()
    {
        Map map = new(width: 10, depth: 10);
        PlacedCubeSnapshot placed = new(
            new Vector3(3f, 1f, 3f),
            new Vector3(3.5f, 1.5f, 3.5f),
            GeometryMaterialId.Dirt);

        RemoteWorldMutationApplier.ApplyWorldSnapshot(map, FracturedWorld(placedCubes: [placed]));
        RemoteWorldMutationApplier.ApplyWorldSnapshot(map, FracturedWorld(placedCubes: [placed]));

        Assert.NotNull(map.TryGetCubeAtCell(3, 1, 3));
        Assert.Equal(GeometryMaterialId.Dirt, map.TryGetGeometryCellAtCell(3, 1, 3)?.Definition.MaterialId);
        Assert.NotNull(map.TryGetRubbleAtCell(new GeometryCellKey(3, 1, 3)));
    }

    [Fact]
    public void ApplyWorldSnapshot_FallbackFracture_OfALocalIntactCube_IsDeterministic()
    {
        // Without pieces the client fractures the cell itself; rubble is seeded by cell, so two
        // clients (one that had the cube, one that did not) build the same rubble and neither
        // keeps the cube.
        Map withCube = new(width: 10, depth: 10);
        Assert.NotNull(withCube.TryAddCube(new Vector3(3f, 1f, 3f), withPhysics: false));
        Map withoutCube = new(width: 10, depth: 10);
        WorldSnapshot snapshot = new(
            10,
            8,
            10,
            Array.Empty<Vector3>(),
            FracturedCubes:
            [
                new FracturedCubeSnapshot(new Vector3(3f, 1f, 3f), GeometryMaterialId.Grass, Array.Empty<GeometryFracturePiece>())
            ]);

        RemoteWorldMutationApplier.ApplyWorldSnapshot(withCube, snapshot);
        RemoteWorldMutationApplier.ApplyWorldSnapshot(withoutCube, snapshot);

        Assert.Null(withCube.TryGetCubeAtCell(3, 1, 3));
        Assert.Null(withoutCube.TryGetCubeAtCell(3, 1, 3));
        Assert.NotNull(withCube.TryGetRubbleAtCell(new GeometryCellKey(3, 1, 3)));
        Assert.Equal(
            withoutCube.CaptureFracturePieces().Select(piece => (piece.Cell, piece.Piece)),
            withCube.CaptureFracturePieces().Select(piece => (piece.Cell, piece.Piece)));
    }

    private static WorldSnapshot FracturedWorld(PlacedCubeSnapshot[] placedCubes)
    {
        return new WorldSnapshot(
            10,
            8,
            10,
            placedCubes.Select(cube => cube.CellPosition).ToArray(),
            FracturedCubes:
            [
                new FracturedCubeSnapshot(new Vector3(3f, 1f, 3f), GeometryMaterialId.Stone, Pieces)
            ],
            PlacedCubes: placedCubes.Length > 0 ? placedCubes : null);
    }
}
