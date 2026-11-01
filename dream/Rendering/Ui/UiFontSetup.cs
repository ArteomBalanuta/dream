using ImGuiNET;

namespace Dream.Rendering.Ui;

// The one place that decides which font the classic HUD uses: ImGui's built-in default font baked at
// two sizes. Swapping in a licensed font later means changing AddFonts only.
public static unsafe class UiFontSetup
{
    public const float BodyPixels = 13f;
    public const float LargePixels = 26f;

    // Adds the body font (atlas index 0, which is the one the ImGui windows keep using) and the
    // large bake (index 1). Call before the font atlas texture is built.
    public static void AddFonts(ImGuiIOPtr io)
    {
        io.Fonts.AddFontDefault();

        ImFontConfigPtr config = ImGuiNative.ImFontConfig_ImFontConfig();
        try
        {
            config.SizePixels = LargePixels;
            io.Fonts.AddFontDefault(config);
        }
        finally
        {
            ImGuiNative.ImFontConfig_destroy(config.NativePtr);
        }
    }

    // Copies glyphs 32..255 of the two baked fonts. Call after the atlas is built (after
    // GetTexDataAsRGBA32). Throws InvalidOperationException when a font is missing or has no '?'.
    public static UiGlyphTable BuildGlyphTable(ImFontAtlasPtr atlas)
    {
        if (atlas.Fonts.Size < 2)
        {
            throw new InvalidOperationException("The UI font atlas does not contain the body and large fonts.");
        }

        ImFontPtr body = atlas.Fonts[0];
        ImFontPtr large = atlas.Fonts[1];
        UiGlyphTable table = new(body.FontSize, large.FontSize);
        Fill(table, UiTextStyle.Body, body);
        Fill(table, UiTextStyle.Large, large);
        if (!table.Has(UiTextStyle.Body, UiGlyphTable.Fallback) || !table.Has(UiTextStyle.Large, UiGlyphTable.Fallback))
        {
            throw new InvalidOperationException("The UI font has no '?' glyph.");
        }

        return table;
    }

    // Reads each glyph from native memory. ImGui.NET 1.91.6.1's ImFontGlyph wrapper does not match
    // the native struct (it reads shifted fields and has a different size), so the 1.91.6 layout is
    // read directly: one word of bitfields (Colored:1, Visible:1, Codepoint:30), then AdvanceX,
    // X0, Y0, X1, Y1, U0, V0, U1, V1. The codepoint in the first word is checked against the
    // requested one, so a layout change fails here (and the HUD falls back) instead of drawing junk.
    private static void Fill(UiGlyphTable table, UiTextStyle style, ImFontPtr font)
    {
        for (int c = UiGlyphTable.FirstCodepoint; c <= UiGlyphTable.LastCodepoint; c++)
        {
            ImFontGlyphPtr g = font.FindGlyphNoFallback((ushort)c);
            if (g.NativePtr == null)
            {
                continue;
            }

            uint* words = (uint*)g.NativePtr;
            if ((words[0] >> 2) != (uint)c)
            {
                throw new InvalidOperationException($"The ImGui glyph layout is not the expected one (glyph {c} reads codepoint {words[0] >> 2}).");
            }

            float* f = (float*)(words + 1);
            table.Set(style, (char)c, new UiGlyph(f[1], f[2], f[3], f[4], f[5], f[6], f[7], f[8], f[0]));
        }
    }
}
