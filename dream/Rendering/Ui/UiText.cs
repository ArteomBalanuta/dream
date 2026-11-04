using OpenTK.Mathematics;

namespace Dream.Rendering.Ui;

// A text size asked for in window units, resolved to a bake and a glyph multiplier.
// Px is the drawn line box in screen pixels: the height a panel must fit the text into.
public readonly record struct UiSizedText(UiTextStyle Style, float Scale, float Px);

// Which face a panel asks for. UiText picks the bake closest to the drawn size within that face,
// so the face never changes with ui_scale -- only which bake of it is stretched or shrunk.
//   Pixel  ProggyClean: every panel's small text, item names, chat, counts.
//   Title  Cinzel: window titles, menu titles, the engraved numbers.
//   Serif  EB Garamond: the stats window.
public enum UiTextFace : byte
{
    Pixel,
    Title,
    Serif
}

// The one place that turns "text about this many units tall" into a bake and a multiplier.
//
// The panels lay out in window units (1 u = UiLayout.Scale px) but used to draw text at a fixed
// baked size, so text shrank relative to its own row as ui_scale rose: 13 px in a 36 px row at
// 2560x1440. Here the size follows the scale, and the bake nearest that size is used so the glyphs
// stay close to their pixel grid instead of being a 13 px bitmap stretched to three times its height.
//
// Every entry point that draws or measures sets the canvas text scale and RESTORES it before
// returning. One canvas draws every panel in the frame, and several panels return early, so a
// panel that left a scale behind would resize the panels drawn after it in some game states only.
public static class UiText
{
    // A 12 u row's text: the size the stats window gives its attribute rows, and the default for a
    // panel that lays its rows out from the text height rather than from the art.
    public const float DefaultTextUnits = 9.5f;

    // Below this the engraved capitals and the thin serif are not legible, so the pixel face keeps
    // the text whatever face was asked for.
    public const float TitleMinPx = 15f;
    public const float SerifMinPx = 15f;

    // EB Garamond's em is small for its letters (x-height about 0.4 em against the pixel face's 0.5),
    // so the serif is set larger to read as big as the size its row was given.
    public const float SerifBoost = 1.2f;

    // At and above this the pixel face uses its 26 px bake rather than stretching the 13 px one.
    public const float PixelLargeFromPx = 20f;

    public static float BakePixels(UiTextStyle style) => style switch
    {
        UiTextStyle.Large => UiFontSetup.LargePixels,
        UiTextStyle.Serif => UiFontSetup.SerifPixels,
        UiTextStyle.BodyLarge => UiFontSetup.BodyLargePixels,
        _ => UiFontSetup.BodyPixels
    };

    // The bake and multiplier for text about units tall, never below the 13 px bake (so scale 1 is
    // exactly as it was before any of this).
    public static UiSizedText For(in UiLayout l, float units, UiTextFace face = UiTextFace.Pixel)
    {
        float px = MathF.Max(UiFontSetup.BodyPixels, MathF.Round(units * l.Scale));
        switch (face)
        {
            case UiTextFace.Title:
                return px < TitleMinPx
                    ? new UiSizedText(UiTextStyle.Body, px / UiFontSetup.BodyPixels, px)
                    : new UiSizedText(UiTextStyle.Large, px / UiFontSetup.LargePixels, px);

            case UiTextFace.Serif:
                if (px < SerifMinPx)
                {
                    return new UiSizedText(UiTextStyle.Body, px / UiFontSetup.BodyPixels, px);
                }

                px = MathF.Round(px * SerifBoost);
                return new UiSizedText(UiTextStyle.Serif, px / UiFontSetup.SerifPixels, px);

            default:
                return px < PixelLargeFromPx
                    ? new UiSizedText(UiTextStyle.Body, px / UiFontSetup.BodyPixels, px)
                    : new UiSizedText(UiTextStyle.BodyLarge, px / UiFontSetup.BodyLargePixels, px);
        }
    }

