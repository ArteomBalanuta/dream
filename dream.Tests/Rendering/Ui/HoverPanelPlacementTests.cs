using Dream.Rendering.Ui;
using Dream.Tests.Rendering.Ui.Panels;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Rendering.Ui;

public class HoverPanelPlacementTests
{
    private const int W = 1920;
    private const int H = 1080;

    // A viewport big enough that the auto fit allows the scale asked for (it caps the scale at height / 480).
    private static UiLayout Layout(float scale) =>
        UiLayout.Create((int)MathF.Max(W, 640f * scale), (int)MathF.Max(H, 480f * scale), scale);

    private static UiLayout Tiny(int w, int h) => UiLayout.Create(w, h, 1f);

    private static UiRect CursorAt(Vector2 p, in UiLayout l)
    {
        UiSprite sprite = StatusBarTestData.Table.Get(UiSpriteNames.CursorPointer);
        return UiCursor.Rect(in sprite, p, l.Scale);
    }

    private static UiRect Place(Vector2 p, float w, float h, in UiLayout l, bool inventory = false)
    {
        Vector2 at = HoverPanelPlacement.AtPointer(p, w, h, in l, inventory, CursorAt(p, in l));
        return new UiRect(at.X, at.Y, w, h);
    }

    private static bool Overlaps(UiRect a, UiRect b) => a.X < b.Right && b.X < a.Right && a.Y < b.Bottom && b.Y < a.Bottom;

    private static void AssertOnScreen(UiRect r, in UiLayout l)
    {
        Assert.True(r.X >= 0f && r.Y >= 0f && r.Right <= l.ViewportWidth && r.Bottom <= l.ViewportHeight, r.ToString());
    }

    [Theory]
    [InlineData(1f)]
    [InlineData(2f)]
    [InlineData(3f)]
    [InlineData(4f)]
    public void InTheMiddle_SitsDownRightOfThePointerAndClearOfTheCursor(float scale)
    {
        UiLayout l = Layout(scale);
        Vector2 p = new(800f, 300f);
        UiRect r = Place(p, 140f * scale, 80f * scale, in l);

        Assert.True(r.X >= p.X, "right of the pointer");
        Assert.True(r.Y >= p.Y + (HoverPanelPlacement.OffsetYUnits * scale) - 0.5f, "not above the offset");
        Assert.False(Overlaps(r, CursorAt(p, in l)));
        Assert.Equal(MathF.Round(r.X), r.X);
        Assert.Equal(MathF.Round(r.Y), r.Y);
    }

    [Theory]
    [InlineData(1f)]
    [InlineData(2f)]
    [InlineData(3f)]
    [InlineData(4f)]
    public void FourCorners_StayOnScreenAndClearOfTheCursor(float scale)
    {
        UiLayout l = Layout(scale);
        float w = 140f * scale;
        float h = 80f * scale;
        foreach (Vector2 p in new[] { new Vector2(0f, 0f), new Vector2(l.ViewportWidth - 1f, 0f), new Vector2(0f, l.ViewportHeight - 1f), new Vector2(l.ViewportWidth - 1f, l.ViewportHeight - 1f) })
        {
            UiRect r = Place(p, w, h, in l);
            AssertOnScreen(r, in l);
            Assert.False(Overlaps(r, CursorAt(p, in l)), $"{p} scale {scale}: {r}");
            Assert.True(r.X >= HoverPanelPlacement.MarginUnits * scale - 0.5f && r.Y >= HoverPanelPlacement.MarginUnits * scale - 0.5f);
        }
    }

    [Fact]
    public void NearTheRightEdge_FlipsToTheLeftOfThePointer()
    {
        UiLayout l = Layout(2f);
        Vector2 p = new(l.ViewportWidth - 40f, 300f);
        UiRect r = Place(p, 280f, 160f, in l);

        Assert.True(r.Right <= p.X, "left of the pointer");
        Assert.True(r.Y >= p.Y, "still below it");
    }

    [Fact]
    public void NearTheBottomEdge_FlipsAboveThePointer()
    {
        UiLayout l = Layout(2f);
        Vector2 p = new(400f, l.ViewportHeight - 200f);
        UiRect r = Place(p, 280f, 160f, in l);

        Assert.True(r.Bottom <= p.Y, "above the pointer");
        Assert.True(r.X >= p.X, "still right of it");
        Assert.False(Overlaps(r, l.Bar));
    }

    [Fact]
    public void PanelDownRightOfAPointerAboveTheBar_IsPushedOutOfTheBar()
    {
        UiLayout l = Layout(2f);
        Vector2 p = new(900f, l.Bar.Y - 40f);
        UiRect r = Place(p, 280f, 160f, in l);

        Assert.False(Overlaps(r, l.Bar), $"{r} vs {l.Bar}");
        Assert.False(Overlaps(r, CursorAt(p, in l)));
        AssertOnScreen(r, in l);
    }

    [Fact]
    public void WithTheInventoryOpen_ThePanelAlsoLeavesTheWindows()
    {
        UiLayout l = Layout(2f);
        foreach (UiRect window in new[] { l.Inventory, l.Character })
        {
            Vector2 p = new(window.X + 20f, window.Y + (window.H * 0.5f));
            UiRect r = Place(p, 200f, 100f, in l, inventory: true);
            Assert.False(Overlaps(r, window), $"{r} vs {window}");
            Assert.False(Overlaps(r, CursorAt(p, in l)));
            AssertOnScreen(r, in l);
        }
    }

