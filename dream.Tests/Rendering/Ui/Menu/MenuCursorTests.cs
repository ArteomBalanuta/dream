using Dream.Assets;
using Dream.Rendering.Ui;
using Dream.Rendering.Ui.Menu;
using Xunit;

namespace Dream.Tests.Rendering.Ui.Menu;

public class MenuCursorTests
{
    [Fact]
    public void MenuAtlasLoader_LoadsTheCursorsPage_AndResolvesThePointer()
    {
        Assert.Contains(UiPage.Cursors, MenuAtlasLoader.Pages);

        UiSpriteTable table = new MenuAtlasLoader(AssetPaths.UiDirectory).LoadTable();

        Assert.True(table.TryGet(UiSpriteNames.CursorPointer, out UiSprite sprite));
        Assert.Equal(UiPage.Cursors, sprite.Page);
    }
}
