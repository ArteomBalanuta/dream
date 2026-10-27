using Dream.Simulation.Determinism;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Simulation.World.Geometry;

public class MapGeometryFacadeTests
{
    [Fact]
    public void Map_Initializes_Floor_As_Geometry_Records()
    {
        Map map = new Map(width: 24, height: 8, depth: 24, generateStartupTerrain: true);

        Assert.True(map.GeometryCells.Count >= 24 * 24);
        GeometryCellRecord floorRecord = Assert.IsType<GeometryCellRecord>(map.TryGetGeometryCellAtCell(0, 0, 0));
        Assert.True(floorRecord.Definition.IsFloor);
        Assert.Equal(GeometryShapeKind.SolidCube, floorRecord.Definition.ShapeKind);
        Assert.Equal(GeometryMaterialId.Stone, floorRecord.Definition.MaterialId);
    }

    [Fact]
    public void AddCube_Adds_Geometry_Record_Alongside_Compatibility_Cube()
    {
        Map map = new Map(width: 8, height: 8, depth: 8);

        Cube cube = Assert.IsType<Cube>(map.TryAddCube(new Vector3(3f, 1f, 3f), withPhysics: false));
        GeometryCellRecord geometryCell = Assert.IsType<GeometryCellRecord>(map.TryGetGeometryCellAtCell(3, 1, 3));

        Assert.False(geometryCell.Definition.IsFloor);
        Assert.Equal(GeometryShapeKind.SolidCube, geometryCell.Definition.ShapeKind);
        Assert.Equal(new GeometryCellKey(3, 1, 3), geometryCell.Key);
        Assert.Equal(new Vector3(3.5f, 1.5f, 3.5f), cube.Position);
    }

    [Fact]
    public void ApplyGeometrySubcellSolid_Activates_Subdivision_Without_Adding_Neighbor_Cubes()
    {
        Map map = new Map(width: 8, height: 8, depth: 8);
        map.AddCube(new Vector3(3f, 1f, 3f), withPhysics: false);
        int initialSolidCount = map.SolidCubes.Count;

        GeometryMutationResult result = map.ApplyGeometrySubcellSolid(
            new GeometryCellKey(3, 1, 3),
            new GeometrySubcellKey(1, 1, 1),
            isSolid: false);

        GeometryCellRecord geometryCell = Assert.IsType<GeometryCellRecord>(map.TryGetGeometryCellAtCell(3, 1, 3));
        Assert.True(result.SubdivisionActivated);
        Assert.True(geometryCell.SubdivisionState != null);
        Assert.Equal(initialSolidCount, map.SolidCubes.Count);
        Assert.Null(map.Cubes[3, 2, 3]);
    }

    [Fact]
    public void CaptureGeometryRenderExport_Uses_Nearby_Compatibility_Cubes()
    {
        Map map = new Map(width: 20, height: 8, depth: 20);
        map.AddCube(new Vector3(3f, 1f, 3f), withPhysics: false);
        map.AddCube(new Vector3(18f, 1f, 18f), withPhysics: false);

        GeometryRenderExport export = map.CaptureGeometryRenderExport(new Vector3(2f, 1.5f, 2f), 8f);

        Assert.Contains(export.Cells, cell => cell.Key == new GeometryCellKey(3, 1, 3));
        Assert.DoesNotContain(export.Cells, cell => cell.Key == new GeometryCellKey(18, 1, 18));
        Assert.Contains(export.DynamicCompatibilityCubes, cube => cube.Position == new Vector3(3.5f, 1.5f, 3.5f));
    }

    [Fact]
    public void CaptureGeometryCollisionExport_Includes_Merged_Floor_And_Placed_Cube_Bodies()
    {
        Map map = new Map(width: 8, height: 8, depth: 8);
        map.AddCube(new Vector3(3f, 1f, 3f), withPhysics: false);

        GeometryCollisionExport export = map.CaptureGeometryCollisionExport();

        Assert.Contains(export.Bodies, body => body.Kind == GeometryCollisionBodyKind.Floor);
        Assert.Contains(export.Bodies, body => body.Kind == GeometryCollisionBodyKind.Cell && body.CellKey == new GeometryCellKey(3, 1, 3));
    }

