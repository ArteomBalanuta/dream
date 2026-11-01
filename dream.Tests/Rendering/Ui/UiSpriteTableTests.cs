using System.Text.Json;
using Xunit;
using Dream.Assets;
using Dream.Rendering.Ui;

namespace Dream.Tests.Rendering.Ui;

public class UiSpriteTableTests
{
    private static UiSpriteTable LoadReal()
    {
        Assert.True(UiSpriteTable.TryLoad(AssetPaths.UiDirectory, out UiSpriteTable? table, out string error), error);
        return table!;
    }

    private static string Json(string sprites, string size = "[100, 100]") =>
        "{\"size\": " + size + ", \"sprites\": {" + sprites + "}}";

    private static string Sprite(string name, string body = "\"x\":0,\"y\":0,\"w\":10,\"h\":10") =>
        "\"" + name + "\": {" + body + "}";

    [Fact]
    public void RealFiles_ParseAndValidate()
    {
        UiSpriteTable table = LoadReal();

        Assert.True(table.Count > 150);
        foreach (string name in UiSpriteNames.Required)
        {
            Assert.True(table.Contains(name), name);
        }

        Assert.True(table.TryGetPageSize(UiPage.Hud, out int w, out int h));
        Assert.Equal((1692, 1452), (w, h));
    }

    [Fact]
    public void RealFiles_ItemFootprintsAndPages()
    {
        UiSpriteTable table = LoadReal();

        UiSprite axe = table.Get("item.axe.twinbit");
        Assert.Equal(UiPage.ItemsCommon, axe.Page);
        Assert.Equal((2, 3), (axe.FootprintCols, axe.FootprintRows));
        Assert.Equal(UiPage.Window, table.Get(UiSpriteNames.TipPanel).Page);
        Assert.Equal(UiPage.Digits, table.Get(UiSpriteNames.Digit(7)).Page);
        Assert.Equal(UiPage.Cursors, table.Get(UiSpriteNames.CursorPointer).Page);
    }

    [Fact]
    public void RealFiles_SliceAndTileAreParsed()
    {
        UiSpriteTable table = LoadReal();

        UiSprite band = table.Get(UiSpriteNames.TipBand);
        Assert.Equal((16, 0, 16, 0), (band.SliceL, band.SliceT, band.SliceR, band.SliceB));
        Assert.True(band.HasSlice);
        Assert.False(table.Get(UiSpriteNames.CellEmpty).HasSlice);
        Assert.Contains(table.Names, n => table.Get(n).Tile != UiTile.None);
    }

    [Fact]
    public void EveryItemInItemsJson_HasASprite()
    {
        UiSpriteTable table = LoadReal();
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(AssetPaths.Config("content/items.json")));

        int count = 0;
        foreach (JsonElement item in doc.RootElement.GetProperty("items").EnumerateArray())
        {
            string key = item.GetProperty("key").GetString()!;
            Assert.True(table.Contains(UiSpriteNames.Item(key)), key);
            count++;
        }

