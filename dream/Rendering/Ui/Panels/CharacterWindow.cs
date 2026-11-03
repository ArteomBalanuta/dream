using Dream.Rendering.Hud;
using Dream.Simulation.Stats;
using OpenTK.Mathematics;

namespace Dream.Rendering.Ui.Panels;

// The character window: the second stone window, left of the inventory (the art has no stat
// panel, so this is the combine window's frame with the sheet in its line boxes and no combine UI).
// The sheet comes from the view's Sheet rows: a header block (class, level, experience, points), then
// each attribute on a plaque with a "+" plate that spends one point, and under it what the attribute
// derives. Art first (frame, plaques, plates), then every line of text, so the window is two draw
// commands. The "+" plates and the close button are the only things that take input.
public static class CharacterWindow
{
    private const float RowX = 19f;
    private const float RowW = 152f;
    private const float HeaderY = 38f;
    private const float HeaderPitch = 16f;
    private const float RowH = 12f;
    private const float AttributePitch = 20f;
    private const float DerivedPitch = 16f;
    private const float GroupGap = 7f;
    private const float PlateSize = 14f;
    private const float PlateGap = 2f;
    private const int MaxRows = 20;

    // Text sizes in window units (1 u = Scale px). The baked fonts are 13 and 26 px and the window grows
    // with ui_scale, so fixed-size text shrank relative to its row as the scale rose (13 px in a 36 px
    // row at 2560x1440). The size now follows the scale, never below the baked 13 px, so scale 1 is as before.
    internal const float AttributeTextUnits = 9.5f;
    internal const float DerivedTextUnits = 8f;
    internal const float TitleTextUnits = 7.5f;

    // Below this the thin serif and the engraved capitals are not legible, so the pixel face keeps the text.
    internal const float SerifMinPx = 15f;
    internal const float SerifBoost = 1.2f;

    // The font and glyph multiplier for text about units tall. From SerifMinPx up the text is set in
    // `face` (Serif for rows, Large for the title) baked at 24 or 26 px and scaled to the size, which
    // is a shrink or a small stretch; below it the 13 px Body bake is used, as before.
    internal static (UiTextStyle Style, float Scale, float Px) TextFor(in UiLayout l, float units, UiTextStyle face = UiTextStyle.Serif)
    {
        float px = MathF.Max(UiFontSetup.BodyPixels, MathF.Round(units * l.Scale));
        if (px < SerifMinPx)
        {
            return (UiTextStyle.Body, px / UiFontSetup.BodyPixels, px);
        }

        // EB Garamond's em is small for its letters (x-height about 0.4 em against the pixel face's 0.5),
        // so the serif is set 20 percent larger to read as big as the size the row was given.
        if (face == UiTextStyle.Serif)
        {
            px = MathF.Round(px * SerifBoost);
        }

        float bake = face == UiTextStyle.Large ? UiFontSetup.LargePixels : UiFontSetup.SerifPixels;
        return (face, px / bake, px);
    }

    // Draws text in the sized style with a one-pixel shadow. Sets the canvas text scale first (Build puts it back).
    private static void Put(IUiCanvas c, ReadOnlySpan<char> text, Vector2 pos, (UiTextStyle Style, float Scale, float Px) t, UiColor color)
    {
        c.SetTextScale(t.Scale);
        c.Text(text, new Vector2(pos.X + 1f, pos.Y + 1f), t.Style, ItemTierStyle.Shadow);
        c.Text(text, pos, t.Style, color);
    }

    // Centred in r, cut to fit.
    private static void PutCentered(IUiCanvas c, UiRect r, ReadOnlySpan<char> text, (UiTextStyle Style, float Scale, float Px) t, UiColor color)
    {
        c.SetTextScale(t.Scale);
        text = text[..PanelText.FitLength(c, text, t.Style, r.W)];
        float width = c.MeasureText(text, t.Style);
        Put(c, text, new Vector2(r.X + MathF.Max(0f, (r.W - width) * 0.5f), r.Y + MathF.Floor((r.H - t.Px) * 0.5f)), t, color);
    }

