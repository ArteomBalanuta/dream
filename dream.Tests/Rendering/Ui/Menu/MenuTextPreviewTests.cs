using Dream.Assets;
using Dream.Rendering.Ui;
using Dream.Rendering.Ui.Menu;
using Dream.Tests.Support;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Rendering.Ui.Menu;

// Review PNGs of the four menu screens with the real baked font, at the three sizes the type has to
// hold up at. Always written (forceWrite) into artifacts/hud-preview/ so the menus can be judged by
// eye: engraved stone, not a settings dialog.
public sealed class MenuTextPreviewTests
{
    // 1024x640 is scale 1.25: the smallest window where the engraved face takes over from the pixel
    // one (13 u = 16 px, just over UiText.TitleMinPx), so the crossover can be judged by eye too.
    public static TheoryData<int, int> Sizes => new() { { 1024, 640 }, { 1280, 720 }, { 1920, 1080 }, { 3840, 2160 } };

    private static UiSoftwareCompositor Render(string name, int width, int height, Func<MenuWidgets, MenuAction> build)
    {
        (UiGlyphTable glyphs, UiTexturePixels page) = RealFont.Get();
        return HudPreview.Render(
            name,
            width,
            height,
            (batch, table, layout) =>
            {
                MenuSkin skin = MenuSkin.From(table);
                UiInputRouter router = new();
                MenuWidgets widgets = new();
                router.BeginFrame(new UiPointer(new Vector2(-1000f, -1000f), false, false, true));
                widgets.Begin(batch, router, skin, in layout);
                build(widgets);
                router.EndFrame();
            },
            glyphs: glyphs,
            fontPage: page,
            forceWrite: true);
    }

    // Any pixel of the text colour inside the box: the glyphs reached the framebuffer at this size,
    // so a preview that silently lost its text fails rather than being written empty.
    private static bool HasBrightPixels(UiSoftwareCompositor c, UiRect box, int threshold)
    {
        int x0 = Math.Max(0, (int)box.X);
        int y0 = Math.Max(0, (int)box.Y);
        int x1 = Math.Min(c.Width, (int)box.Right);
        int y1 = Math.Min(c.Height, (int)box.Bottom);
        for (int y = y0; y < y1; y++)
        {
            for (int x = x0; x < x1; x++)
            {
                SixLabors.ImageSharp.PixelFormats.Rgba32 p = c.GetRgba(x, y);
                if (p.R >= threshold && p.G >= threshold)
                {
                    return true;
                }
            }
        }

        return false;
    }

    // The centred 480-unit design box the menus lay themselves out in, full viewport width.
    private static UiRect DesignBox(int width, int height)
    {
        UiLayout layout = UiLayout.Create(width, height, 0f);
        float boxHeight = MathF.Min(height, MenuWidgets.DesignHeightUnits * layout.Scale);
        float originY = MathF.Max(0f, (height - MenuWidgets.DesignHeightUnits * layout.Scale) * 0.5f);
        return new UiRect(0f, originY, width, boxHeight);
    }

    // The scanlines inside box that hold a text-coloured pixel, grouped into runs of consecutive
    // lines. One run is one band of drawn text (a title, a row, a button label).
    private static List<(int Top, int Bottom)> TextBands(UiSoftwareCompositor c, UiRect box, int threshold)
    {
        int x0 = Math.Max(0, (int)box.X);
        int y0 = Math.Max(0, (int)box.Y);
        int x1 = Math.Min(c.Width, (int)box.Right);
        int y1 = Math.Min(c.Height, (int)box.Bottom);
        List<(int Top, int Bottom)> bands = [];
        bool inBand = false;
        int bandTop = 0;
        for (int y = y0; y < y1; y++)
        {
            bool bright = false;
            for (int x = x0; x < x1 && !bright; x++)
            {
                SixLabors.ImageSharp.PixelFormats.Rgba32 p = c.GetRgba(x, y);
                bright = p.R >= threshold && p.G >= threshold;
            }

            if (bright && !inBand)
            {
                inBand = true;
                bandTop = y;
            }
            else if (!bright && inBand)
            {
                inBand = false;
                bands.Add((bandTop, y - 1));
            }
        }

        if (inBand)
        {
            bands.Add((bandTop, y1 - 1));
        }

        return bands;
    }

    // What every one of these four screens establishes, and all a preview renderer honestly can:
    // the panel really reached the framebuffer at this size. Each of them draws a title or heading,
    // several text rows and a button label, so the drawn text has to appear as several separate
    // horizontal bands spread down the design box. That fails for the whole family of ways a
    // preview can be silently useless - zero glyphs, pure black, everything clipped off-screen,
    // every row collapsed onto one line, the panel drawn outside the centred box - while asking
    // nothing about the art, which is judged from the PNG by eye.
    private static void AssertPanelDrewText(UiSoftwareCompositor c, string name, int width, int height)
    {
        UiRect box = DesignBox(width, height);
        List<(int Top, int Bottom)> bands = TextBands(c, box, 150);
        Assert.True(bands.Count >= 3, $"{name} at {width}x{height}: {bands.Count} bands of text drew inside the design box");
        float span = bands[^1].Bottom - bands[0].Top;
        Assert.True(
            span >= box.H * 0.2f,
            $"{name} at {width}x{height}: the drawn text spans {span:0} px of a {box.H:0} px design box");
    }

    [Theory]
    [MemberData(nameof(Sizes))]
    public void MainMenu(int width, int height)
    {
        UiSoftwareCompositor c = Render(
            "menu-main",
            width,
            height,
            w => MainMenuPanel.Build(w, busy: false, "Session ended: the host closed the world", true));
        UiLayout layout = UiLayout.Create(width, height, 0f);
        float originY = MathF.Max(0f, (height - MenuWidgets.DesignHeightUnits * layout.Scale) * 0.5f);
        UiRect firstButton = new(
            (width - MenuWidgets.ButtonWidthUnits * layout.Scale) * 0.5f,
            originY + MainMenuPanel.ButtonsTopUnits * layout.Scale,
            MenuWidgets.ButtonWidthUnits * layout.Scale,
            MenuWidgets.ButtonHeightUnits * layout.Scale);

        Assert.True(HasBrightPixels(c, firstButton, 150), "the first button's label did not draw");
    }

    [Theory]
    [MemberData(nameof(Sizes))]
    public void Credits(int width, int height)
    {
        AssertPanelDrewText(Render("menu-credits", width, height, MainMenuPanel.BuildCredits), "menu-credits", width, height);
    }

    [Theory]
    [MemberData(nameof(Sizes))]
    public void Options(int width, int height)
    {
        AssertPanelDrewText(
            Render("menu-options", width, height, w => OptionsPanel.Build(w, MenuTypographyTests.OptionRows(), backdrop: true)),
            "menu-options",
            width,
            height);
    }

    [Theory]
    [MemberData(nameof(Sizes))]
    public void Pause(int width, int height)
    {
        AssertPanelDrewText(
            Render("menu-pause", width, height, w => PauseMenuPanel.Build(w, showDeveloper: true)),
            "menu-pause",
            width,
            height);
    }

    [Theory]
    [MemberData(nameof(Sizes))]
    public void Keys(int width, int height)
    {
        AssertPanelDrewText(
            Render("menu-keys", width, height, w => KeyBindingsPanel.Build(w, backdrop: true)),
            "menu-keys",
            width,
            height);
    }
}
