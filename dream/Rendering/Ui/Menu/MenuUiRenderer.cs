using Dream.Assets;
using Dream.Rendering.Core;
using Dream.Rendering.Ui.Gl;
using ImGuiNET;

namespace Dream.Rendering.Ui.Menu;

// Loads only the pages the front end needs (the window plates and the menu art).
public sealed class MenuAtlasLoader(string uiDirectory) : IUiAtlasLoader
{
    public static readonly UiPage[] Pages = [UiPage.Window, UiPage.Menu, UiPage.Cursors];

    public UiSpriteTable LoadTable()
    {
        if (!UiSpriteTable.TryLoadPages(uiDirectory, Pages, out UiSpriteTable? table, out string error))
        {
            throw new UiSpriteTableException(error);
        }

        return table!;
    }

    public DecodedTexture DecodePage(UiPage page)
    {
        string relative = UiSpriteTable.PagePngPath(page) ?? throw new InvalidOperationException($"{page} has no image file.");
        return TextureDecoder.DecodeRgba(Path.Combine(uiDirectory, relative));
    }
}

// Draws the title screen and the pause menu with the classic sprite UI, independent of any play scene:
// its own atlas (window plates + menu art), batch, GL renderer, input router and font texture. The
// glyphs are baked once in a temporary ImGui context, which is destroyed again; the context that was
// current before (a play scene's) is made current again, so this can run between a scene's frames.
// Render thread only. Game owns one instance for the whole run.
public sealed class MenuUiRenderer : IDisposable
{
    private readonly UiGlyphTable _glyphs;
    private readonly int _fontTexture;
    private readonly UiGlRenderer _gl;
    private readonly UiAtlasStore _atlas;
    private readonly UiInputRouter _router = new();
    private readonly MenuWidgets _widgets = new();
    private UiBatch? _batch;
    private MenuSkin? _skin;
    private bool _disposed;

    private MenuUiRenderer(UiGlyphTable glyphs, int fontTexture, UiGlRenderer gl, UiAtlasStore atlas)
    {
        _glyphs = glyphs;
        _fontTexture = fontTexture;
        _gl = gl;
        _atlas = atlas;
    }

    // Idle while loading is not yet begun, Loading while the PNGs decode, Ready once drawable.
    public UiAtlasState State => _atlas.State;

    public string FailureReason => _atlas.FailureReason;

    // True when the last Draw put a sprite cursor on screen (the OS cursor is then hidden).
    public bool CursorDrawn { get; private set; }

    // True when a Draw this frame will put the sprite cursor on screen (given an interactive pointer): the
    // atlas is ready and has the pointer sprite. Lets the OS cursor be hidden for the same frame instead of
    // one frame late, which CursorDrawn (the last Draw) cannot do.
    public bool CursorWillDraw =>
        IsReady && _atlas.Table is { } table && table.TryGet(UiCursor.SpriteName(UiCursorKind.Pointer), out _);

    public bool IsReady => !_disposed && _atlas.State == UiAtlasState.Ready;

    // Null with a reason when the glyphs, shader or atlas cannot be set up; Game then skips the menus.
    public static MenuUiRenderer? TryCreate(out string error)
    {
        error = string.Empty;
        GLUtils.AssertRenderThread("MenuUiRenderer creation");
        IntPtr previous = ImGui.GetCurrentContext();
        IntPtr context = IntPtr.Zero;
        int fontTexture = 0;
        UiGlRenderer? gl = null;
        try
        {
            UiGlyphTable glyphs;
            context = ImGui.CreateContext();
            ImGui.SetCurrentContext(context);
            ImGuiIOPtr io = ImGui.GetIO();
            UiFontSetup.AddFonts(io);
            io.Fonts.GetTexDataAsRGBA32(out IntPtr pixels, out int width, out int height, out _);
            fontTexture = UiGlFontTexture.Upload(pixels, width, height);
            glyphs = UiFontSetup.BuildGlyphTable(io.Fonts);

            gl = new UiGlRenderer();
            UiAtlasStore atlas = new(
                new MenuAtlasLoader(AssetPaths.UiDirectory),
                new UiGlTextureUploader(),
                pages: MenuAtlasLoader.Pages);
            atlas.Start();
            return new MenuUiRenderer(glyphs, fontTexture, gl, atlas);
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            gl?.Dispose();
            if (fontTexture != 0)
            {
                UiGlFontTexture.Delete(fontTexture);
            }

            return null;
        }
        finally
        {
            if (context != IntPtr.Zero)
            {
                ImGui.DestroyContext(context);
            }

            // DestroyContext leaves no current context when it destroyed the current one.
            ImGui.SetCurrentContext(previous);
        }
    }

    // Builds and draws one frame. build runs with the widgets ready; it is skipped (and false is
    // returned) while the art is still loading or after it failed, so the caller can fall back.
    public bool Draw(int width, int height, float uiScaleSetting, UiPointer pointer, Action<MenuWidgets> build)
    {
        ArgumentNullException.ThrowIfNull(build);
        CursorDrawn = false;
        if (_disposed)
        {
            return false;
        }

        _atlas.Update();
        if (_atlas.State != UiAtlasState.Ready || _atlas.Table is not { } table)
        {
            return false;
        }

        if (_batch == null)
        {
            UiBatch batch = new(_glyphs);
            batch.SetSpriteTable(table);
            _batch = batch;
            _skin = MenuSkin.From(table);
        }

        UiLayout layout = UiLayout.Create(Math.Max(width, 1), Math.Max(height, 1), uiScaleSetting);
        _batch.BeginFrame(in layout);
        _router.BeginFrame(pointer);
        _widgets.Begin(_batch, _router, _skin!, in layout);
        try
        {
            build(_widgets);
        }
        finally
        {
            _router.EndFrame();
        }

        CursorDrawn = UiCursor.Draw(_batch, table, in pointer, layout.Scale, UiCursor.Select(_router.Dragging, pointer.LeftDown, false));
        _gl.Submit(_batch, _atlas, _fontTexture);
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _atlas.Dispose();
        }
        finally
        {
            try
            {
                _gl.Dispose();
            }
            finally
            {
                UiGlFontTexture.Delete(_fontTexture);
            }
        }
    }
}