    [Fact]
    public void WithTheInventoryClosed_TheWindowsAreIgnored()
    {
        UiLayout l = Layout(2f);
        Vector2 p = new(l.Inventory.X + 20f, l.Inventory.Y + (l.Inventory.H * 0.3f));
        UiRect closed = Place(p, 200f, 100f, in l, inventory: false);
        UiRect open = Place(p, 200f, 100f, in l, inventory: true);

        Assert.True(Overlaps(closed, l.Inventory), "the closed-inventory placement must not avoid the window");
        Assert.NotEqual(closed, open);
    }

    [Theory]
    [InlineData(1f)]
    [InlineData(2f)]
    [InlineData(3f)]
    [InlineData(4f)]
    public void TheCursorIsNeverCovered_AcrossAGridOfPointers(float scale)
    {
        UiLayout l = Layout(scale);
        float w = 140f * scale;
        float h = 90f * scale;
        for (float x = 0f; x < l.ViewportWidth; x += 97f)
        {
            for (float y = 0f; y < l.ViewportHeight; y += 83f)
            {
                Vector2 p = new(x, y);
                UiRect r = Place(p, w, h, in l);
                AssertOnScreen(r, in l);
                if (Overlaps(r, l.Bar))
                {
                    // Only when no room is left: a bar-sized strip cannot be avoided by a panel this tall.
                    Assert.True(l.Bar.Y < h + (2f * HoverPanelPlacement.MarginUnits * scale), $"{p}: {r}");
                }

                Assert.False(Overlaps(r, CursorAt(p, in l)), $"{p} scale {scale}: {r} vs {CursorAt(p, in l)}");
            }
        }
    }

    [Fact]
    public void ACursorOverlapGate_CanFail()
    {
        // The same check on a panel put right where the offset says, ignoring the cursor: it must report an overlap.
        UiLayout l = Layout(4f);
        Vector2 p = new(800f, 300f);
        UiRect naive = new(p.X + (18f * l.Scale), p.Y + (22f * l.Scale), 140f * l.Scale, 80f * l.Scale);
        Assert.True(Overlaps(naive, CursorAt(p, in l)));
        Assert.False(Overlaps(Place(p, 140f * l.Scale, 80f * l.Scale, in l), CursorAt(p, in l)));
    }

    [Theory]
    [InlineData(320, 200)]
    [InlineData(160, 100)]
    [InlineData(40, 30)]
    [InlineData(1, 1)]
    public void TinyViewport_GivesFinitePositionsAndTriesToStayOnScreen(int vw, int vh)
    {
        UiLayout l = Tiny(vw, vh);
        foreach (Vector2 p in new[] { new Vector2(0f, 0f), new Vector2(vw * 0.5f, vh * 0.5f), new Vector2(vw, vh) })
        {
            Vector2 at = HoverPanelPlacement.AtPointer(p, 140f, 80f, in l, true, CursorAt(p, in l));
            Assert.True(float.IsFinite(at.X) && float.IsFinite(at.Y));
            Vector2 anchored = HoverPanelPlacement.Anchored(p, 140f, 80f, in l, true);
            Assert.True(float.IsFinite(anchored.X) && float.IsFinite(anchored.Y));
        }

        // A panel smaller than the viewport stays inside it.
        if (vw >= 100 && vh >= 100)
        {
            UiRect r = Place(new Vector2(vw * 0.5f, vh * 0.5f), 60f, 40f, in l);
            AssertOnScreen(r, in l);
        }
    }

    [Fact]
    public void NonFiniteInput_FallsBackWithoutNaN()
    {
        UiLayout l = Layout(2f);
        Vector2 a = HoverPanelPlacement.AtPointer(new Vector2(float.NaN, 5f), 100f, 50f, in l, false, default);
        Vector2 b = HoverPanelPlacement.AtPointer(new Vector2(5f, 5f), float.PositiveInfinity, 50f, in l, false, default);
        Vector2 c = HoverPanelPlacement.Anchored(new Vector2(float.PositiveInfinity, 5f), 100f, 50f, in l, false);
        foreach (Vector2 v in new[] { a, b, c })
        {
            Assert.True(float.IsFinite(v.X) && float.IsFinite(v.Y));
        }
    }

    [Theory]
    [InlineData(1f)]
    [InlineData(2f)]
    [InlineData(4f)]
    public void Anchored_SitsCentredAboveTheAnchor(float scale)
    {
        UiLayout l = Layout(scale);
        Vector2 anchor = new(900f, 500f);
        float w = 140f * scale;
        float h = 70f * scale;
        Vector2 at = HoverPanelPlacement.Anchored(anchor, w, h, in l, false);

        Assert.Equal(MathF.Round(anchor.X - (w * 0.5f)), at.X);
        Assert.True(at.Y + h <= anchor.Y);
        Assert.Equal(MathF.Round(at.Y), at.Y);
    }

    [Fact]
    public void Anchored_FlipsBelowAtTheTop_AndClampsAtTheSides()
    {
        UiLayout l = Layout(2f);
        Vector2 top = HoverPanelPlacement.Anchored(new Vector2(900f, 10f), 280f, 140f, in l, false);
        Assert.True(top.Y >= 10f);

        Vector2 left = HoverPanelPlacement.Anchored(new Vector2(5f, 500f), 280f, 140f, in l, false);
        Assert.True(left.X >= HoverPanelPlacement.MarginUnits * 2f - 0.5f);
        Vector2 right = HoverPanelPlacement.Anchored(new Vector2(l.ViewportWidth - 5f, 500f), 280f, 140f, in l, false);
        Assert.True(right.X + 280f <= l.ViewportWidth);
    }

    [Fact]
    public void Anchored_LeavesTheBar()
    {
        UiLayout l = Layout(2f);
        Vector2 at = HoverPanelPlacement.Anchored(new Vector2(900f, l.Bar.Y + 60f), 280f, 140f, in l, false);
        Assert.False(Overlaps(new UiRect(at.X, at.Y, 280f, 140f), l.Bar));
    }
}
