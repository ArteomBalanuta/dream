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
        Map map = new(width: 40, height: 8, depth: 40, loadVisualAssets: false, generateStartupTerrain: true);

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
        Map map = new(width: 40, height: 8, depth: 40, loadVisualAssets: false, generateStartupTerrain: true);

        GeometryCellRecord[] raisedCells = map.GeometryCells.Values
            .Where(cell => cell.Key.Y > 0)
            .ToArray();

        Assert.NotEmpty(raisedCells);
        Assert.Contains(raisedCells, cell => cell.Definition.MaterialId == GeometryMaterialId.Stone);
    }

    [Fact]
    public void StartupWorld_Creates_Visible_Spawn_Approach_Path_And_Nearby_Landmark()
    {
        Map map = new(width: 40, height: 8, depth: 40, loadVisualAssets: false, generateStartupTerrain: true);

        GeometryCellRecord spawnPathCell = Assert.IsType<GeometryCellRecord>(map.TryGetGeometryCellAtCell(5, 0, 8));
        GeometryCellRecord nearbyLandmark = Assert.IsType<GeometryCellRecord>(map.TryGetGeometryCellAtCell(10, 1, 8));

        Assert.Equal(GeometryMaterialId.Dirt, spawnPathCell.Definition.MaterialId);
        Assert.Equal(GeometryMaterialId.Stone, nearbyLandmark.Definition.MaterialId);
    }

    [Fact]
    public void StartupWorld_Places_A_Raised_Adjacent_Landmass_Next_To_Spawn_Platform()
    {
        Map map = new(width: 40, height: 8, depth: 40, loadVisualAssets: false, generateStartupTerrain: true);

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
}
