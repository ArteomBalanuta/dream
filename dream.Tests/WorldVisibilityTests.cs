using OpenTK.Mathematics;
using Xunit;

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
}
