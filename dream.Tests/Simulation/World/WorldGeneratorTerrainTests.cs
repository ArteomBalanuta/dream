using System.Linq;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Simulation.World;

public class WorldGeneratorTerrainTests
{
    [Fact]
    public void StartupWorld_Uses_More_Than_One_Terrain_Material()
    {
        Map map = new(width: 56, height: 8, depth: 56, loadVisualAssets: false, generateStartupTerrain: true);

        GeometryMaterialId[] materials = map.GeometryCells.Values
            .Where(cell => cell.Key.Y == 0)
            .Select(cell => cell.Definition.MaterialId)
            .Distinct()
            .OrderBy(material => material)
            .ToArray();

        Assert.True(materials.Length >= 3);
        Assert.Contains(GeometryMaterialId.Grass, materials);
        Assert.Contains(GeometryMaterialId.Dirt, materials);
        Assert.Contains(GeometryMaterialId.Stone, materials);
    }

    [Fact]
    public void StartupWorld_Adds_Raised_Landmark_Cells_Above_Floor_Level()
    {
        Map map = new(width: 56, height: 8, depth: 56, loadVisualAssets: false, generateStartupTerrain: true);

        GeometryCellRecord[] raisedCells = map.GeometryCells.Values
            .Where(cell => cell.Key.Y > 0)
            .ToArray();

        Assert.NotEmpty(raisedCells);
        Assert.Contains(raisedCells, cell => cell.Definition.MaterialId == GeometryMaterialId.Stone);
    }

    [Fact]
    public void StartupWorld_Creates_Visible_Spawn_Approach_Path_And_Nearby_Landmark()
    {
        Map map = new(width: 56, height: 8, depth: 56, loadVisualAssets: false, generateStartupTerrain: true);

        GeometryCellRecord spawnPathCell = Assert.IsType<GeometryCellRecord>(map.TryGetGeometryCellAtCell(5, 0, 8));
        GeometryCellRecord nearbyLandmark = Assert.IsType<GeometryCellRecord>(map.TryGetGeometryCellAtCell(10, 1, 8));

        Assert.Equal(GeometryMaterialId.Dirt, spawnPathCell.Definition.MaterialId);
        Assert.Equal(GeometryMaterialId.Stone, nearbyLandmark.Definition.MaterialId);
    }

    [Fact]
    public void StartupWorld_Places_A_Raised_Adjacent_Landmass_Next_To_Spawn_Platform()
    {
        Map map = new(width: 56, height: 8, depth: 56, loadVisualAssets: false, generateStartupTerrain: true);

        GeometryCellRecord bridgeStart = Assert.IsType<GeometryCellRecord>(map.TryGetGeometryCellAtCell(7, 1, 5));
        GeometryCellRecord bridgeMid = Assert.IsType<GeometryCellRecord>(map.TryGetGeometryCellAtCell(8, 1, 5));
        GeometryCellRecord islandEdge = Assert.IsType<GeometryCellRecord>(map.TryGetGeometryCellAtCell(9, 1, 5));
        GeometryCellRecord islandInterior = Assert.IsType<GeometryCellRecord>(map.TryGetGeometryCellAtCell(12, 1, 8));

        Assert.Equal(GeometryMaterialId.Dirt, bridgeStart.Definition.MaterialId);
        Assert.Equal(GeometryMaterialId.Dirt, bridgeMid.Definition.MaterialId);
        Assert.True(
            islandEdge.Definition.MaterialId == GeometryMaterialId.Stone ||
            islandEdge.Definition.MaterialId == GeometryMaterialId.Dirt);
        Assert.True(
            islandInterior.Definition.MaterialId == GeometryMaterialId.Grass ||
            islandInterior.Definition.MaterialId == GeometryMaterialId.Dirt ||
            islandInterior.Definition.MaterialId == GeometryMaterialId.Stone);
    }

