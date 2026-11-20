using OpenTK.Mathematics;

namespace Dream.Rendering.Ui.Panels;

// The compact info panel for the hovered world thing, drawn from HudView.HoverInfo and HudView.HoverPanel
// only. Non-interactive: it registers no widget, so it never counts as HUD under the pointer. It takes the
// pointer position for placement and nothing else from the input.
public static class HoverInfoPanel
{
    // Width per kind, in UI units. Fixed per kind so the panel does not breathe as numbers change.
    public const float EnemyWidthUnits = 140f;
    public const float NpcWidthUnits = 124f;
    public const float ItemWidthUnits = 124f;
    public const float PlayerWidthUnits = 124f;
    public const float OtherWidthUnits = 110f;

    // Optional: skip when the batch is nearly full, as the world labels do. A panel is about 100 quads.
    public const int ReserveQuads = 512;

    private static readonly UiColor HostileTitle = new(0xB8, 0x4A, 0x42, 255);
    private static readonly UiColor FriendlyTitle = new(0xE6, 0xDD, 0xC4, 255);
    private static readonly UiColor PartyTitle = new(0x7F, 0xC4, 0x9E, 255);
    private static readonly UiColor Trough = new(0x12, 0x0D, 0x0C, 255);
    private static readonly UiColor Frame = new(0x05, 0x03, 0x03, 255);

    public static float WidthUnits(HoverKind kind) => kind switch
    {
        HoverKind.Enemy => EnemyWidthUnits,
        HoverKind.Npc => NpcWidthUnits,
        HoverKind.GroundItem => ItemWidthUnits,
        HoverKind.RemotePlayer => PlayerWidthUnits,
        _ => OtherWidthUnits
    };

    public static UiColor TitleColor(HoverInfo info) => info.Defeated
        ? ItemTierStyle.Dim
        : info.Disposition switch
        {
            HoverDisposition.Hostile => HostileTitle,
            HoverDisposition.Friendly => FriendlyTitle,
            HoverDisposition.Party => PartyTitle,
            HoverDisposition.Item => ItemTierStyle.ColorOf(info.Tier),
            _ => ItemTierStyle.Body
        };

    public static UiColor BarFill(HoverBarKind kind) => kind switch
    {
        HoverBarKind.Mana => new UiColor(0x2F, 0x45, 0x7A, 255),
        HoverBarKind.Stamina => new UiColor(0x9A, 0x74, 0x28, 255),
        _ => new UiColor(0x8A, 0x22, 0x20, 255)
    };

    public static void Build(IUiCanvas c, IUiInput i, HudView v, in UiLayout l, UiSpriteTable s)
    {
        HoverPanelState state = v.HoverPanel;
        HoverInfo? info = v.HoverInfo;
        if (!state.Visible || !(state.Alpha > 0f) || info is null || c.QuadHeadroom < ReserveQuads)
        {
            return;
        }

        UiPointer pointer = i.Pointer;
        if (!float.IsFinite(pointer.Position.X) || !float.IsFinite(pointer.Position.Y))
        {
            return;
        }

        float a = MathF.Min(state.Alpha, 1f);
        float scale = l.Scale;
        UiSizedText t = UiText.AtCanvasScale(c);
        float pad = 4f * scale;
        float pitch = t.Px + (3f * scale);
        float barH = t.Px + (2f * scale);
        float barGap = 2f * scale;
        float w = WidthUnits(state.Kind) * scale;
        float inner = w - (2f * pad);

        bool hasSubtitle = !string.IsNullOrEmpty(info.Subtitle);
        bool hasHint = !string.IsNullOrEmpty(info.Hint);
        int bars = Math.Clamp(info.BarCount, 0, HoverInfo.MaxBars);
        int lines = Math.Clamp(info.LineCount, 0, HoverInfo.MaxLines);
        float h = (2f * pad) + pitch
            + (hasSubtitle ? pitch : 0f)
            + (bars > 0 ? ((barH + barGap) * bars) : 0f)
            + (lines * pitch)
            + (hasHint ? pitch : 0f);

        Vector2 pos = HoverPanelPlacement.AtPointer(pointer.Position, w, h, in l, v.InventoryOpen, CursorRect(v, s, pointer.Position, scale));
        UiRect panel = new(pos.X, pos.Y, w, h);
        if (s.TryGet(UiSpriteNames.TipPanel, out UiSprite tip))
        {
            c.NineSlice(tip, panel, ItemTierStyle.WithAlpha(UiColor.White, a));
        }
        else
        {
            c.Solid(panel, ItemTierStyle.WithAlpha(new UiColor(0, 0, 0, 204), a));
        }

        float shadow = UiText.ShadowPx(in l);
        float x = panel.X + pad;
        float y = panel.Y + pad;
        Text(c, info.Title, x, y, inner, in t, TitleColor(info), a, shadow, Align.Centre);
        y += pitch;
        if (hasSubtitle)
        {
            Text(c, info.Subtitle, x, y, inner, in t, ItemTierStyle.Dim, a, shadow, Align.Centre);
            y += pitch;
        }

        float edge = MathF.Max(1f, MathF.Round(scale * 0.5f));
        for (int n = 0; n < bars; n++)
        {
            HoverBar bar = info.Bars[n];
            DrawBar(c, new UiRect(x, y, inner, barH), in bar, info.Defeated, edge, a);
            Text(c, bar.Text, x, y + MathF.Floor((barH - t.Px) * 0.5f), inner, in t, ItemTierStyle.Title, a, shadow, Align.Centre);
            y += barH + barGap;
        }

        for (int n = 0; n < lines; n++)
        {
            HoverLine line = info.Lines[n];
            // The value is cut to what the label leaves, so a long server string never runs over its label.
            float labelW = string.IsNullOrEmpty(line.Label) ? 0f : UiText.Measure(c, line.Label, in t);
            Text(c, line.Label, x, y, inner, in t, ItemTierStyle.Dim, a, shadow, Align.Left);
            Text(c, line.Value, x + labelW + pad, y, MathF.Max(0f, inner - labelW - pad), in t, ItemTierStyle.Body, a, shadow, Align.Right);
            y += pitch;
        }

        if (hasHint)
        {
            Text(c, info.Hint, x, y, inner, in t, ItemTierStyle.Dim, a, shadow, Align.Centre);
        }
    }

