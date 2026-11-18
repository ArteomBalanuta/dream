using OpenTK.Mathematics;
using Xunit;
using Dream.Client.Replication;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;

namespace Dream.Tests.BugHunt;

// BH-BUILD-3. A join or resync used to destroy a cube built over unmoved rubble: the snapshot
// listed the cell twice (a placed cube and the rubble), the rubble carried no pieces because
// unmoved rubble goes on the wire as "the cell's first-pass rubble", and the fallback fracture that
// rebuilds those pieces locally also fractures the cell, which removes the cube the placed-cube
// loop had just rebuilt. The client was left with less than the snapshot described, and it stayed
// that way until the player rebuilt.
public class Cli3RubbleWithoutPiecesTests
{
    private static readonly Vector3 Cell = new(3f, 1f, 3f);

    [Fact]
    public void ApplyWorldSnapshot_KeepsAPlacedCube_WhenTheSameCellsRubbleArrivesWithoutPieces()
    {
        Map map = new(width: 10, depth: 10);
        PlacedCubeSnapshot placed = new(Cell, new Vector3(3.5f, 1.5f, 3.5f), GeometryMaterialId.Dirt);
        WorldSnapshot snapshot = Snapshot([placed]);

        RemoteWorldMutationApplier.ApplyWorldSnapshot(map, snapshot);

        // The cube the snapshot explicitly listed must survive the rubble in the same cell.
        Assert.NotNull(map.TryGetCubeAtCell(3, 1, 3));
        Assert.Equal(GeometryMaterialId.Dirt, map.TryGetGeometryCellAtCell(3, 1, 3)?.Definition.MaterialId);
        Assert.NotNull(map.TryGetRubbleAtCell(new GeometryCellKey(3, 1, 3)));

        // A second resync of the same world is just as stable.
        RemoteWorldMutationApplier.ApplyWorldSnapshot(map, snapshot);
        Assert.NotNull(map.TryGetCubeAtCell(3, 1, 3));
        Assert.NotNull(map.TryGetRubbleAtCell(new GeometryCellKey(3, 1, 3)));
    }

    [Fact]
    public void ApplyWorldSnapshot_RebuildsTheSameFirstPassRubble_AsTheFallbackFracture()
    {
        // The rubble beside the kept cube must be the rubble every other peer has, or the world
        // checksum would never match and resyncs would never heal the cell.
        Map withCube = new(width: 10, depth: 10);
        Map withoutCube = new(width: 10, depth: 10);

        RemoteWorldMutationApplier.ApplyWorldSnapshot(
            withCube,
            Snapshot([new PlacedCubeSnapshot(Cell, new Vector3(3.5f, 1.5f, 3.5f), GeometryMaterialId.Stone)]));
        RemoteWorldMutationApplier.ApplyWorldSnapshot(withoutCube, Snapshot([]));

        Assert.NotNull(withCube.TryGetCubeAtCell(3, 1, 3));
        Assert.Null(withoutCube.TryGetCubeAtCell(3, 1, 3));
        Assert.Equal(
            withoutCube.CaptureFracturePieces().Select(piece => (piece.Cell, piece.Piece)).ToArray(),
            withCube.CaptureFracturePieces().Select(piece => (piece.Cell, piece.Piece)).ToArray());
        Assert.NotEmpty(withCube.CaptureFracturePieces());
    }

    // Control, the fallback's real purpose: a cell the snapshot lists as rubble and NOT as a placed
    // cube still loses its local intact cube.
    [Fact]
    public void ApplyWorldSnapshot_StillFracturesACellWithNoPlacedCube()
    {
        Map map = new(width: 10, depth: 10);
        Assert.NotNull(map.TryAddCube(Cell, withPhysics: false, materialId: GeometryMaterialId.Stone));

        RemoteWorldMutationApplier.ApplyWorldSnapshot(map, Snapshot([]));

        Assert.Null(map.TryGetCubeAtCell(3, 1, 3));
        Assert.NotNull(map.TryGetRubbleAtCell(new GeometryCellKey(3, 1, 3)));
    }

    // Control, the other direction: the delta path is deliberately asymmetric. A CubeFractured
    // delta without pieces is a single event saying this cell's cube just fractured, so it must
    // still remove the cube.
    [Fact]
    public void ApplyWorldMutations_FracturedWithoutPieces_StillRemovesTheCubeInThatCell()
    {
        Map map = new(width: 10, depth: 10);
        Assert.NotNull(map.TryAddCube(Cell, withPhysics: false, materialId: GeometryMaterialId.Stone));

        RemoteWorldMutationApplier.ApplyWorldMutations(
            map,
            [
                new WorldMutationDelta(
                    WorldMutationKind.CubeFractured,
                    Cell,
                    MaterialId: GeometryMaterialId.Stone,
                    Sequence: 1UL)
            ]);

        Assert.Null(map.TryGetCubeAtCell(3, 1, 3));
        Assert.NotNull(map.TryGetRubbleAtCell(new GeometryCellKey(3, 1, 3)));
    }

    private static WorldSnapshot Snapshot(PlacedCubeSnapshot[] placedCubes)
    {
        return new WorldSnapshot(
            10,
            8,
            10,
            placedCubes.Select(cube => cube.CellPosition).ToArray(),
            PlacedCubes: placedCubes.Length > 0 ? placedCubes : null,
            FracturedCubes:
            [
                // Unmoved rubble as the wire carries it: no pieces, the client regenerates them.
                new FracturedCubeSnapshot(Cell, GeometryMaterialId.Stone, Array.Empty<GeometryFracturePiece>())
            ]);
    }
}
