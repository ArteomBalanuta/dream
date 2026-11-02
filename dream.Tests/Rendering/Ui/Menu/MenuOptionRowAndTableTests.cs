using Dream.Assets;
using Dream.Rendering.Ui;
using Dream.Rendering.Ui.Menu;
using Xunit;

namespace Dream.Tests.Rendering.Ui.Menu;

public class MenuOptionRowAndTableTests
{
    [Fact]
    public void Slider_SnapsToStepAndClamps()
    {
        MenuOptionRow row = MenuOptionRow.Slider("x", 0.5f, 2.5f, 0.25f, () => 1f, _ => { });

        Assert.Equal(1.5f, row.FromUnit(0.5f));
        Assert.Equal(0.5f, row.FromUnit(-3f));
        Assert.Equal(2.5f, row.FromUnit(9f));
        Assert.Equal(1.25f, row.FromUnit(0.38f), 3);
        Assert.Equal(0.5f, row.ToUnit(1.5f));
        Assert.Equal(0f, row.ToUnit(-10f));
        Assert.Equal(1f, row.ToUnit(10f));
    }

    [Fact]
    public void Rows_FormatAndEnabledDefaults()
    {
        MenuOptionRow plain = MenuOptionRow.Slider("x", 0f, 1f, 0f, () => 0f, _ => { });
        MenuOptionRow custom = MenuOptionRow.Slider("x", 0f, 1f, 0f, () => 0f, _ => { }, v => $"<{v}>");
        MenuOptionRow off = MenuOptionRow.Toggle("t", () => false, _ => { }, isEnabled: () => false);

        Assert.Equal("0.5", plain.FormatValue(0.5f));
        Assert.Equal("<1>", custom.FormatValue(1f));
        Assert.True(plain.Enabled);
        Assert.False(off.Enabled);
        Assert.Equal(MenuOptionKind.Header, MenuOptionRow.Header("h").Kind);
    }

    [Fact]
    public void MenuPage_LoadsWithTheWindowPage_AndTheHudTableDoesNotNeedIt()
    {
        Assert.True(UiSpriteTable.TryLoadPages(AssetPaths.UiDirectory, MenuAtlasLoader.Pages, out UiSpriteTable? table, out string error), error);
        Assert.True(table!.TryGetPageSize(UiPage.Menu, out int w, out int h));
        Assert.True(w > 0 && h > 0);
        foreach (string name in new[]
        {
            MenuSkin.Backdrop, MenuSkin.Title, MenuSkin.ButtonNormal, MenuSkin.ButtonHover, MenuSkin.ButtonPressed,
            MenuSkin.ButtonDisabled, MenuSkin.Panel, MenuSkin.SliderTrack, MenuSkin.SliderKnob, MenuSkin.Divider
        })
        {
            Assert.True(table.Contains(name), name);
        }

        Assert.Equal("menu/menu.png", UiSpriteTable.PagePngPath(UiPage.Menu));
        Assert.True(File.Exists(AssetPaths.Ui("menu/menu.png")));

        Assert.True(UiSpriteTable.TryLoad(AssetPaths.UiDirectory, out UiSpriteTable? hud, out string hudError), hudError);
        Assert.False(hud!.Contains(MenuSkin.Title));
        Assert.False(hud.TryGetPageSize(UiPage.Menu, out _, out _));
    }

    [Fact]
    public void TryLoadPages_ReportsAMissingDirectoryWithoutThrowing()
    {
        string missing = Path.Combine(Path.GetTempPath(), "dream-no-ui-" + Guid.NewGuid().ToString("N"));

        Assert.False(UiSpriteTable.TryLoadPages(missing, MenuAtlasLoader.Pages, out UiSpriteTable? table, out string error));
        Assert.Null(table);
        Assert.NotEmpty(error);
    }

    [Fact]
    public void AtlasStore_LoadsOnlyTheRequestedPages()
    {
        List<UiPage> decoded = [];
        FakeLoader loader = new(decoded);
        FakeUploader uploader = new();
        UiAtlasStore store = new(loader, uploader, pages: [UiPage.Window, UiPage.Menu]);
        store.Start();

        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (store.State == UiAtlasState.Loading)
        {
            store.Update();
            Assert.True(DateTime.UtcNow < deadline, "atlas store never settled");
            Thread.Sleep(1);
        }

        Assert.Equal(UiAtlasState.Ready, store.State);
        Assert.Equal(2, uploader.Uploaded.Count);
        Assert.NotEqual(0, store.GetTexture(UiPage.Menu));
        Assert.Equal(0, store.GetTexture(UiPage.Hud));
        Assert.DoesNotContain(UiPage.Hud, decoded);
    }

    private sealed class FakeLoader(List<UiPage> decoded) : IUiAtlasLoader
    {
        public UiSpriteTable LoadTable()
        {
            UiSpriteTable t = new();
            t.AddPage(UiPage.Window, "{\"size\":[8,8],\"sprites\":{\"w.s\":{\"x\":0,\"y\":0,\"w\":4,\"h\":4}}}", 8, 8);
            t.AddPage(UiPage.Menu, "{\"size\":[8,8],\"sprites\":{\"m.s\":{\"x\":0,\"y\":0,\"w\":4,\"h\":4}}}", 8, 8);
            return t;
        }

        public Dream.Rendering.Core.DecodedTexture DecodePage(UiPage page)
        {
            lock (decoded)
            {
                decoded.Add(page);
            }

            return new Dream.Rendering.Core.DecodedTexture(8, 8, new byte[8 * 8 * 4]);
        }
    }

    private sealed class FakeUploader : IUiTextureUploader
    {
        private int _next = 50;

        public List<UiPage> Uploaded { get; } = [];

        public int Upload(UiPage page, Dream.Rendering.Core.DecodedTexture texture)
        {
            Uploaded.Add(page);
            return _next++;
        }

        public void Delete(int handle)
        {
        }
    }
}