    // The cursor sprite's rect at the pointer, so the panel can stay clear of it. Without a sprite, the 32 u box.
    private static UiRect CursorRect(HudView v, UiSpriteTable s, Vector2 position, float scale)
    {
        UiCursorKind kind = UiCursor.Select(false, false, v.HoverCursorKind);
        if (s.TryGet(UiCursor.SpriteName(kind), out UiSprite sprite) || s.TryGet(UiSpriteNames.CursorPointer, out sprite))
        {
            return UiCursor.Rect(in sprite, position, scale);
        }

        return new UiRect(position.X - (2f * scale), position.Y - (2f * scale), 32f * scale, 32f * scale);
    }

    private static void DrawBar(IUiCanvas c, UiRect r, in HoverBar bar, bool defeated, float edge, float alpha)
    {
        c.Solid(r, ItemTierStyle.WithAlpha(Frame, alpha));
        UiRect well = new(r.X + edge, r.Y + edge, MathF.Max(0f, r.W - (2f * edge)), MathF.Max(0f, r.H - (2f * edge)));
        c.Solid(well, ItemTierStyle.WithAlpha(Trough, alpha));
        float fraction = defeated || bar.Max <= 0 ? 0f : Math.Clamp((float)bar.Current / bar.Max, 0f, 1f);
        float fill = MathF.Round(well.W * fraction);
        if (fill > 0f)
        {
            UiColor colour = BarFill(bar.Kind);
            c.Solid(new UiRect(well.X, well.Y, fill, well.H), ItemTierStyle.WithAlpha(colour, alpha));
            // A lit upper edge, so the bar reads as a lacquered rod and not a flat slab.
            c.Solid(new UiRect(well.X, well.Y, fill, MathF.Min(edge, well.H)), ItemTierStyle.WithAlpha(Lift(colour), alpha));
        }
    }

    private static UiColor Lift(UiColor c) => new(
        (byte)Math.Min(255, c.R + 0x30), (byte)Math.Min(255, c.G + 0x20), (byte)Math.Min(255, c.B + 0x20), c.A);

    private enum Align : byte { Left, Centre, Right }

    // One server string, cut to maxWidth, with a shadow whose alpha follows the panel's.
    private static void Text(
        IUiCanvas c, string text, float x, float y, float maxWidth, in UiSizedText t, UiColor color, float alpha, float shadowPx, Align align)
    {
        if (string.IsNullOrEmpty(text) || maxWidth <= 0f)
        {
            return;
        }

        ReadOnlySpan<char> span = text.AsSpan();
        span = span[..UiText.FitLength(c, span, in t, maxWidth)];
        if (span.IsEmpty)
        {
            return;
        }

        float width = UiText.Measure(c, span, in t);
        float left = align switch
        {
            Align.Centre => x + MathF.Max(0f, (maxWidth - width) * 0.5f),
            Align.Right => x + MathF.Max(0f, maxWidth - width),
            _ => x
        };
        left = MathF.Round(left);
        y = MathF.Round(y);
        float previous = c.TextScale;
        c.SetTextScale(t.Scale);
        c.Text(span, new Vector2(left + shadowPx, y + shadowPx), t.Style, ItemTierStyle.WithAlpha(ItemTierStyle.Shadow, alpha));
        c.Text(span, new Vector2(left, y), t.Style, ItemTierStyle.WithAlpha(color, alpha));
        c.SetTextScale(previous);
    }
}
