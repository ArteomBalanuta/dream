using Dream.Client.CameraControl;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Client.CameraControl;

public class EdgePanFieldTests
{
    private static readonly Vector2i Size = new(1000, 600);

    [Fact]
    public void Centre_is_zero() =>
        Assert.Equal(Vector2.Zero, EdgePanField.Evaluate(new Vector2(500, 300), Size, 10f));

    [Fact]
    public void Left_edge_pans_left()
    {
        Vector2 v = EdgePanField.Evaluate(new Vector2(0, 300), Size, 10f);
        Assert.Equal(-1f, v.X, 3);
        Assert.Equal(0f, v.Y, 3);
    }

    [Fact]
    public void Right_edge_pans_right()
    {
        Vector2 v = EdgePanField.Evaluate(new Vector2(999, 300), Size, 10f);
        Assert.Equal(1f, v.X, 3);
        Assert.Equal(0f, v.Y, 3);
    }

    [Fact]
    public void Top_edge_is_positive_y_and_bottom_negative()
    {
        Assert.Equal(1f, EdgePanField.Evaluate(new Vector2(500, 0), Size, 10f).Y, 3);
        Assert.Equal(-1f, EdgePanField.Evaluate(new Vector2(500, 599), Size, 10f).Y, 3);
    }

    [Fact]
    public void Corners_clamp_to_unit_length()
    {
        foreach (Vector2 corner in new[] { new Vector2(0, 0), new Vector2(999, 0), new Vector2(0, 599), new Vector2(999, 599) })
        {
            Assert.Equal(1f, EdgePanField.Evaluate(corner, Size, 10f).Length, 3);
        }
    }

    [Fact]
    public void Ramp_rises_through_the_margin()
    {
        float outer = EdgePanField.Evaluate(new Vector2(1, 300), Size, 10f).X;
        float inner = EdgePanField.Evaluate(new Vector2(8, 300), Size, 10f).X;
        Assert.True(outer < inner && inner < 0f);
    }

    [Fact]
    public void Margin_scales()
    {
        Assert.Equal(Vector2.Zero, EdgePanField.Evaluate(new Vector2(15, 300), Size, 10f));
        Assert.True(EdgePanField.Evaluate(new Vector2(15, 300), Size, 20f).X < 0f);
    }

    [Fact]
    public void Degenerate_inputs_are_zero()
    {
        Assert.Equal(Vector2.Zero, EdgePanField.Evaluate(new Vector2(0, 0), new Vector2i(0, 600), 10f));
        Assert.Equal(Vector2.Zero, EdgePanField.Evaluate(new Vector2(0, 0), Size, 0f));
        Assert.Equal(Vector2.Zero, EdgePanField.Evaluate(new Vector2(0, 0), Size, -3f));
        Assert.Equal(Vector2.Zero, EdgePanField.Evaluate(new Vector2(float.NaN, 0), Size, 10f));
        Assert.Equal(Vector2.Zero, EdgePanField.Evaluate(new Vector2(0, float.PositiveInfinity), Size, 10f));
    }

    [Fact]
    public void Huge_margin_is_limited_to_a_third_so_the_centre_stays_idle()
    {
        Assert.Equal(Vector2.Zero, EdgePanField.Evaluate(new Vector2(500, 300), Size, 10000f));
    }
}
