using OpenTK.Mathematics;
using Xunit;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;

namespace Dream.Tests.Simulation.World;


public class WorldVisibilityTests
{
    [Fact]
    public void IsCubeVisible_Renders_Cube_In_Front_Within_Distance()
    {
        Vector3 cameraPosition = Vector3.Zero;
        Vector3 cameraFront = Vector3.UnitZ;
        Cube cube = new Cube
        {
            Position = new Vector3(0f, 0f, 12f)
        };

        bool visible = WorldVisibility.IsCubeVisible(cameraPosition, cameraFront, cube);

        Assert.True(visible);
    }

    [Fact]
    public void IsCubeVisible_Hides_Cube_Outside_Render_Distance()
    {
        Vector3 cameraPosition = Vector3.Zero;
        Vector3 cameraFront = Vector3.UnitZ;
        Cube cube = new Cube
        {
            Position = new Vector3(0f, 0f, 80f)
        };

        bool visible = WorldVisibility.IsCubeVisible(cameraPosition, cameraFront, cube);

        Assert.False(visible);
    }

    [Fact]
    public void IsCubeVisible_Hides_Cube_Fully_Behind_Camera()
    {
        Vector3 cameraPosition = Vector3.Zero;
        Vector3 cameraFront = Vector3.UnitZ;
        Cube cube = new Cube
        {
            Position = new Vector3(0f, 0f, -12f)
        };

        bool visible = WorldVisibility.IsCubeVisible(cameraPosition, cameraFront, cube);

        Assert.False(visible);
    }

    [Fact]
    public void IsCubeVisible_Still_Renders_Very_Close_Cube_Behind_Player()
    {
        Vector3 cameraPosition = Vector3.Zero;
        Vector3 cameraFront = Vector3.UnitZ;
        Cube cube = new Cube
        {
            Position = new Vector3(0f, 0f, -0.2f)
        };

        bool visible = WorldVisibility.IsCubeVisible(cameraPosition, cameraFront, cube);

        Assert.True(visible);
    }

    [Fact]
    public void IsCubeVisible_Renders_Cube_Whose_Bounds_Overlap_Visible_Frustum()
    {
        Vector3 cameraPosition = Vector3.Zero;
        Vector3 cameraFront = Vector3.UnitZ;
        Cube cube = new Cube
        {
            Position = new Vector3(2.75f, 0f, -1.2f)
        };

        bool visible = WorldVisibility.IsCubeVisible(cameraPosition, cameraFront, cube);

        Assert.True(visible);
    }

    [Fact]
    public void IsGeometryBlockVisible_Uses_Block_Bounds_Not_Only_Center_Point()
    {
        Vector3 cameraPosition = Vector3.Zero;
        Vector3 cameraFront = Vector3.UnitZ;
        GeometryRenderBlockDescriptor block = new(
            new GeometryCellKey(0, 0, 0),
            new Vector3(2.75f, 0f, -1.2f),
            1f,
            GeometryMaterialId.Grass,
            "default");

        bool visible = WorldVisibility.IsGeometryBlockVisible(cameraPosition, cameraFront, block);

        Assert.True(visible);
    }

    [Fact]
    public void IsGeometryBlockVisible_Uses_Merged_Block_Size_For_Bounds()
    {
        Vector3 cameraPosition = Vector3.Zero;
        Vector3 cameraFront = Vector3.UnitZ;
        GeometryRenderBlockDescriptor block = new(
            new GeometryCellKey(0, 0, 0),
            new Vector3(2.75f, 0f, -1.2f),
            WorldMicroGrid.MicroCellSize,
            GeometryMaterialId.Stone,
            "default",
            SizeOverride: new Vector3(3f, WorldMicroGrid.MicroCellSize, 3f));

        bool visible = WorldVisibility.IsGeometryBlockVisible(cameraPosition, cameraFront, block);

        Assert.True(visible);
    }
}