    [Fact]
    public void CaptureGeometryRenderExport_ForSubdividedCell_UsesRefinedBlocksInsteadOfCoarseCompatibilityCube()
    {
        Map map = new Map(width: 8, height: 8, depth: 8);
        map.AddCube(new Vector3(3f, 1f, 3f), withPhysics: false);

        map.ApplyGeometrySubcellSolid(
            new GeometryCellKey(3, 1, 3),
            new GeometrySubcellKey(0, 0, 0),
            isSolid: false);

        GeometryRenderExport export = map.CaptureGeometryRenderExport(new Vector3(3f, 1.5f, 3f), 4f);

        Assert.DoesNotContain(export.CompatibilityCubes, cube => cube.Position == new Vector3(3.5f, 1.5f, 3.5f));
        Assert.DoesNotContain(export.DynamicCompatibilityCubes, cube => cube.Position == new Vector3(3.5f, 1.5f, 3.5f));
        Assert.Contains(export.Blocks, block => block.CellKey == new GeometryCellKey(3, 1, 3));
        Assert.DoesNotContain(export.Blocks, block => block.SubcellKey == new GeometrySubcellKey(0, 0, 0));
        Assert.Contains(new GeometryCellKey(3, 1, 3), export.DirtyCells);
    }

    [Fact]
    public void CaptureGeometryCollisionExport_ForSubdividedCell_UsesRefinedSubcellBodies()
    {
        Map map = new Map(width: 8, height: 8, depth: 8);
        map.AddCube(new Vector3(3f, 1f, 3f), withPhysics: false);

        map.ApplyGeometrySubcellSolid(
            new GeometryCellKey(3, 1, 3),
            new GeometrySubcellKey(0, 0, 0),
            isSolid: false);

        GeometryCollisionExport export = map.CaptureGeometryCollisionExport();

        Assert.DoesNotContain(export.Bodies, body => body.Kind == GeometryCollisionBodyKind.Cell && body.CellKey == new GeometryCellKey(3, 1, 3));
        Assert.Contains(export.Bodies, body => body.Kind == GeometryCollisionBodyKind.Subcell && body.CellKey == new GeometryCellKey(3, 1, 3));
        Assert.DoesNotContain(export.Bodies, body => body.SubcellKey == new GeometrySubcellKey(0, 0, 0));
        Assert.Contains(new GeometryCellKey(3, 1, 3), export.DirtyCells);
    }

    [Fact]
    public void FractureCubeIntoPieces_ReplacesCoarseCellWithSeparatedRubblePieces()
    {
        Map map = new Map(width: 8, height: 8, depth: 8);
        map.AddCube(new Vector3(3f, 1f, 3f), withPhysics: false);

        bool fractured = map.FractureCubeIntoPieces(new GeometryCellKey(3, 1, 3));

        // Rubble lives apart from cell occupancy: the fractured cell itself is released.
        GeometryCellRecord geometryCell = Assert.IsType<GeometryCellRecord>(map.TryGetRubbleAtCell(new GeometryCellKey(3, 1, 3)));
        Assert.Null(map.TryGetGeometryCellAtCell(3, 1, 3));
        Assert.Null(map.TryGetCubeAtCell(3, 1, 3));
        GeometryRenderExport export = map.CaptureGeometryRenderExport(new Vector3(3.5f, 1.5f, 3.5f), 4f);
        GeometryCollisionExport collision = map.CaptureGeometryCollisionExport();

        Assert.True(fractured);
        Assert.NotNull(geometryCell.SubdivisionState);
        Assert.DoesNotContain(export.CompatibilityCubes, cube => cube.Position == new Vector3(3.5f, 1.5f, 3.5f));
        Assert.DoesNotContain(export.DynamicCompatibilityCubes, cube => cube.Position == new Vector3(3.5f, 1.5f, 3.5f));
        Assert.DoesNotContain(export.Blocks, block => block.CellKey == new GeometryCellKey(3, 1, 3));
        Assert.InRange(export.DynamicBlocks.Count(block => block.CellKey == new GeometryCellKey(3, 1, 3)), 5, 7);
        Assert.Equal(
            export.DynamicBlocks.Count(block => block.CellKey == new GeometryCellKey(3, 1, 3)),
            collision.Bodies.Count(body => body.CellKey == new GeometryCellKey(3, 1, 3)));
        Assert.All(export.DynamicBlocks, block =>
        {
            Assert.InRange(block.SideLength, 0.14f, 0.28f);
            Assert.InRange((block.Center - new Vector3(3.5f, 1.5f, 3.5f)).Length, 0.05f, 0.75f);
        });

        GeometryRenderBlockDescriptor[] pieces = export.DynamicBlocks
            .Where(block => block.CellKey == new GeometryCellKey(3, 1, 3))
            .ToArray();
        for (int first = 0; first < pieces.Length; first++)
        {
            for (int second = first + 1; second < pieces.Length; second++)
            {
                float centerDistanceXz = new Vector2(
                    pieces[first].Center.X - pieces[second].Center.X,
                    pieces[first].Center.Z - pieces[second].Center.Z).Length;
                float minimumSeparatedDistance = ((pieces[first].SideLength + pieces[second].SideLength) * 0.5f) + 0.01f;
                Assert.True(
                    centerDistanceXz >= minimumSeparatedDistance,
                    $"Pieces {first} and {second} were packed too tightly: {centerDistanceXz} < {minimumSeparatedDistance}");
            }
        }
    }

