using Xunit;

public class MapPerformanceStructureTests
{
    [Fact]
    public void Map_OnlyMaterializes_Solid_Cubes_On_Startup()
    {
        var map = new Map(loadVisualAssets: false);

        Assert.Equal(map.Width * map.Depth, map.SolidCubes.Count);
        Assert.NotNull(map.Cubes[0, 0, 0]);
        Assert.Null(map.Cubes[0, 1, 0]);
    }

    [Fact]
    public void AddCube_Materializes_New_Solid_Without_Filling_Neighbors()
    {
        var map = new Map(loadVisualAssets: false);
        int initialSolidCount = map.SolidCubes.Count;

        map.AddCube(new OpenTK.Mathematics.Vector3(2, 1, 2), withPhysics: false);

        Assert.Equal(initialSolidCount + 1, map.SolidCubes.Count);
        Assert.NotNull(map.Cubes[2, 1, 2]);
        Assert.Null(map.Cubes[2, 2, 2]);
    }

    [Fact]
    public void Map_Can_Be_Constructed_With_Custom_Floor_Size()
    {
        var map = new Map(width: 10, height: 8, depth: 10, loadVisualAssets: false);

        Assert.Equal(10, map.Width);
        Assert.Equal(10, map.Depth);
        Assert.Equal(100, map.SolidCubes.Count);
        Assert.NotNull(map.Cubes[9, 0, 9]);
        Assert.Null(map.Cubes[9, 1, 9]);
    }

    [Fact]
    public void CopyPlacedCubesTo_Preserves_InBounds_NonFloor_Cubes()
    {
        var source = new Map(width: 8, height: 8, depth: 8, loadVisualAssets: false);
        source.AddCube(new OpenTK.Mathematics.Vector3(3, 1, 3), withPhysics: false);

        var target = new Map(width: 10, height: 8, depth: 10, loadVisualAssets: false);
        source.CopyPlacedCubesTo(target);

        Assert.NotNull(target.Cubes[3, 1, 3]);
        Assert.Equal((10 * 10) + 1, target.SolidCubes.Count);
    }

    [Fact]
    public void CopyPlacedCubesTo_Drops_OutOfBounds_NonFloor_Cubes()
    {
        var source = new Map(width: 10, height: 8, depth: 10, loadVisualAssets: false);
        source.AddCube(new OpenTK.Mathematics.Vector3(9, 1, 9), withPhysics: false);

        var target = new Map(width: 5, height: 8, depth: 5, loadVisualAssets: false);
        source.CopyPlacedCubesTo(target);

        Assert.Null(target.Cubes[4, 1, 4]);
        Assert.Equal(25, target.SolidCubes.Count);
    }
}