    // Widget ids of the "+" plates, in sheet order. Clear of the inventory's ids.
    internal const int IdSpendBase = InventoryWindow.IdBase + 0x310;

    // The spend button beside a stat row, shown only while a point is free and the stat can take it.
    private const float SpendW = 14f;
    private const float SpendGap = 2f;
    private const int IdSpend = InventoryWindow.IdBase + 0x310;

    public static void Build(IUiCanvas c, IUiInput i, HudView v, in UiLayout l, UiSpriteTable s, IHudActions a)
    {
        if (!v.InventoryOpen)
        {
            return;
        }

        UiRect win = l.Character;
        InventoryWindow.DrawFrame(c, s, l, win, inventoryKind: false);
        CharacterSheetRow[] rows = v.Sheet ?? [];
        int count = Math.Min(rows.Length, MaxRows);
        Span<float> ys = stackalloc float[MaxRows];
        LayoutRows(rows, count, ys);

        bool live = !InventoryUiState.Current.ModalOpen;
        bool haveBox = s.TryGet(UiSpriteNames.WinLineBox, out UiSprite lineBox);
        for (int n = 0; n < count; n++)
        {
            CharacterSheetRow row = rows[n];
            if (row.Kind == CharacterSheetRowKind.Derived || !haveBox)
            {
                continue;
            }

            float w = row.Kind == CharacterSheetRowKind.Attribute ? RowW - PlateSize - PlateGap : RowW;
            c.NineSlice(lineBox, l.FromUnits(win, RowX, ys[n], w, RowH), UiColor.White);
        }

        // The "+" plates are art now; their "+" glyph goes in the text pass below.
        int spendSlot = 0;
        Span<UiRect> plateRects = stackalloc UiRect[MaxRows];
        for (int n = 0; n < count; n++)
        {
            if (rows[n].Kind != CharacterSheetRowKind.Attribute)
            {
                continue;
            }

            UiRect plate = l.FromUnits(win, RowX + RowW - PlateSize, ys[n] - ((PlateSize - RowH) * 0.5f), PlateSize, PlateSize);
            plateRects[n] = plate;
            int id = IdSpendBase + spendSlot;
            bool enabled = live && rows[n].CanSpend;
            bool hovered = enabled && i.Hovered(id, plate);
            string sprite = !rows[n].CanSpend
                ? UiSpriteNames.ButtonDisabled
                : hovered ? UiSpriteNames.ButtonHover : UiSpriteNames.ButtonNormal;
            if (s.TryGet(sprite, out UiSprite art) || s.TryGet(UiSpriteNames.ButtonNormal, out art))
            {
                c.Sprite(art, plate, rows[n].CanSpend ? UiColor.White : new UiColor(150, 150, 150, 255));
            }

            if (enabled && i.Button(id, plate) && rows[n].Attribute is StatId stat
                && CharacterPanelModel.SpendAction(stat) is InventoryHudAction spend)
            {
                // One point, as PlayerStatSpendIntent; the server checks the budget and the cap.
                a.Inventory(spend);
            }

            spendSlot++;
        }

        InventoryWindow.DrawCloseButton(c, i, l, s, a, win, InventoryWindow.IdCloseCharacter, live);

        float canvasScale = c.TextScale;
        var title = TextFor(in l, TitleTextUnits, UiTextStyle.Large);
        var rowText = TextFor(in l, AttributeTextUnits);
        PutCentered(c, l.FromUnits(win, 35f, 8f, 120f, 8f), "Character", title, ItemTierStyle.Title);
        if (v.Inventory is Simulation.Inventory.PlayerInventorySnapshot inventory && inventory.PlayerName is { Length: > 0 } name)
        {
            // Server text: cut to the strip, never a format string.
            PutCentered(c, l.FromUnits(win, 35f, 20f, 119f, 8f), name.AsSpan(0, Math.Min(name.Length, 40)), title, ItemTierStyle.Body);
        }

        if (rows.Length == 0)
        {
            PutCentered(c, l.FromUnits(win, RowX, HeaderY, RowW, RowH), "No data", rowText, ItemTierStyle.Dim);
        }

        for (int n = 0; n < count; n++)
        {
            CharacterSheetRow row = rows[n];
            if (row.Kind == CharacterSheetRowKind.Attribute)
            {
                PutCentered(c, plateRects[n], "+", rowText, row.CanSpend ? ItemTierStyle.Title : ItemTierStyle.Dim);
            }

            DrawRow(c, l, win, row, ys[n]);
        }

        c.SetTextScale(canvasScale);
    }

