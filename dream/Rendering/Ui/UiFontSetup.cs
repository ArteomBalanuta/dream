using ImGuiNET;

namespace Dream.Rendering.Ui;

// The one place that decides which fonts the classic HUD uses, baked in three sizes:
//   Body  (atlas 0)  ImGui's built-in ProggyClean at 13 px. Every panel lays out in 13 px lines, and a
//                    thin serif is not legible that small, so the small text stays on the pixel face.
//   Large (atlas 1)  Cinzel (engraved Roman capitals) at 26 px: the status bar numbers and titles.
//   Serif (atlas 2)  EB Garamond at 24 px: the stats window, which sizes its own text by ui_scale.
//   BodyLarge (atlas 3)  the same ProggyClean at 26 px. Panel text follows ui_scale, so above about
//                    20 px the 13 px bake would be a stretched bitmap; the other two bakes are
//                    different faces, so the pixel face needs a bake of its own to grow into.
// Cinzel and EB Garamond are third-party SIL OFL fonts in dream/fonts (see the README there). Both are
// variable fonts; stb_truetype ignores the wght axis and renders the default instance (Regular, 400).
// A missing file falls back to the built-in face at that size, so the HUD still draws.
public static unsafe class UiFontSetup
{
    public const float BodyPixels = 13f;
    public const float LargePixels = 26f;
    public const float SerifPixels = 24f;
    public const float BodyLargePixels = 26f;
    public const string LargeFontFile = "Cinzel.ttf";
    public const string SerifFontFile = "EBGaramond.ttf";

    // The Dear ImGui version whose glyph struct layout Fill reads (ImGui.NET 1.91.6.1 wraps it).
    // A different native version fails BuildGlyphTable, so the HUD falls back instead of reading junk.
    public const string AuditedImGuiVersion = "1.91.6";

    private static readonly UiTextStyle[] AllStyles =
        [UiTextStyle.Body, UiTextStyle.Large, UiTextStyle.Serif, UiTextStyle.BodyLarge];

    // Adds the body font (atlas index 0, which is the one the ImGui windows keep using), the large
    // bake (index 1) and the serif bake (index 2). Call before the font atlas texture is built.
    public static void AddFonts(ImGuiIOPtr io)
    {
        io.Fonts.AddFontDefault();
        AddFace(io, LargeFontFile, LargePixels);
        AddFace(io, SerifFontFile, SerifPixels);
        AddDefaultFace(io, BodyLargePixels);
    }

    // The built-in face at size. ProggyClean is a bitmap face designed at 13 px, so 26 px is exactly
    // two texels per pixel and stays crisp.
    private static void AddDefaultFace(ImGuiIOPtr io, float size)
    {
        ImFontConfigPtr config = ImGuiNative.ImFontConfig_ImFontConfig();
        try
        {
            config.SizePixels = size;
            io.Fonts.AddFontDefault(config);
        }
        finally
        {
            ImGuiNative.ImFontConfig_destroy(config.NativePtr);
        }
    }

    // The file's font at size, or the built-in face at size when the file is not there (ImGui asserts on
    // a missing file, so it is checked first).
    private static void AddFace(ImGuiIOPtr io, string fileName, float size)
    {
        string path = Dream.Assets.AssetPaths.Font(fileName);
        if (File.Exists(path))
        {
            io.Fonts.AddFontFromFileTTF(path, size);
            return;
        }

        AddDefaultFace(io, size);
    }

    // Copies glyphs 32..255 of the two baked fonts. Call after the atlas is built (after
    // GetTexDataAsRGBA32). Throws InvalidOperationException when a font is missing or has no '?'.
    public static UiGlyphTable BuildGlyphTable(ImFontAtlasPtr atlas)
    {
        string version = ImGui.GetVersion();
        if (!string.Equals(version, AuditedImGuiVersion, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Dear ImGui {version} is loaded; the glyph layout was audited for {AuditedImGuiVersion}.");
        }

        if (atlas.Fonts.Size < 4)
        {
            throw new InvalidOperationException("The UI font atlas does not contain the body, large, serif and body-large fonts.");
        }

        ImFontPtr body = atlas.Fonts[0];
        ImFontPtr large = atlas.Fonts[1];
        ImFontPtr serif = atlas.Fonts[2];
        ImFontPtr bodyLarge = atlas.Fonts[3];
        UiGlyphTable table = new(body.FontSize, large.FontSize, serif.FontSize, bodyLarge.FontSize);
        Fill(table, UiTextStyle.Body, body);
        Fill(table, UiTextStyle.Large, large);
        Fill(table, UiTextStyle.Serif, serif);
        Fill(table, UiTextStyle.BodyLarge, bodyLarge);
        foreach (UiTextStyle style in AllStyles)
        {
            if (!table.Has(style, UiGlyphTable.Fallback))
            {
                throw new InvalidOperationException($"The {style} UI font has no '?' glyph.");
            }
        }

        // Every missing character draws '?', so a misread struct would show up everywhere: check it.
        foreach (UiTextStyle style in AllStyles)
        {
            table.TryGet(style, UiGlyphTable.Fallback, out UiGlyph question);
            if (GlyphFault(question) is { } fault)
            {
                throw new InvalidOperationException($"The {style} '?' glyph is not sane: {fault}.");
            }
        }

        return table;
    }

    // Null when the glyph can be drawn: a positive width, texture coordinates inside 0..1 (and in
    // order) and a positive advance. Otherwise what is wrong.
    internal static string? GlyphFault(in UiGlyph g)
    {
        if (!float.IsFinite(g.X0) || !float.IsFinite(g.X1) || !(g.X1 > g.X0))
        {
            return "X1 is not greater than X0";
        }

        if (!float.IsFinite(g.Y0) || !float.IsFinite(g.Y1))
        {
            return "Y is not finite";
        }

        if (!InUnit(g.U0) || !InUnit(g.V0) || !InUnit(g.U1) || !InUnit(g.V1))
        {
            return "texture coordinates are outside 0..1";
        }

        if (!float.IsFinite(g.Advance) || !(g.Advance > 0f))
        {
            return "the advance is not positive";
        }

        return null;
    }

    private static bool InUnit(float v) => float.IsFinite(v) && v >= 0f && v <= 1f;

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
