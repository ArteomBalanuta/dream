using Xunit;
using Dream.Rendering.Ui;

namespace Dream.Tests.Rendering.Ui;

public class UiScaleTests
{
    [Theory]
    [InlineData(1280, 720, 1.5f)]
    [InlineData(1920, 1080, 2.25f)]
    [InlineData(1440, 1024, 2.0f)]
    [InlineData(2560, 1440, 3.0f)]
    [InlineData(3840, 2160, 4.0f)]
    [InlineData(640, 480, 1.0f)]
    [InlineData(320, 200, 1.0f)]
    [InlineData(0, 0, 1.0f)]
    public void Auto_FitsBothAxes_FlooredToQuarterSteps(int w, int h, float expected)
    {
        Assert.Equal(expected, UiScale.Resolve(w, h, 0f));
    }

    [Theory]
    [InlineData(1.3f, 1.25f)]
    [InlineData(2.0f, 2.0f)]
    [InlineData(0.1f, 1.0f)]
    [InlineData(9f, 4.0f)]
    public void Configured_SnapsToQuarterAndClamps(float configured, float expected)
    {
        Assert.Equal(expected, UiScale.Resolve(1920, 1080, configured));
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(-3f)]
    public void NonsenseConfigured_FallsBackToAuto(float configured)
    {
        Assert.Equal(2.25f, UiScale.Resolve(1920, 1080, configured));
    }

    [Theory]
    [InlineData(1280, 720)]
    [InlineData(1920, 1080)]
    [InlineData(1440, 1024)]
    public void Layout_BarAndWindowsFitTheViewport(int w, int h)
    {
        UiLayout l = UiLayout.Create(w, h, 0f);

        Assert.Equal(h, l.Bar.Bottom, 0.01f);
        Assert.Equal(48f * l.Scale, l.Bar.H, 0.01f);
        Assert.True(l.Inventory.Bottom <= l.Bar.Y + 0.01f, "inventory window stays above the bar");
        Assert.Equal(w, l.Inventory.Right, 0.01f);
        Assert.Equal(l.Inventory.X, l.Character.Right, 0.01f);
        Assert.True(l.Character.X >= 0f);
    }

    [Fact]
    public void Layout_720p_Numbers()
    {
        UiLayout l = UiLayout.Create(1280, 720, 0f);

        Assert.Equal(1.5f, l.Scale);
        Assert.Equal(new UiRect(0, 648, 298.5f, 72), l.LeftGroup);
        Assert.Equal(new UiRect(1280 - 298.5f, 648, 298.5f, 72), l.RightGroup);
        Assert.Equal(new UiRect(1280 - 285, 0, 285, 648), l.Inventory);
        Assert.Equal(new UiRect(1280 - 570, 0, 285, 648), l.Character);
        // 640 u * 1.5 = 960 px of art in 1280 px: 320 px of gaps in two halves.
        Assert.Equal(160f, l.LeftGap.W, 0.6f);
        Assert.Equal(160f, l.RightGap.W, 0.6f);
    }

    [Fact]
    public void Layout_1080p_Numbers()
    {
        UiLayout l = UiLayout.Create(1920, 1080, 0f);

        Assert.Equal(2.25f, l.Scale);
        Assert.Equal(108f, l.Bar.H, 0.001f);
        Assert.Equal(972f, l.Bar.Y, 0.001f);
        Assert.Equal(new UiRect(1920 - 427.5f, 0, 427.5f, 972), l.Inventory);
        // 1440 px of art in 1920 px: 480 px of gaps, 240 each.
        Assert.Equal(240f, l.LeftGap.W, 0.6f);
        Assert.Equal(240f, l.RightGap.W, 0.6f);
        Assert.Equal(l.LeftGap.Right, l.CentreGroup.X, 0.001f);
        Assert.Equal(l.CentreGroup.Right, l.RightGap.X, 0.001f);
        Assert.Equal(l.RightGap.Right, l.RightGroup.X, 0.001f);
    }

    [Fact]
    public void Layout_1440x1024_Numbers()
    {
        UiLayout l = UiLayout.Create(1440, 1024, 0f);

        Assert.Equal(2.0f, l.Scale);
        Assert.Equal(96f, l.Bar.H, 0.001f);
        Assert.Equal(928f, l.Bar.Y, 0.001f);
        Assert.Equal(new UiRect(1440 - 380, 0, 380, 864), l.Inventory);
        Assert.Equal(new UiRect(1440 - 760, 0, 380, 864), l.Character);
        // 1280 px of art in 1440 px: 160 px of gaps.
        Assert.Equal(80f, l.LeftGap.W, 0.6f);
        Assert.Equal(80f, l.RightGap.W, 0.6f);
    }

    [Fact]
    public void Layout_NarrowViewport_GapsNeverNegative()
    {
        UiLayout l = UiLayout.Create(640, 480, 0f);

        Assert.Equal(1f, l.Scale);
        Assert.Equal(0f, l.LeftGap.W, 0.6f);
        Assert.True(l.RightGap.W >= 0f);
    }

    [Fact]
    public void Rect_Contains_IsHalfOpen()
    {
        UiRect r = new(10, 20, 30, 40);

        Assert.True(r.Contains(new OpenTK.Mathematics.Vector2(10, 20)));
        Assert.True(r.Contains(new OpenTK.Mathematics.Vector2(39.9f, 59.9f)));
        Assert.False(r.Contains(new OpenTK.Mathematics.Vector2(40, 30)));
        Assert.False(r.Contains(new OpenTK.Mathematics.Vector2(20, 60)));
        Assert.False(r.Contains(new OpenTK.Mathematics.Vector2(9.9f, 30)));
    }
}