    [Fact]
    public void CubeFracturePattern_UsesDifferentCentersForDifferentExplicitSeeds()
    {
        GeometryCellKey cell = new(3, 1, 3);

        IReadOnlyList<GeometryFracturePiece> first = CubeFracturePattern.CreateFirstPassRubble(cell, new DeterministicRandom(101));
        IReadOnlyList<GeometryFracturePiece> second = CubeFracturePattern.CreateFirstPassRubble(cell, new DeterministicRandom(202));

        Assert.NotEqual(
            first.Select(piece => piece.Center).ToArray(),
            second.Select(piece => piece.Center).ToArray());
    }

    [Fact]
    public void RestoreFracturePieces_RehydratesPersistedPiecesIntoRenderAndCollisionExports()
    {
        Map map = new Map(width: 8, height: 8, depth: 8);
        GeometryCellKey cell = new(3, 1, 3);
        GeometryFracturePiece[] pieces =
        [
            new(new Vector3(3.2f, 1.2f, 3.25f), 0.2f),
            new(new Vector3(3.72f, 1.18f, 3.68f), 0.18f)
        ];

        bool restored = map.RestoreFracturePieces(cell, GeometryMaterialId.Stone, pieces, withPhysics: false);

        GeometryRenderExport renderExport = map.CaptureGeometryRenderExport(new Vector3(3.5f, 1.5f, 3.5f), 4f);
        GeometryCollisionExport collisionExport = map.CaptureGeometryCollisionExport();

        Assert.True(restored);
        // Restored rubble no longer materializes a cube in its origin cell.
        Assert.Null(map.TryGetCubeAtCell(3, 1, 3));
        Assert.NotNull(map.TryGetRubbleAtCell(cell));
        Assert.DoesNotContain(renderExport.Blocks, block => block.CellKey == cell);
        Assert.Equal(2, renderExport.DynamicBlocks.Count(block => block.CellKey == cell));
        Assert.Equal(2, collisionExport.Bodies.Count(body => body.CellKey == cell));
        Assert.All(renderExport.DynamicBlocks.Where(block => block.CellKey == cell), block =>
        {
            Assert.Equal(GeometryMaterialId.Stone, block.MaterialId);
            Assert.Contains(pieces, piece => piece.Center == block.Center && piece.SideLength == block.SideLength);
        });
    }

    [Fact]
    public void RestoreFracturePieces_RejectsInvalidPiecesBeforeRenderOrCollisionExport()
    {
        Map map = new Map(width: 8, height: 8, depth: 8);
        GeometryCellKey cell = new(3, 1, 3);
        GeometryFracturePiece[] invalidPieces =
        [
            new(new Vector3(float.NaN, 1.2f, 3.2f), 0.2f),
            new(new Vector3(3.7f, 1.3f, 3.65f), 0f),
            new(new Vector3(3.7f, 1.3f, 3.65f), -0.2f),
            new(new Vector3(3.7f, 1.3f, 3.65f), 1.25f)
        ];

        bool restored = map.RestoreFracturePieces(cell, GeometryMaterialId.Stone, invalidPieces, withPhysics: false);

        Assert.False(restored);
        Assert.Null(map.TryGetGeometryCellAtCell(3, 1, 3));
        GeometryRenderExport renderExport = map.CaptureGeometryRenderExport(new Vector3(3.5f, 1.5f, 3.5f), 4f);
        GeometryCollisionExport collisionExport = map.CaptureGeometryCollisionExport();
        Assert.DoesNotContain(renderExport.DynamicBlocks, block => block.CellKey == cell);
        Assert.DoesNotContain(collisionExport.Bodies, body => body.CellKey == cell);
    }

