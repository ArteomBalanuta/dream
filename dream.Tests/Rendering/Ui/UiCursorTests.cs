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

    [Theory]
    [InlineData(false, false, HoverKind.None, UiCursorKind.Pointer)]
    [InlineData(false, true, HoverKind.None, UiCursorKind.Pressed)]
    [InlineData(false, false, HoverKind.Enemy, UiCursorKind.Attack)]
    [InlineData(false, true, HoverKind.Enemy, UiCursorKind.Attack)]
    [InlineData(false, false, HoverKind.Npc, UiCursorKind.Talk)]
    [InlineData(false, true, HoverKind.Npc, UiCursorKind.Talk)]
    [InlineData(false, false, HoverKind.GroundItem, UiCursorKind.Take)]
    [InlineData(false, true, HoverKind.GroundItem, UiCursorKind.Take)]
    [InlineData(false, false, HoverKind.Interactable, UiCursorKind.Use)]
    [InlineData(false, true, HoverKind.Interactable, UiCursorKind.Use)]
    [InlineData(false, false, HoverKind.RemotePlayer, UiCursorKind.Pointer)]
    [InlineData(false, true, HoverKind.RemotePlayer, UiCursorKind.Pressed)]
    [InlineData(true, false, HoverKind.None, UiCursorKind.Grab)]
    [InlineData(true, true, HoverKind.Enemy, UiCursorKind.Grab)]
    [InlineData(true, false, HoverKind.Npc, UiCursorKind.Grab)]
    [InlineData(true, true, HoverKind.GroundItem, UiCursorKind.Grab)]
    [InlineData(true, false, HoverKind.Interactable, UiCursorKind.Grab)]
    public void Select_ByHoverKind_FollowsPrecedence(bool dragging, bool leftDown, HoverKind hovered, UiCursorKind expected) =>
        Assert.Equal(expected, UiCursor.Select(dragging, leftDown, hovered));

    [Fact]
    public void BoolOverload_MapsOverEnemyToEnemy()
    {
        Assert.Equal(UiCursor.Select(false, false, HoverKind.Enemy), UiCursor.Select(false, false, true));
        Assert.Equal(UiCursor.Select(false, true, HoverKind.None), UiCursor.Select(false, true, false));
    }

    [Fact]
    public void SpriteName_MapsTheWorldKinds()
    {
        Assert.Equal(UiSpriteNames.CursorTalk, UiCursor.SpriteName(UiCursorKind.Talk));
        Assert.Equal(UiSpriteNames.CursorTake, UiCursor.SpriteName(UiCursorKind.Take));
        Assert.Equal(UiSpriteNames.CursorUse, UiCursor.SpriteName(UiCursorKind.Use));
    }

    [Theory]
    [InlineData(UiSpriteNames.CursorTalk)]
    [InlineData(UiSpriteNames.CursorTake)]
    [InlineData(UiSpriteNames.CursorUse)]
    public void WorldCursorPivots_AreOnTheArtInTheTopLeftQuarter(string name)
    {
        UiSprite sprite = StatusBarTestData.Table.Get(name);
        Assert.InRange(sprite.PivotX, 0f, 63f);
        Assert.InRange(sprite.PivotY, 0f, 63f);
        Assert.Equal(128, sprite.W);
        Assert.Equal(128, sprite.H);
    }

    [Fact]
    public void EveryWorldKind_DrawsItsOwnSprite()
    {
        UiSpriteTable table = StatusBarTestData.Table;
        foreach ((UiCursorKind kind, string name) in new[]
        {
            (UiCursorKind.Talk, "cursor.talk"), (UiCursorKind.Take, "cursor.take"), (UiCursorKind.Use, "cursor.use")
        })
        {
            StatusBarRecordingCanvas c = new(table);
            Assert.True(UiCursor.Draw(c, table, new UiPointer(new Vector2(5f, 5f), false, false, true), 4f, kind));
            Assert.Equal(name, Assert.Single(c.Calls).Name);
        }
    }

    // An atlas from before these cursors has no talk/take/use sprite: the pointer is drawn, never nothing.
    [Fact]
    public void AMissingKindSprite_FallsBackToThePointer()
    {
        UiSpriteTable older = CursorsOnly(UiSpriteNames.CursorPointer);
        StatusBarRecordingCanvas c = new(StatusBarTestData.Table);

        Assert.True(UiCursor.Draw(c, older, new UiPointer(new Vector2(5f, 5f), false, false, true), 4f, UiCursorKind.Talk));
        Assert.Equal("cursor.pointer", Assert.Single(c.Calls).Name);

        // With no pointer sprite either, nothing is drawn and nothing throws.
        c.Calls.Clear();
        Assert.False(UiCursor.Draw(c, CursorsOnly(UiSpriteNames.CursorGrab), new UiPointer(new Vector2(5f, 5f), false, false, true), 4f, UiCursorKind.Talk));
        Assert.Empty(c.Calls);
    }

    // The real cursors page cut down to the named sprites.
    private static UiSpriteTable CursorsOnly(params string[] keep)
    {
        string path = Path.Combine(AssetPaths.UiDirectory, "hud", "cursors.json");
        System.Text.Json.Nodes.JsonNode root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
        System.Text.Json.Nodes.JsonObject sprites = root["sprites"]!.AsObject();
        foreach (string name in sprites.Select(kv => kv.Key).ToArray())
        {
            if (!keep.Contains(name))
            {
                sprites.Remove(name);
            }
        }

        (int w, int h) = UiSpriteTable.ReadPngSize(Path.Combine(AssetPaths.UiDirectory, "hud", "cursors.png"));
        UiSpriteTable table = new();
        table.AddPage(UiPage.Cursors, root.ToJsonString(), w, h);
        return table;
    }

    [Fact]
    public void AttackPivot_IsTheRealBladeTipInsideTheTopLeftQuarter()
    {
        UiSprite sprite = StatusBarTestData.Table.Get(UiSpriteNames.CursorAttack);
        Assert.InRange(sprite.PivotX, 0f, 63f);
        Assert.InRange(sprite.PivotY, 0f, 63f);
    }
}
