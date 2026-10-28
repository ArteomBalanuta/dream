using OpenTK.Mathematics;
using Xunit;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using static Dream.Tests.Simulation.World.PresentationExportCacheAssertions;

namespace Dream.Tests.Simulation.World;

// PresentationExportCacheTests' static geometry edits: subdivided, sculpted and fractured cells
// must invalidate the cached static part. These keep the startup floor size, so the merged
// sculpture cells exist and are compared against the uncached export after every edit.
public class PresentationExportCacheStaticEditTests
{
    [Fact]
    public void SubdividingPlacedAndGroundCells_MatchesUncachedExport()
    {
        Map map = CreateStartupTerrainMap();
        Vector3 placed = FindOpenCell(map, 4, 4);
        map.AddCube(placed, withPhysics: false);
        GeometryCellKey placedKey = new((int)placed.X, 1, (int)placed.Z);
        GeometryCellKey groundKey = new(2, 0, 2);

        AssertEquivalentAfter(map, () => map.ApplyGeometrySubcellSolid(placedKey, new GeometrySubcellKey(0, 0, 0), isSolid: false));
        AssertEquivalentAfter(map, () => map.ApplyGeometrySubcellSolid(placedKey, new GeometrySubcellKey(1, 1, 1), isSolid: false));
        AssertEquivalentAfter(map, () => map.ApplyGeometrySubcellSolid(groundKey, new GeometrySubcellKey(0, 0, 0), isSolid: false));
        AssertEquivalentAfter(map, () => Assert.True(map.RestoreSolidCubeCell(placedKey)));
    }

    [Fact]
    public void ChangingMergedSculptureSubcells_MatchesUncachedExport()
    {
        Map map = CreateStartupTerrainMap();
        GeometryCellRecord sculpture = map.GeometryCells.Values.First(record => record.SubdivisionState != null && record.Key.Y > 0);
        long version = map.StaticContentVersion;

        AssertEquivalentAfter(map, () => map.ApplyGeometrySubcellSolid(sculpture.Key, new GeometrySubcellKey(0, 0, 0), isSolid: false));
        Assert.NotEqual(version, map.StaticContentVersion);
        AssertEquivalentAfter(map, () => map.ApplyGeometrySubcellSolid(sculpture.Key, new GeometrySubcellKey(0, 0, 0), isSolid: true));
        AssertEquivalentAfter(map, () => Assert.True(map.RestoreGeneratedStartupCell(sculpture.Key)));
    }

    [Fact]
    public void FractureAndRubble_MatchesUncachedExport()
    {
        Map map = CreateStartupTerrainMap();
        Vector3 dynamicCell = FindOpenCell(map, 4, 4);
        Vector3 staticCell = FindOpenCell(map, 6, 6);
        map.AddCube(dynamicCell, withPhysics: false);
        map.AddCube(staticCell, isStatic: true, withPhysics: false);
        GeometryCellKey dynamicKey = new((int)dynamicCell.X, 1, (int)dynamicCell.Z);
        GeometryCellKey staticKey = new((int)staticCell.X, 1, (int)staticCell.Z);

        AssertEquivalentAfter(map, () => Assert.True(map.FractureCubeIntoPieces(dynamicKey)));
        AssertEquivalentAfter(map, () => Assert.True(map.FractureCubeIntoPieces(staticKey)));
        GeometryFracturePiece piece = map.TryGetRubbleAtCell(dynamicKey)!.FracturePieces![0];
        AssertEquivalentAfter(map, () => Assert.True(map.UpdateFracturePiecePosition(dynamicKey, 0, piece.Center + new Vector3(0f, 0f, 20f))));
        AssertEquivalentAfter(map, () => Assert.True(map.RemoveRubble(staticKey)));
        AssertEquivalentAfter(map, () => Assert.True(map.RestoreFracturePieces(
            staticKey,
            GeometryMaterialId.Sand,
            CubeFracturePattern.CreateFirstPassRubble(staticKey),
            withPhysics: false)));
    }
}