    // For(), but never taller than maxUnits: a label in a box the art fixed cannot outgrow it.
    public static UiSizedText For(in UiLayout l, float units, float maxUnits, UiTextFace face = UiTextFace.Pixel)
    {
        UiSizedText t = For(in l, units, face);
        float cap = MathF.Max(UiFontSetup.BodyPixels, maxUnits * l.Scale);
        if (t.Px <= cap)
        {
            return t;
        }

        // Shrink the asked-for units until the resolved size fits. The Serif boost is applied inside
        // For, so the cap has to be checked against the resolved Px rather than against units * Scale.
        float shrunk = units * (cap / t.Px);
        UiSizedText capped = For(in l, shrunk, face);
        return capped.Px <= cap ? capped : new UiSizedText(capped.Style, cap / BakePixels(capped.Style), cap);
    }

    // Top of a line box of t centred vertically in r.
    public static float TextY(UiRect r, in UiSizedText t) => r.Y + MathF.Floor((r.H - t.Px) * 0.5f);

    // A shadow and rim offset that keeps up with the text: a 1 px hairline under 38 px glyphs reads
    // as a scratch rather than a shadow.
    public static float ShadowPx(in UiLayout l) => MathF.Max(1f, MathF.Round(l.Scale * 0.5f));

    public static float Measure(IUiCanvas c, ReadOnlySpan<char> text, in UiSizedText t)
    {
        float previous = c.TextScale;
        c.SetTextScale(t.Scale);
        float width = c.MeasureText(text, t.Style);
        c.SetTextScale(previous);
        return width;
    }

    // Longest prefix of text that fits maxWidthPx at this size.
    public static int FitLength(IUiCanvas c, ReadOnlySpan<char> text, in UiSizedText t, float maxWidthPx)
    {
        float previous = c.TextScale;
        c.SetTextScale(t.Scale);
        int length = Panels.PanelText.FitLength(c, text, t.Style, maxWidthPx);
        c.SetTextScale(previous);
        return length;
    }

    public static void Put(IUiCanvas c, ReadOnlySpan<char> text, Vector2 pos, in UiSizedText t, UiColor color, float shadowPx = 1f)
    {
        float previous = c.TextScale;
        c.SetTextScale(t.Scale);
        c.Text(text, new Vector2(pos.X + shadowPx, pos.Y + shadowPx), t.Style, Panels.ItemTierStyle.Shadow);
        c.Text(text, pos, t.Style, color);
        c.SetTextScale(previous);
    }

    // Centred in r, cut to fit its width.
    public static void PutCentered(IUiCanvas c, UiRect r, ReadOnlySpan<char> text, in UiSizedText t, UiColor color, float shadowPx = 1f)
    {
        float previous = c.TextScale;
        c.SetTextScale(t.Scale);
        text = text[..Panels.PanelText.FitLength(c, text, t.Style, r.W)];
        float width = c.MeasureText(text, t.Style);
        c.SetTextScale(previous);
        Put(c, text, new Vector2(r.X + MathF.Max(0f, (r.W - width) * 0.5f), TextY(r, in t)), in t, color, shadowPx);
    }

    // Left-aligned padPx in from r.X, vertically centred, cut to the room that leaves.
    public static void PutLeft(IUiCanvas c, UiRect r, float padPx, ReadOnlySpan<char> text, in UiSizedText t, UiColor color, float shadowPx = 1f)
    {
        float previous = c.TextScale;
        c.SetTextScale(t.Scale);
        text = text[..Panels.PanelText.FitLength(c, text, t.Style, MathF.Max(0f, r.W - padPx * 2f))];
        c.SetTextScale(previous);
        Put(c, text, new Vector2(r.X + padPx, TextY(r, in t)), in t, color, shadowPx);
    }

    // Right-aligned padPx in from r.Right, vertically centred. Not cut: the caller reserves the room.
    public static void PutRight(IUiCanvas c, UiRect r, float padPx, ReadOnlySpan<char> text, in UiSizedText t, UiColor color, float shadowPx = 1f)
    {
        float width = Measure(c, text, in t);
        Put(c, text, new Vector2(r.Right - padPx - width, TextY(r, in t)), in t, color, shadowPx);
    }

    // Four dark copies then the text, the halo PanelText draws for text over the world.
    public static void PutHaloCentred(IUiCanvas c, ReadOnlySpan<char> text, float centreX, float y, in UiSizedText t, UiColor color, float haloPx = 1f)
    {
        float previous = c.TextScale;
        c.SetTextScale(t.Scale);
        Panels.PanelText.HaloCentred(c, text, centreX, y, t.Style, color, haloPx);
        c.SetTextScale(previous);
    }
}
