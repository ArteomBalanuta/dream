namespace Dream.Rendering.Ui;

// One baked glyph. X0..Y1 are pixel offsets from the pen position at the top of the line; U0..V1 are
// normalized coordinates on the font page (top-left origin); Advance is the pen step in pixels.
public readonly record struct UiGlyph(float X0, float Y0, float X1, float Y1, float U0, float V0, float U1, float V1, float Advance);

// Glyphs for the two text styles, copied once from the baked font so drawing text never touches
// ImGui. GL-free and ImGui-free: UiFontSetup fills it. Covers codepoints 32..255; anything the font
// does not have (or outside that range) draws the '?' glyph.
public sealed class UiGlyphTable
{
    public const int FirstCodepoint = 32;
    public const int LastCodepoint = 255;
    public const char Fallback = '?';

    private readonly UiGlyph?[][] _glyphs;
    private readonly float[] _lineHeights;

    public UiGlyphTable(float bodyLineHeight, float largeLineHeight)
    {
        _lineHeights = [bodyLineHeight, largeLineHeight];
        _glyphs =
        [
            new UiGlyph?[LastCodepoint - FirstCodepoint + 1],
            new UiGlyph?[LastCodepoint - FirstCodepoint + 1],
        ];
    }

    public void Set(UiTextStyle style, char codepoint, in UiGlyph glyph)
    {
        if (codepoint < FirstCodepoint || codepoint > LastCodepoint)
        {
            throw new ArgumentOutOfRangeException(nameof(codepoint));
        }

        _glyphs[(int)style][codepoint - FirstCodepoint] = glyph;
    }

    public float LineHeight(UiTextStyle style) => _lineHeights[(int)style];

    public bool Has(UiTextStyle style, char c) =>
        c >= FirstCodepoint && c <= LastCodepoint && _glyphs[(int)style][c - FirstCodepoint].HasValue;

    // The glyph for c, or the '?' glyph. Returns false only when the table has no '?' either
    // (an empty table), in which case nothing can be drawn.
    public bool TryGet(UiTextStyle style, char c, out UiGlyph glyph)
    {
        UiGlyph?[] row = _glyphs[(int)style];
        if (c >= FirstCodepoint && c <= LastCodepoint && row[c - FirstCodepoint] is { } found)
        {
            glyph = found;
            return true;
        }

        if (row[Fallback - FirstCodepoint] is { } question)
        {
            glyph = question;
            return true;
        }

        glyph = default;
        return false;
    }
}
