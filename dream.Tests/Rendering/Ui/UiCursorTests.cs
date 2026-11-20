using System.Text.Json;
using Dream.Assets;
using Dream.Rendering.Ui;
using Dream.Tests.Rendering.Ui.Panels;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Rendering.Ui;

public class UiCursorTests
{
    [Theory]
    [InlineData(true, true, true, UiCursorKind.Grab)]
    [InlineData(true, false, false, UiCursorKind.Grab)]
    [InlineData(false, true, true, UiCursorKind.Attack)]
    [InlineData(false, false, true, UiCursorKind.Attack)]
    [InlineData(false, true, false, UiCursorKind.Pressed)]
    [InlineData(false, false, false, UiCursorKind.Pointer)]
    public void Select_FollowsPrecedence(bool dragging, bool leftDown, bool overEnemy, UiCursorKind expected) =>
        Assert.Equal(expected, UiCursor.Select(dragging, leftDown, overEnemy));

    [Fact]
    public void SpriteName_MapsEveryKind()
    {
        Assert.Equal(UiSpriteNames.CursorPointer, UiCursor.SpriteName(UiCursorKind.Pointer));
        Assert.Equal(UiSpriteNames.CursorPressed, UiCursor.SpriteName(UiCursorKind.Pressed));
        Assert.Equal(UiSpriteNames.CursorGrab, UiCursor.SpriteName(UiCursorKind.Grab));
        Assert.Equal(UiSpriteNames.CursorAttack, UiCursor.SpriteName(UiCursorKind.Attack));
    }

    [Fact]
    public void Rect_PutsThePivotOnThePosition()
    {
        UiSprite sprite = StatusBarTestData.Table.Get(UiSpriteNames.CursorAttack);
        float k = 4f / UiCursor.ArtPixelsPerUnit;
        UiRect r = UiCursor.Rect(in sprite, new Vector2(400f, 300f), 4f);

        Assert.Equal(400f, r.X + sprite.PivotX * k, 0);
        Assert.Equal(300f, r.Y + sprite.PivotY * k, 0);
        Assert.Equal(sprite.W * k, r.W, 3);
        Assert.Equal(sprite.H * k, r.H, 3);

        UiRect big = UiCursor.Rect(in sprite, new Vector2(400f, 300f), 8f);
        Assert.Equal(r.W * 2f, big.W, 3);
    }

    [Fact]
    public void Draw_SkipsNonInteractiveNanAndMissingSprites()
    {
        UiSpriteTable table = StatusBarTestData.Table;
        StatusBarRecordingCanvas c = new(table);

        Assert.False(UiCursor.Draw(c, table, new UiPointer(new Vector2(5f, 5f), false, false, false), 4f, UiCursorKind.Pointer));
        Assert.False(UiCursor.Draw(c, table, new UiPointer(new Vector2(float.NaN, 5f), false, false, true), 4f, UiCursorKind.Pointer));
        Assert.False(UiCursor.Draw(c, table, new UiPointer(new Vector2(5f, float.PositiveInfinity), false, false, true), 4f, UiCursorKind.Pointer));
        Assert.Empty(c.Calls);

        Assert.True(UiSpriteTable.TryLoadPages(AssetPaths.UiDirectory, [UiPage.Window], out UiSpriteTable? noCursors, out string error), error);
        Assert.False(UiCursor.Draw(c, noCursors!, new UiPointer(new Vector2(5f, 5f), false, false, true), 4f, UiCursorKind.Pointer));
        Assert.Empty(c.Calls);

        Assert.True(UiCursor.Draw(c, table, new UiPointer(new Vector2(5f, 5f), false, false, true), 4f, UiCursorKind.Grab));
        Assert.Equal("cursor.grab", Assert.Single(c.Calls).Name);
    }

    [Fact]
    public void ArtPixelsPerUnit_MatchesTheCursorsJson()
    {
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AssetPaths.UiDirectory, "hud", "cursors.json")));
        Assert.Equal(UiCursor.ArtPixelsPerUnit, doc.RootElement.GetProperty("scale").GetSingle());
    }

    // The sword's drawn tip (the real cursors.json pivot) lands on the pointer, whatever the layout scale.
    [Theory]
    [InlineData(1f)]
    [InlineData(2f)]
    [InlineData(3f)]
    [InlineData(4f)]
    [InlineData(5.5f)]
    [InlineData(8f)]
    public void AttackTip_LandsOnThePointerAtEveryLayoutScale(float scale)
    {
        UiSprite sprite = StatusBarTestData.Table.Get(UiSpriteNames.CursorAttack);
        float k = scale / UiCursor.ArtPixelsPerUnit;
        foreach (Vector2 at in new[] { new Vector2(400f, 300f), new Vector2(0f, 0f), new Vector2(123.4f, 77.7f) })
        {
            UiRect r = UiCursor.Rect(in sprite, at, scale);
            // The origin is rounded to a whole pixel, so the tip is within half a pixel.
            Assert.InRange(r.X + sprite.PivotX * k - at.X, -0.5f, 0.5f);
            Assert.InRange(r.Y + sprite.PivotY * k - at.Y, -0.5f, 0.5f);
            Assert.Equal(sprite.W * k, r.W, 3);
            Assert.Equal(sprite.H * k, r.H, 3);
        }
    }

    [Fact]
    public void AttackPivot_IsTheRealBladeTipInsideTheTopLeftQuarter()
    {
        UiSprite sprite = StatusBarTestData.Table.Get(UiSpriteNames.CursorAttack);
        Assert.InRange(sprite.PivotX, 0f, 63f);
        Assert.InRange(sprite.PivotY, 0f, 63f);
    }
}