    // The y of each row, in window units: header rows tight, then a gap before each attribute group.
    internal static void LayoutRows(CharacterSheetRow[] rows, int count, Span<float> ys)
    {
        float y = HeaderY;
        for (int n = 0; n < count; n++)
        {
            CharacterSheetRowKind kind = rows[n].Kind;
            CharacterSheetRowKind previous = n > 0 ? rows[n - 1].Kind : kind;
            if (kind == CharacterSheetRowKind.Attribute)
            {
                y += previous == CharacterSheetRowKind.Header ? GroupGap : GroupGap - 2f;
            }

            ys[n] = y;
            y += kind switch
            {
                CharacterSheetRowKind.Attribute => AttributePitch,
                CharacterSheetRowKind.Derived => DerivedPitch,
                _ => HeaderPitch
            };
        }
    }

    private static void DrawRow(IUiCanvas c, in UiLayout l, UiRect win, in CharacterSheetRow row, float y)
    {
        bool attribute = row.Kind == CharacterSheetRowKind.Attribute;
        bool derived = row.Kind == CharacterSheetRowKind.Derived;
        float indent = derived ? 6f : 0f;
        float width = attribute ? RowW - PlateSize - PlateGap : RowW;
        UiRect box = l.FromUnits(win, RowX + indent, y, width - indent, RowH);
        var t = TextFor(in l, derived ? DerivedTextUnits : AttributeTextUnits);
        c.SetTextScale(t.Scale);
        float padX = 2f * l.Scale;
        float textY = box.Y + MathF.Floor((box.H - t.Px) * 0.5f);
        float inner = box.W - (2f * padX);
        float gap = MathF.Max(6f, 3f * l.Scale);

        string value = row.Value ?? string.Empty;
        ReadOnlySpan<char> valueText = value.AsSpan(0, Math.Min(value.Length, 40));
        valueText = valueText[..PanelText.FitLength(c, valueText, t.Style, inner)];
        float valueWidth = c.MeasureText(valueText, t.Style);

        string label = row.Label ?? string.Empty;
        ReadOnlySpan<char> labelText = label.AsSpan(0, Math.Min(label.Length, 24));
        labelText = labelText[..PanelText.FitLength(c, labelText, t.Style, MathF.Max(0f, inner - valueWidth - gap))];

        // The derived label was Dim, which read as a ghost on the marble; it keeps its smaller size and indent.
        UiColor labelColor = attribute ? ItemTierStyle.Title : ItemTierStyle.Body;
        UiColor valueColor = attribute
            ? (value.Contains('=') ? ItemTierStyle.Bonus : ItemTierStyle.Title)
            : derived ? ItemTierStyle.Body : ItemTierStyle.Title;
        if (row.Kind == CharacterSheetRowKind.Header && row.Label == "Points" && value != "0")
        {
            valueColor = ItemTierStyle.Rare;
        }

        Put(c, labelText, new Vector2(box.X + padX, textY), t, labelColor);
        Put(c, valueText, new Vector2(box.Right - padX - valueWidth, textY), t, valueColor);
    }
}
