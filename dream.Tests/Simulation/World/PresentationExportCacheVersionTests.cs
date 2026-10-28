using OpenTK.Mathematics;
using Xunit;
using Dream.Rendering.World;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using static Dream.Tests.Simulation.World.PresentationExportCacheAssertions;

namespace Dream.Tests.Simulation.World;

// PresentationExportCacheTests' content version and export signature checks. None of them is
// about sculptures, so they use a smaller generated terrain (still enriched, with landmarks)
// that the world generator builds without the merged sculpture cells.
public class PresentationExportCacheVersionTests
{
    [Fact]
    public void StaticContentVersion_IsStableWithoutStaticChanges()
    {
        Map map = CreateTerrainMap(SmallTerrainSize);
        Cube cube = Assert.IsType<Cube>(map.TryAddCube(FindOpenCell(map, 4, 4, run: 4), withPhysics: false));
        long version = map.StaticContentVersion;

        GeometryRenderExport first = Capture(map, LocalCenter, LocalRadius);
        GeometryRenderExport second = Capture(map, LocalCenter, LocalRadius);
        map.UpdateCubePosition(cube, cube.Position + new Vector3(0.3f, 0f, 0f));
        map.UpdateCubePosition(cube, cube.Position + new Vector3(1f, 0f, 0f));
        GeometryRenderExport moved = Capture(map, LocalCenter, LocalRadius);

        Assert.Equal(version, map.StaticContentVersion);
        Assert.Equal(version, first.StaticContentVersion);
        Assert.Equal(version, second.StaticContentVersion);
        Assert.Equal(version, moved.StaticContentVersion);
        Assert.Same(first.CompatibilityCubes, moved.CompatibilityCubes);
        Assert.Same(first.Blocks, moved.Blocks);
    }

    [Fact]
    public void StaticContentVersion_ChangesAfterStaticChanges()
    {
        Map map = CreateTerrainMap(SmallTerrainSize);
        long initial = map.StaticContentVersion;
        Cube cube = Assert.IsType<Cube>(map.TryAddCube(FindOpenCell(map, 4, 4, run: 2), isStatic: true, withPhysics: false));
        long afterAdd = map.StaticContentVersion;
        map.UpdateCubePosition(cube, cube.Position + new Vector3(0.2f, 0f, 0f), syncPhysics: false);
        long afterInCellMove = map.StaticContentVersion;
        map.RemoveCube(cube);
        long afterRemove = map.StaticContentVersion;

        Assert.NotEqual(initial, afterAdd);
        Assert.NotEqual(afterAdd, afterInCellMove);
        Assert.NotEqual(afterInCellMove, afterRemove);
    }

    [Fact]
    public void StaticContentVersion_DiffersBetweenMaps()
    {
        Map first = CreateTerrainMap(SmallTerrainSize);
        Map second = CreateTerrainMap(SmallTerrainSize);

        Assert.NotEqual(0, first.StaticContentVersion);
        Assert.NotEqual(first.StaticContentVersion, second.StaticContentVersion);
    }

    [Fact]
    public void ExportSignature_IsStableForUnchangedFrames_AndChangesWithStaticContent()
    {
        Map map = CreateTerrainMap(SmallTerrainSize);
        Capture(map, LocalCenter, LocalRadius);

        int first = StaticWorldChunkRenderer.CalculateExportSignature(Capture(map, LocalCenter, LocalRadius));
        int second = StaticWorldChunkRenderer.CalculateExportSignature(Capture(map, LocalCenter, LocalRadius));
        map.AddCube(FindOpenCell(map, 4, 4), isStatic: true, withPhysics: false);
        GeometryRenderExport changedExport = Capture(map, LocalCenter, LocalRadius);
        int changed = StaticWorldChunkRenderer.CalculateExportSignature(changedExport);
        int settled = StaticWorldChunkRenderer.CalculateExportSignature(Capture(map, LocalCenter, LocalRadius));

        Assert.Equal(first, second);
        Assert.NotEmpty(changedExport.StaticDirtyCells);
        Assert.NotEqual(first, changed);
        Assert.NotEqual(first, settled);
    }

    [Fact]
    public void ExportSignature_DoesNotDependOnStaticDirtyCellOrder()
    {
        GeometryRenderExport export = Capture(CreateTerrainMap(SmallTerrainSize), LocalCenter, LocalRadius) with
        {
            StaticDirtyCells = [new GeometryCellKey(1, 1, 1), new GeometryCellKey(2, 1, 1)]
        };
        GeometryRenderExport reordered = export with
        {
            StaticDirtyCells = [new GeometryCellKey(2, 1, 1), new GeometryCellKey(1, 1, 1)]
        };

        Assert.Equal(
            StaticWorldChunkRenderer.CalculateExportSignature(export),
            StaticWorldChunkRenderer.CalculateExportSignature(reordered));
    }

    [Fact]
    public void ExportSignature_WithoutVersion_StillHashesContent()
    {
        Map map = CreateTerrainMap(SmallTerrainSize);
        Capture(map, LocalCenter, LocalRadius);
        GeometryRenderExport before = Capture(map, LocalCenter, LocalRadius) with { StaticContentVersion = 0 };
        GeometryRenderExport beforeAgain = Capture(map, LocalCenter, LocalRadius) with { StaticContentVersion = 0 };
        map.AddCube(FindOpenCell(map, 4, 4), isStatic: true, withPhysics: false);
        GeometryRenderExport after = Capture(map, LocalCenter, LocalRadius) with
        {
            StaticContentVersion = 0,
            StaticDirtyCells = []
        };

        Assert.Equal(
            StaticWorldChunkRenderer.CalculateExportSignature(before),
            StaticWorldChunkRenderer.CalculateExportSignature(beforeAgain));
        Assert.NotEqual(
            StaticWorldChunkRenderer.CalculateExportSignature(before),
            StaticWorldChunkRenderer.CalculateExportSignature(after));
    }

    [Fact]
    public void UncachedExports_KeepUnknownVersion()
    {
        Map map = CreateTerrainMap(SmallTerrainSize);

        Assert.Equal(0, map.CaptureFullGeometryRenderExport().StaticContentVersion);
        Assert.Equal(0, map.CaptureGeometryRenderExport(LocalCenter, LocalRadius).StaticContentVersion);
    }
}
