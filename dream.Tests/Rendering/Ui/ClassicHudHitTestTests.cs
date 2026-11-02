using OpenTK.Mathematics;
using Xunit;
using Dream.Rendering.Ui;

namespace Dream.Tests.Rendering.Ui;

public class ClassicHudHitTestTests
{
    private static readonly UiLayout Layout = UiLayout.Create(1440, 1024, 0f);

    [Fact]
    public void TheBarIsAlwaysOverTheHud()
    {
        Vector2 inBar = new(Layout.Bar.X + 5f, Layout.Bar.Y + 5f);
        Assert.True(ClassicHudHitTest.Over(in Layout, inventoryOpen: false, inBar));
        Assert.True(ClassicHudHitTest.Over(in Layout, inventoryOpen: true, inBar));
    }

    [Fact]
    public void TheWorldAboveTheBarIsNotOverTheHud()
    {
        Vector2 world = new(Layout.ViewportWidth * 0.3f, Layout.ViewportHeight * 0.4f);
        Assert.False(ClassicHudHitTest.Over(in Layout, false, world));
        Assert.False(ClassicHudHitTest.Over(in Layout, true, world));
    }

    [Fact]
    public void WindowsCountOnlyWhileOpen()
    {
        Vector2 inInventory = new(Layout.Inventory.X + 5f, Layout.Inventory.Y + 5f);
        Vector2 inCharacter = new(Layout.Character.X + 5f, Layout.Character.Y + 5f);

        Assert.False(ClassicHudHitTest.Over(in Layout, false, inInventory));
        Assert.False(ClassicHudHitTest.Over(in Layout, false, inCharacter));
        Assert.True(ClassicHudHitTest.Over(in Layout, true, inInventory));
        Assert.True(ClassicHudHitTest.Over(in Layout, true, inCharacter));
    }

    [Fact]
    public void JustPastTheBarTopEdgeIsTheWorld()
    {
        Vector2 above = new(Layout.ViewportWidth * 0.5f, Layout.Bar.Y - 1f);
        Assert.False(ClassicHudHitTest.Over(in Layout, false, above));
    }
}