    [Fact]
    public void StartupWorld_Uses_Subcells_For_Authored_Pyramid_And_Rounded_Forms()
    {
        Map map = new(width: 56, height: 8, depth: 56, loadVisualAssets: false, generateStartupTerrain: true);

        GeometryCellRecord pyramidBase = Assert.IsType<GeometryCellRecord>(map.TryGetGeometryCellAtCell(18, 1, 16));
        GeometryCellRecord pyramidPeak = Assert.IsType<GeometryCellRecord>(map.TryGetGeometryCellAtCell(18, 4, 16));
        GeometryCellRecord roundedBoulder = Assert.IsType<GeometryCellRecord>(map.TryGetGeometryCellAtCell(32, 2, 14));
        GeometryRenderExport export = map.CaptureGeometryRenderExport(new Vector3(26f, 2.5f, 15f), radius: 16f);

        Assert.NotNull(pyramidBase.SubdivisionState);
        Assert.NotNull(pyramidPeak.SubdivisionState);
        Assert.NotNull(roundedBoulder.SubdivisionState);
        Assert.DoesNotContain(export.CompatibilityCubes, cube => cube.Position == new Vector3(18.5f, 1.5f, 16.5f));
        Assert.Contains(export.Blocks, block => block.CellKey == new GeometryCellKey(18, 1, 16) && block.SideLength < 1f);
        Assert.Contains(export.Blocks, block => block.CellKey == new GeometryCellKey(32, 2, 14) && block.SideLength < 1f);
    }

    [Fact]
    public void StartupWorld_Uses_Finer_Denser_Subcell_Sculpture_Clusters()
    {
        Map map = new(width: 56, height: 8, depth: 56, loadVisualAssets: false, generateStartupTerrain: true);

        GeometryRenderExport export = map.CaptureGeometryRenderExport(new Vector3(24f, 2f, 16f), radius: 18f);
        GeometryRenderBlockDescriptor[] sculptedBlocks = export.Blocks
            .Where(block => block.CellKey.Y > 0)
            .ToArray();
        GeometryCellRecord[] sculptedCells = map.GeometryCells.Values
            .Where(cell => cell.Key.Y > 0 && cell.SubdivisionState != null && cell.FracturePieces == null)
            .ToArray();
        GeometryCellRecord[] fillerCluster = sculptedCells
            .Where(cell => cell.Key.X >= 24 && cell.Key.X <= 30 && cell.Key.Z >= 11 && cell.Key.Z <= 20)
            .ToArray();

        Assert.NotEmpty(sculptedBlocks);
        Assert.All(sculptedCells, cell => Assert.Equal(WorldMicroGrid.MicroCellsPerWorldCell, cell.Definition.SubdivisionResolution));
        Assert.All(sculptedBlocks, block => Assert.True(block.SideLength <= WorldMicroGrid.MicroCellSize + 0.0001f));
        Assert.All(sculptedBlocks, block => Assert.True(block.VisualFillScale > 1f));
        Assert.True(sculptedCells.Length >= 140);
        Assert.True(fillerCluster.Length >= 10);
    }

    [Fact]
    public void StartupWorld_Projects_Sculpture_Microcells_From_GlobalCoordinates()
    {
        Map map = new(width: 56, height: 8, depth: 56, loadVisualAssets: false, generateStartupTerrain: true);

        GeometryRenderExport export = map.CaptureGeometryRenderExport(new Vector3(18.5f, 2f, 16.5f), radius: 8f);
        GeometryRenderBlockDescriptor[] boundaryBlocks = export.Blocks
            .Where(block =>
                block.CellKey.Y > 0 &&
                block.Center.X > 18.9f &&
                block.Center.X < 19.1f)
            .OrderBy(block => block.Center.X)
            .ThenBy(block => block.Center.Y)
            .ThenBy(block => block.Center.Z)
            .ToArray();

        Assert.Contains(boundaryBlocks, block => block.CellKey.X == 18 && block.SubcellKey?.X >= 14);
        Assert.Contains(boundaryBlocks, block => block.CellKey.X == 19 && block.SubcellKey?.X <= 1);
        Assert.DoesNotContain(boundaryBlocks, block => MathF.Abs((block.Center.X * WorldMicroGrid.MicroCellsPerWorldCell) - 0.5f) > 0.001f &&
            MathF.Abs(MathF.Round((block.Center.X * WorldMicroGrid.MicroCellsPerWorldCell) - 0.5f) - ((block.Center.X * WorldMicroGrid.MicroCellsPerWorldCell) - 0.5f)) > 0.001f);
    }

    [Fact]
    public void StartupWorld_Bounds_Rendered_GlobalMicroGrid_Sculpture_Blocks()
    {
        Map map = new(width: 56, height: 8, depth: 56, loadVisualAssets: false, generateStartupTerrain: true);

        GeometryRenderExport renderExport = map.CaptureGeometryRenderExport(new Vector3(24f, 2f, 16f), WorldVisibility.MaxRenderDistance);
        int sculptedRenderBlocks = renderExport.Blocks.Count(block => block.CellKey.Y > 0);

        Assert.InRange(sculptedRenderBlocks, 20_000, 80_000);
    }

}
