using Xunit;

public class HudLayoutTests
{
    [Fact]
    public void CreateCrosshair_Centers_On_Viewport_Midpoint()
    {
        var layout = HudLayout.CreateCrosshair(1440, 1024);

        Assert.Equal(720f, layout.CenterX);
        Assert.Equal(512f, layout.CenterY);
    }

    [Fact]
    public void CreateCrosshair_Scales_With_Smaller_Viewport_Axis()
    {
        var small = HudLayout.CreateCrosshair(800, 600);
        var large = HudLayout.CreateCrosshair(1920, 1080);

        Assert.True(large.ArmLength > small.ArmLength);
        Assert.True(large.Gap > small.Gap);
    }

    [Fact]
    public void CreateCrosshair_Clamps_Arm_Length_And_Gap()
    {
        var tiny = HudLayout.CreateCrosshair(100, 100);
        var huge = HudLayout.CreateCrosshair(4000, 4000);

        Assert.Equal(8f, tiny.ArmLength);
        Assert.Equal(3f, tiny.Gap);
        Assert.Equal(18f, huge.ArmLength);
        Assert.Equal(8f, huge.Gap);
    }

    [Fact]
    public void CreateCrosshair_Throws_For_Non_Positive_Width()
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => HudLayout.CreateCrosshair(0, 600));

        Assert.Equal("width", exception.ParamName);
    }

    [Fact]
    public void CreateCrosshair_Throws_For_Non_Positive_Height()
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => HudLayout.CreateCrosshair(800, -1));

        Assert.Equal("height", exception.ParamName);
    }

    [Fact]
    public void ToVertexArray_Returns_Symmetric_Line_Segments()
    {
        var layout = HudLayout.CreateCrosshair(1600, 900);
        float[] vertices = layout.ToVertexArray();

        Assert.Equal(16, vertices.Length);

        float leftOuterX = vertices[0];
        float leftInnerX = vertices[2];
        float rightInnerX = vertices[4];
        float rightOuterX = vertices[6];
        float horizontalY1 = vertices[1];
        float horizontalY2 = vertices[3];
        float horizontalY3 = vertices[5];
        float horizontalY4 = vertices[7];

        Assert.Equal(layout.CenterY, horizontalY1, 3);
        Assert.Equal(layout.CenterY, horizontalY2, 3);
        Assert.Equal(layout.CenterY, horizontalY3, 3);
        Assert.Equal(layout.CenterY, horizontalY4, 3);
        Assert.True(leftOuterX < leftInnerX);
        Assert.True(leftInnerX < layout.CenterX);
        Assert.True(layout.CenterX < rightInnerX);
        Assert.True(rightInnerX < rightOuterX);
        Assert.Equal(layout.Gap, layout.CenterX - leftInnerX, 3);
        Assert.Equal(layout.Gap, rightInnerX - layout.CenterX, 3);
        Assert.Equal(layout.ArmLength, leftInnerX - leftOuterX, 3);
        Assert.Equal(layout.ArmLength, rightOuterX - rightInnerX, 3);

        float upOuterY = vertices[9];
        float upInnerY = vertices[11];
        float downInnerY = vertices[13];
        float downOuterY = vertices[15];
        float verticalX1 = vertices[8];
        float verticalX2 = vertices[10];
        float verticalX3 = vertices[12];
        float verticalX4 = vertices[14];

        Assert.Equal(layout.CenterX, verticalX1, 3);
        Assert.Equal(layout.CenterX, verticalX2, 3);
        Assert.Equal(layout.CenterX, verticalX3, 3);
        Assert.Equal(layout.CenterX, verticalX4, 3);
        Assert.True(upOuterY < upInnerY);
        Assert.True(upInnerY < layout.CenterY);
        Assert.True(layout.CenterY < downInnerY);
        Assert.True(downInnerY < downOuterY);
        Assert.Equal(layout.Gap, layout.CenterY - upInnerY, 3);
        Assert.Equal(layout.Gap, downInnerY - layout.CenterY, 3);
        Assert.Equal(layout.ArmLength, upInnerY - upOuterY, 3);
        Assert.Equal(layout.ArmLength, downOuterY - downInnerY, 3);
    }
}