    [Fact]
    public void FractureCubeIntoPieces_UsesSameCentersAndSizesForRenderAndCollisionPieces()
    {
        Map map = new Map(width: 8, height: 8, depth: 8);
        map.AddCube(new Vector3(3f, 1f, 3f), withPhysics: false);

        map.FractureCubeIntoPieces(new GeometryCellKey(3, 1, 3));

        GeometryRenderExport renderExport = map.CaptureGeometryRenderExport(new Vector3(3.5f, 1.5f, 3.5f), 4f);
        GeometryCollisionExport collisionExport = map.CaptureGeometryCollisionExport();

        Assert.Empty(renderExport.Blocks);
        Assert.InRange(renderExport.DynamicBlocks.Count, 5, 7);
        Assert.Equal(
            renderExport.DynamicBlocks.Count,
            collisionExport.Bodies.Count(body => body.Kind == GeometryCollisionBodyKind.Subcell));
        foreach (GeometryRenderBlockDescriptor block in renderExport.DynamicBlocks)
        {
            GeometryCollisionBodyDescriptor collisionBody = Assert.Single(
                collisionExport.Bodies,
                body => body.CellKey == block.CellKey &&
                    body.Center == block.Center &&
                    MathF.Abs((body.HalfExtents.X * 2f) - block.SideLength) < 0.0001f);

            Assert.Equal(block.SideLength * 0.5f, collisionBody.HalfExtents.X, 4);
            Assert.Equal(block.SideLength * 0.5f, collisionBody.HalfExtents.Y, 4);
            Assert.Equal(block.SideLength * 0.5f, collisionBody.HalfExtents.Z, 4);
        }
    }

    [Fact]
    public void CaptureGeometryRenderExport_Includes_Moved_FracturePieces_By_Piece_Position()
    {
        Map map = new Map(width: 10, height: 8, depth: 10);
        GeometryCellKey parentCell = new(2, 1, 2);
        map.AddCube(new Vector3(parentCell.X, parentCell.Y, parentCell.Z), withPhysics: false);
        Assert.True(map.FractureCubeIntoPieces(parentCell));

        Vector3 movedPieceCenter = new(7.25f, 1.25f, 7.25f);
        Assert.True(map.UpdateFracturePiecePosition(parentCell, pieceIndex: 0, movedPieceCenter));

        GeometryRenderExport renderExport = map.CaptureGeometryRenderExport(movedPieceCenter, radius: 1f);

        Assert.Contains(
            renderExport.DynamicBlocks,
            block => block.CellKey == parentCell && block.Center == movedPieceCenter);
    }

    [Fact]
    public void StartupSculpture_VisualFillScale_DoesNotInflateCollisionBodies()
    {
        Map map = new Map(width: 56, height: 8, depth: 56, generateStartupTerrain: true);

        GeometryRenderExport renderExport = map.CaptureGeometryRenderExport(new Vector3(18.5f, 2f, 16.5f), 8f);
        GeometryCollisionExport collisionExport = map.CaptureGeometryCollisionExport();
        GeometryRenderBlockDescriptor visualBlock = Assert.Single(
            renderExport.Blocks
                .Where(block => block.VisualFillScale > 1f)
                .Take(1));

        GeometryCollisionBodyDescriptor collisionBody = Assert.Single(
            collisionExport.Bodies,
            body =>
                body.Kind == GeometryCollisionBodyKind.Subcell &&
                ContainsPoint(body, visualBlock.Center));

        Assert.True(visualBlock.VisualSideLength > visualBlock.SideLength);
        AssertGridAlignedExtent(collisionBody.HalfExtents.X * 2f, visualBlock.SideLength);
        AssertGridAlignedExtent(collisionBody.HalfExtents.Y * 2f, visualBlock.SideLength);
        AssertGridAlignedExtent(collisionBody.HalfExtents.Z * 2f, visualBlock.SideLength);
    }

    [Fact]
    public void StartupSculpture_Merges_StaticSubcellCollisionBodies()
    {
        Map map = new Map(width: 56, height: 8, depth: 56, generateStartupTerrain: true);

        GeometryRenderExport renderExport = map.CaptureGeometryRenderExport(new Vector3(24f, 2f, 16f), WorldVisibility.MaxRenderDistance);
        GeometryCollisionExport collisionExport = map.CaptureGeometryCollisionExport();
        int sculptedRenderBlocks = renderExport.Blocks.Count(block => block.CellKey.Y > 0);
        int sculptedCollisionBodies = collisionExport.Bodies.Count(body =>
            body.Kind == GeometryCollisionBodyKind.Subcell &&
            body.CellKey.HasValue &&
            body.CellKey.Value.Y > 0);

        Assert.InRange(sculptedRenderBlocks, 4_000, 12_000);
        Assert.True(sculptedCollisionBodies < 12_000);
    }