        Assert.True(count >= 9);
    }

    [Fact]
    public void NoSpriteNameUsesABannedWord()
    {
        string[] banned = ["zen", "chaos", "webzen", "mu", "muonline"];
        UiSpriteTable table = LoadReal();

        foreach (string name in table.Names)
        {
            foreach (string token in name.Split('.', '-', '_'))
            {
                Assert.DoesNotContain(token.ToLowerInvariant(), banned);
            }
        }
    }

    [Fact]
    public void TierPages_HaveAPngForEachTier()
    {
        Assert.Equal("items/items_uncommon.png", UiSpriteTable.PagePngPath(UiPage.ItemsUncommon));
        Assert.Equal("items/items_rare.png", UiSpriteTable.PagePngPath(UiPage.ItemsRare));
        Assert.Null(UiSpriteTable.PagePngPath(UiPage.Font));
        Assert.True(File.Exists(AssetPaths.Ui(UiSpriteTable.PagePngPath(UiPage.ItemsRare)!)));
    }

    [Fact]
    public void AddPage_ParsesAllFields()
    {
        UiSpriteTable t = new();
        t.AddPage(UiPage.Window, Json(Sprite("a.b",
            "\"x\":4,\"y\":6,\"w\":20,\"h\":30,\"pivot\":[1,2],\"slice\":[3,4,5,6],\"tile\":\"xy\",\"meta\":{\"footprint\":[2,3]}")), 100, 100);

        UiSprite s = t.Get("a.b");
        Assert.Equal(new UiSprite(UiPage.Window, 4, 6, 20, 30, 1, 2, 3, 4, 5, 6, UiTile.XY, 2, 3), s);
    }

    [Fact]
    public void AddPage_NullSliceAndNoMeta_Defaults()
    {
        UiSpriteTable t = new();
        t.AddPage(UiPage.Hud, Json(Sprite("a", "\"x\":0,\"y\":0,\"w\":5,\"h\":5,\"slice\":null,\"tile\":\"none\",\"meta\":{}")), 100, 100);

        UiSprite s = t.Get("a");
        Assert.False(s.HasSlice);
        Assert.Equal((1, 1), (s.FootprintCols, s.FootprintRows));
    }

    [Theory]
    [InlineData("\"x\":95,\"y\":0,\"w\":10,\"h\":10")]
    [InlineData("\"x\":0,\"y\":91,\"w\":10,\"h\":10")]
    [InlineData("\"x\":-1,\"y\":0,\"w\":10,\"h\":10")]
    [InlineData("\"x\":0,\"y\":0,\"w\":0,\"h\":10")]
    [InlineData("\"x\":0,\"y\":0,\"w\":10,\"h\":10,\"slice\":[6,0,6,0]")]
    [InlineData("\"x\":0,\"y\":0,\"w\":10,\"h\":10,\"slice\":[1,2,3]")]
    [InlineData("\"x\":0,\"y\":0,\"w\":10,\"h\":10,\"tile\":\"diagonal\"")]
    [InlineData("\"x\":0,\"y\":0,\"w\":10,\"h\":10,\"meta\":{\"footprint\":[0,1]}")]
    [InlineData("\"y\":0,\"w\":10,\"h\":10")]
    public void AddPage_RejectsBadSprites(string body)
    {
        UiSpriteTable t = new();

        Assert.Throws<UiSpriteTableException>(() => t.AddPage(UiPage.Window, Json(Sprite("bad", body)), 100, 100));
        Assert.Equal(0, t.Count);
    }

    [Fact]
    public void AddPage_RejectsSizeMismatch()
    {
        UiSpriteTable t = new();

        Assert.Throws<UiSpriteTableException>(() => t.AddPage(UiPage.Window, Json(Sprite("a"), "[100, 99]"), 100, 100));
    }

    [Fact]
    public void AddPage_RejectsDuplicateNameAcrossPages()
    {
        UiSpriteTable t = new();
        t.AddPage(UiPage.Window, Json(Sprite("a")), 100, 100);

        Assert.Throws<UiSpriteTableException>(() => t.AddPage(UiPage.Hud, Json(Sprite("a")), 100, 100));
    }

    [Fact]
    public void AddPage_BadJson_Throws()
    {
        UiSpriteTable t = new();

        Assert.ThrowsAny<JsonException>(() => t.AddPage(UiPage.Window, "{ not json", 100, 100));
        Assert.Throws<UiSpriteTableException>(() => t.AddPage(UiPage.Font, Json(Sprite("a")), 100, 100));
    }

    [Fact]
    public void Validate_ReportsAMissingRequiredSprite()
    {
        UiSpriteTable t = new();
        t.AddPage(UiPage.Window, Json(Sprite("only.one")), 100, 100);

        UiSpriteTableException ex = Assert.Throws<UiSpriteTableException>(t.Validate);
        Assert.Contains("Required", ex.Message);
    }

    [Fact]
    public void TryLoad_MissingDirectory_FailsWithoutThrowing()
    {
        string missing = Path.Combine(Path.GetTempPath(), "dream-no-such-ui-" + Guid.NewGuid().ToString("N"));

        Assert.False(UiSpriteTable.TryLoad(missing, out UiSpriteTable? table, out string error));
        Assert.Null(table);
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void TryLoad_CorruptPng_FailsWithoutThrowing()
    {
        string dir = Path.Combine(Path.GetTempPath(), "dream-ui-corrupt-" + Guid.NewGuid().ToString("N"));
        try
        {
            CopyDirectory(AssetPaths.UiDirectory, dir);
            File.WriteAllBytes(Path.Combine(dir, "hud", "hud.png"), [1, 2, 3, 4]);

            Assert.False(UiSpriteTable.TryLoad(dir, out UiSpriteTable? table, out string error));
            Assert.Null(table);
            Assert.Contains("hud.png", error);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void TryLoad_JsonSizeNotMatchingPng_Fails()
    {
        string dir = Path.Combine(Path.GetTempPath(), "dream-ui-size-" + Guid.NewGuid().ToString("N"));
        try
        {
            CopyDirectory(AssetPaths.UiDirectory, dir);
            // Swap in a PNG of another size under the cursors name.
            File.Copy(Path.Combine(dir, "hud", "hud.png"), Path.Combine(dir, "hud", "cursors.png"), overwrite: true);

            Assert.False(UiSpriteTable.TryLoad(dir, out _, out string error));
            Assert.Contains("size", error);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ClassicHud_Build_WithStubPanels_DoesNothing()
    {
        UiSpriteTable table = LoadReal();
        UiLayout layout = UiLayout.Create(1920, 1080, 0f);

        ClassicHud.Build(null!, null!, new HudView(), in layout, table, null!);
    }

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (string file in Directory.GetFiles(from))
        {
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
        }

        foreach (string sub in Directory.GetDirectories(from))
        {
            CopyDirectory(sub, Path.Combine(to, Path.GetFileName(sub)));
        }
    }
}
