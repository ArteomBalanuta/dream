using Dream.Rendering.Core;

namespace Dream.Rendering.Ui.Gl;

// Reads the sprite JSON and page PNGs from the ui directory. Any failure throws; UiAtlasStore turns
// that into the Failed state.
public sealed class UiAtlasFileLoader(string uiDirectory) : IUiAtlasLoader
{
    public UiSpriteTable LoadTable()
    {
        if (!UiSpriteTable.TryLoad(uiDirectory, out UiSpriteTable? table, out string error))
        {
            throw new UiSpriteTableException(error);
        }

        return table!;
    }

    public DecodedTexture DecodePage(UiPage page)
    {
        string? relative = UiSpriteTable.PagePngPath(page);
        if (relative == null)
        {
            throw new InvalidOperationException($"{page} has no image file.");
        }

        return TextureDecoder.DecodeRgba(Path.Combine(uiDirectory, relative));
    }
}
