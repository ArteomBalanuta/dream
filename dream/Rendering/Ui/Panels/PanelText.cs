using System.Globalization;
using OpenTK.Mathematics;

namespace Dream.Rendering.Ui.Panels;

// Text drawing shared by the panels. Lived at the bottom of StatusBarPanel.cs until the typography
// pass, where three workstreams needed it at once.
internal static class PanelText
{
    private static readonly UiColor Halo = new(0x0A, 0x01, 0x01, 200);

    // Numbers go through int.TryFormat, not an interpolated TryWrite: the handler's generic
    // AppendFormatted<int> boxes its value while it runs in the PGO instrumented tier, which is
    // 24 B per number on every frame until tier 1 lands.
    public static bool TryWriteInt(Span<char> buf, int value, out int written) =>
        value.TryFormat(buf, out written, default, CultureInfo.InvariantCulture);

    public static bool TryWritePair(Span<char> buf, int first, char separator, int second, out int written)
    {
        written = 0;
        if (!TryWriteInt(buf, first, out int a) || a >= buf.Length)
        {
            return false;
        }

        buf[a] = separator;
        if (!TryWriteInt(buf[(a + 1)..], second, out int b))
        {
            return false;
        }

        written = a + 1 + b;
        return true;
    }

    public static bool TryWritePercent(Span<char> buf, int value, out int written)
    {
        if (!TryWriteInt(buf, value, out written) || written >= buf.Length)
        {
            written = 0;
            return false;
        }

        buf[written++] = '%';
        return true;
    }

    // Line height of the style's own bake, in px, times the canvas's text scale. This used to read
    // "Large ? 26 : 13", which gave the 24 px Serif bake a height of 13.
    public static float Height(IUiCanvas c, UiTextStyle style) => UiText.BakePixels(style) * c.TextScale;

    public static void Centred(IUiCanvas c, ReadOnlySpan<char> text, float centreX, float y, UiTextStyle style, UiColor color)
    {
        float w = c.MeasureText(text, style);
        c.Text(text, new Vector2(MathF.Round(centreX - w * 0.5f), MathF.Round(y)), style, color);
    }

    // Dark halo of four 1 px copies, then the text.
    public static void HaloCentred(IUiCanvas c, ReadOnlySpan<char> text, float centreX, float y, UiTextStyle style, UiColor color, float haloPx = 1f)
    {
        float w = c.MeasureText(text, style);
        float x = MathF.Round(centreX - w * 0.5f);
        y = MathF.Round(y);
        c.Text(text, new Vector2(x + haloPx, y), style, Halo);
        c.Text(text, new Vector2(x - haloPx, y), style, Halo);
        c.Text(text, new Vector2(x, y + haloPx), style, Halo);
        c.Text(text, new Vector2(x, y - haloPx), style, Halo);
        c.Text(text, new Vector2(x, y), style, color);
    }

    // Longest prefix of text that fits in maxWidth px.
    public static int FitLength(IUiCanvas c, ReadOnlySpan<char> text, UiTextStyle style, float maxWidth)
    {
        if (c.MeasureText(text, style) <= maxWidth)
        {
            return text.Length;
        }

        int lo = 0;
        int hi = text.Length;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) >> 1;
            if (c.MeasureText(text[..mid], style) <= maxWidth)
            {
                lo = mid;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return lo;
    }
}
