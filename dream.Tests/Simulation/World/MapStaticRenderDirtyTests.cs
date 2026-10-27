using OpenTK.Mathematics;
using Xunit;
using Dream.Rendering.World;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;

namespace Dream.Tests.Simulation.World;

// Dynamic cubes and fracture pieces are not part of static chunk meshes. Marking their cells
// static-dirty forced a full static extract and chunk rebuild on every frame a cube was pushed.
public class MapStaticRenderDirtyTests
{
    private static readonly Vector3 ExportCenter = new(5f, 1.5f, 5f);
    private const float ExportRadius = 32f;

    [Fact]
    public void DynamicCubeCrossingCells_IsRenderDirtyButNotStaticDirty()
    {
        Map map = new(physics: null, width: 20, depth: 20);
        Cube cube = Assert.IsType<Cube>(map.TryAddCube(new Vector3(3f, 1f, 3f), withPhysics: false));
        DrainExports(map);

        map.UpdateCubePosition(cube, new Vector3(4.1f, 1.5f, 3.5f));
        GeometryRenderExport export = map.CaptureGeometryRenderExport(ExportCenter, ExportRadius);

        Assert.Contains(new GeometryCellKey(3, 1, 3), export.DirtyCells);
        Assert.Contains(new GeometryCellKey(4, 1, 3), export.DirtyCells);
        Assert.Empty(export.StaticDirtyCells);
    }

    [Fact]
    public void FracturePieceMovement_IsNotStaticDirty()
    {
        Map map = new(physics: null, width: 10, depth: 10);
        map.AddCube(new Vector3(3f, 1f, 3f), withPhysics: false);
        GeometryCellKey cell = new(3, 1, 3);
        map.FractureCubeIntoPieces(cell);
        DrainExports(map);
        GeometryFracturePiece piece = map.TryGetRubbleAtCell(cell)!.FracturePieces![0];

        Assert.True(map.UpdateFracturePiecePosition(cell, 0, piece.Center + new Vector3(0.3f, 0f, 0.2f)));
        GeometryRenderExport export = map.CaptureGeometryRenderExport(ExportCenter, ExportRadius);

        Assert.Contains(cell, export.DirtyCells);
        Assert.DoesNotContain(cell, export.StaticDirtyCells);
    }

    [Fact]
    public void RemovingStaticCube_IsStaticDirty()
    {
        Map map = new(physics: null, width: 10, depth: 10);
        Cube cube = Assert.IsType<Cube>(map.TryAddCube(new Vector3(3f, 1f, 3f), isStatic: true, withPhysics: false));
        DrainExports(map);

        Assert.True(map.RemoveCube(cube));
        GeometryRenderExport export = map.CaptureGeometryRenderExport(ExportCenter, ExportRadius);

        Assert.Contains(new GeometryCellKey(3, 1, 3), export.StaticDirtyCells);
    }

    [Fact]
    public void SubdividingPlacedCube_IsStaticDirty()
    {
        // Subdivided, unfractured cells export their blocks in the static Blocks list.
        Map map = new(physics: null, width: 10, depth: 10);
        map.AddCube(new Vector3(3f, 1f, 3f), withPhysics: false);
        DrainExports(map);

        map.ApplyGeometrySubcellSolid(new GeometryCellKey(3, 1, 3), new GeometrySubcellKey(0, 0, 0), isSolid: false);
        GeometryRenderExport export = map.CaptureGeometryRenderExport(ExportCenter, ExportRadius);

        Assert.Contains(new GeometryCellKey(3, 1, 3), export.StaticDirtyCells);
    }

    [Fact]
    public void PushingDynamicCube_KeepsStaticChunkExportSignatureStable()
    {
        Map map = new(physics: null, generateStartupTerrain: true, width: 20, depth: 20);
        Cube cube = Assert.IsType<Cube>(map.TryAddCube(FindOpenCell(map), withPhysics: false));
        Assert.True(map.TryGetCellForCube(cube, out GeometryCellKey startCell));
        DrainExports(map);
        int restingSignature = StaticWorldChunkRenderer.CalculateExportSignature(
            map.CapturePresentationGeometryRenderExport(ExportCenter, ExportRadius));

        for (int step = 1; step <= 5; step++)
        {
            map.UpdateCubePosition(cube, cube.Position + new Vector3(0.3f, 0f, 0f));
            int pushedSignature = StaticWorldChunkRenderer.CalculateExportSignature(
                map.CapturePresentationGeometryRenderExport(ExportCenter, ExportRadius));

            Assert.Equal(restingSignature, pushedSignature);
        }

        // The push must actually cross cell boundaries for the check above to mean anything.
        Assert.True(map.TryGetCellForCube(cube, out GeometryCellKey finalCell));
        Assert.NotEqual(startCell.X, finalCell.X);
    }

    private static void DrainExports(Map map)
    {
        _ = map.CaptureGeometryRenderExport(ExportCenter, ExportRadius);
        _ = map.CaptureGeometryCollisionExport();
    }

    private static Vector3 FindOpenCell(Map map)
    {
        for (int x = 1; x < map.Width - 4; x++)
        {
            for (int z = 1; z < map.Depth - 1; z++)
            {
                bool open = Enumerable.Range(0, 4).All(offset =>
                    map.TryGetGeometryCellAtCell(x + offset, 1, z) == null);
                if (open)
                {
                    return new Vector3(x, 1f, z);
                }
            }
        }

        throw new InvalidOperationException("No open run of cells found in the startup world.");
    }
}