    [Fact]
    public void StartupSculpture_Merges_StaticSubcellCollisionBodies_Across_Cell_Boundaries()
    {
        Map map = new Map(width: 56, height: 8, depth: 56, generateStartupTerrain: true);

        GeometryRenderExport renderExport = map.CaptureGeometryRenderExport(new Vector3(24f, 2f, 16f), WorldVisibility.MaxRenderDistance);
        GeometryCollisionExport collisionExport = map.CaptureGeometryCollisionExport();
        int sculptedRenderBlocks = renderExport.Blocks.Count(block => block.CellKey.Y > 0);
        int sculptedCollisionBodies = collisionExport.Bodies.Count(body =>
            body.Kind == GeometryCollisionBodyKind.Subcell &&
            body.CellKey.HasValue &&
            body.CellKey.Value.Y > 0);

        Assert.True(
            sculptedCollisionBodies <= sculptedRenderBlocks,
            $"Expected static collision merge to be at least as compact as render merge, but collision={sculptedCollisionBodies}, render={sculptedRenderBlocks}.");
    }

    [Fact]
    public void StartupSculpture_Uses_Coarse_StaticCollision_For_ResponsiveBootstrap()
    {
        Map map = new Map(width: 56, height: 8, depth: 56, generateStartupTerrain: true);

        GeometryCollisionExport collisionExport = map.CaptureGeometryCollisionExport();
        int sculptedCollisionBodies = collisionExport.Bodies.Count(body =>
            body.Kind == GeometryCollisionBodyKind.Subcell &&
            body.CellKey.HasValue &&
            body.CellKey.Value.Y > 0);

        Assert.True(
            sculptedCollisionBodies < 1_000,
            $"Expected static startup sculpture collision to stay bootstrap-cheap, but exported {sculptedCollisionBodies} subcell bodies.");
    }

    private static bool ContainsPoint(GeometryCollisionBodyDescriptor body, Vector3 point)
    {
        Vector3 delta = point - body.Center;
        return MathF.Abs(delta.X) <= body.HalfExtents.X + 0.0001f &&
            MathF.Abs(delta.Y) <= body.HalfExtents.Y + 0.0001f &&
            MathF.Abs(delta.Z) <= body.HalfExtents.Z + 0.0001f;
    }

    private static void AssertGridAlignedExtent(float extent, float sideLength)
    {
        float units = extent / sideLength;
        Assert.Equal(MathF.Round(units), units, precision: 3);
    }

    [Fact]
    public void FractureCubeIntoPieces_ProducesSameRubbleForSameCellOnEveryMap()
    {
        Map first = new Map(width: 8, height: 8, depth: 8);
        first.AddCube(new Vector3(3f, 1f, 3f), withPhysics: false);
        first.FractureCubeIntoPieces(new GeometryCellKey(3, 1, 3));

        Map second = new Map(width: 8, height: 8, depth: 8);
        second.AddCube(new Vector3(3f, 1f, 3f), withPhysics: false);
        second.FractureCubeIntoPieces(new GeometryCellKey(3, 1, 3));

        GeometryRenderExport firstExport = first.CaptureGeometryRenderExport(new Vector3(3.5f, 1.5f, 3.5f), 4f);
        GeometryRenderExport secondExport = second.CaptureGeometryRenderExport(new Vector3(3.5f, 1.5f, 3.5f), 4f);

        Assert.Equal(
            firstExport.DynamicBlocks
                .OrderBy(block => block.Center.X)
                .ThenBy(block => block.Center.Y)
                .ThenBy(block => block.Center.Z)
                .Select(block => (block.Center, block.SideLength))
                .ToArray(),
            secondExport.DynamicBlocks
                .OrderBy(block => block.Center.X)
                .ThenBy(block => block.Center.Y)
                .ThenBy(block => block.Center.Z)
                .Select(block => (block.Center, block.SideLength))
                .ToArray());
    }

    [Fact]
    public void FractureCubeIntoPieces_ExportsDynamicCollisionPieces()
    {
        Map map = new Map(width: 8, height: 8, depth: 8);
        map.AddCube(new Vector3(3f, 1f, 3f), withPhysics: false);

        map.FractureCubeIntoPieces(new GeometryCellKey(3, 1, 3));

        GeometryCollisionExport collisionExport = map.CaptureGeometryCollisionExport();

        Assert.All(
            collisionExport.Bodies.Where(body => body.CellKey == new GeometryCellKey(3, 1, 3)),
            body =>
            {
                Assert.False(body.IsStatic);
                Assert.NotNull(body.FracturePieceIndex);
            });
    }
}
