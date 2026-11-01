using Dream.Rendering.Hud;
using OpenTK.Mathematics;

namespace Dream.Rendering.Ui.Panels;

// The character window: the second stone window, left of the inventory (the art has no stat
// panel, so this is the combine window's frame with stat rows in its line boxes and no combine
// UI). Rows come from the view's StatRows: label on the left, "base +bonus = effective" on the
// right. Interactive: the close button and, while stat points are free, a "+" per stat that takes one
// (with a Points row below the table). The admin sliders stay in the dev-only settings window.
public static class CharacterWindow
{
    private const float RowX = 19f;
    private const float RowY = 50f;
    private const float RowPitch = 24f;
    private const float RowW = 152f;
    private const float RowH = 10f;
    private const int MaxRows = 6;

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
        // Art first (frame, line boxes, close button), then every line of text, so the window is two
        // draw commands. The text is cut to fit instead of clipped, which would split the command.
        InventoryWindow.DrawFrame(c, s, l, win, inventoryKind: false);
        CharacterStatRow[] rows = v.StatRows ?? [];
        int count = Math.Min(rows.Length, MaxRows);
        bool live = !InventoryUiState.Current.ModalOpen;

        // Unspent stat points come from the same rule the server checks (PlayerStatRules). With none,
        // the window is exactly the plain stat table.
        int points = CharacterPanelModel.PointsAvailable(v.Status);
        Span<bool> spendable = stackalloc bool[MaxRows];
        for (int n = 0; n < count; n++)
        {
            spendable[n] = CharacterPanelModel.CanSpend(v.Status, rows[n].Stat);
            float boxW = spendable[n] ? RowW - SpendW - SpendGap : RowW;
            if (s.TryGet(UiSpriteNames.WinLineBox, out UiSprite lineBox))
            {
                c.NineSlice(lineBox, l.FromUnits(win, RowX, RowY + (n * RowPitch), boxW, RowH), UiColor.White);
            }

            if (spendable[n])
            {
                DrawSpendButton(c, i, l, s, a, win, n, rows[n].Stat, RowY + (n * RowPitch), live);
            }
        }

        if (points > 0 && count < MaxRows && s.TryGet(UiSpriteNames.WinLineBox, out UiSprite pointsBox))
        {
            c.NineSlice(pointsBox, l.FromUnits(win, RowX, RowY + (count * RowPitch), RowW, RowH), UiColor.White);
        }

        InventoryWindow.DrawCloseButton(c, i, l, s, a, win, InventoryWindow.IdCloseCharacter, live);

        InventoryWindow.DrawTextCentered(c, l.FromUnits(win, 35f, 8f, 120f, 8f), "Character", ItemTierStyle.Title);
        if (v.Inventory is Simulation.Inventory.PlayerInventorySnapshot inventory && inventory.PlayerName is { Length: > 0 } name)
        {
            // Server text: cut to the strip, never a format string.
            InventoryWindow.DrawTextCentered(c, l.FromUnits(win, 35f, 20f, 119f, 8f), name.AsSpan(0, Math.Min(name.Length, 40)), ItemTierStyle.Body);
        }

        if (rows.Length == 0)
        {
            InventoryWindow.DrawTextCentered(c, l.FromUnits(win, RowX, RowY, RowW, RowH), "No data", ItemTierStyle.Dim);
        }

        for (int n = 0; n < count; n++)
        {
            DrawRow(c, l, win, rows[n], RowY + (n * RowPitch), spendable[n] ? RowW - SpendW - SpendGap : RowW);
            if (spendable[n])
            {
                InventoryWindow.DrawTextCentered(
                    c, l.FromUnits(win, RowX + RowW - SpendW, RowY + (n * RowPitch), SpendW, RowH), "+", ItemTierStyle.Title);
            }
        }

        if (points > 0 && count < MaxRows)
        {
            DrawPointsRow(c, l, win, points, RowY + (count * RowPitch));
        }
    }

    // A small stone plate right of the stat's row. One click spends one point; hold-free by design,
    // since the server may refuse (the cap, a stale panel) and the next snapshot corrects the panel.
    private static void DrawSpendButton(
        IUiCanvas c, IUiInput i, in UiLayout l, UiSpriteTable s, IHudActions a, UiRect win, int n,
        CharacterStat stat, float y, bool live)
    {
        UiRect r = l.FromUnits(win, RowX + RowW - SpendW, y, SpendW, RowH);
        int id = IdSpend + n;
        bool hovered = live && i.Hovered(id, r);
        if (s.TryGet(hovered ? UiSpriteNames.ButtonHover : UiSpriteNames.ButtonNormal, out UiSprite plate))
        {
            c.Sprite(plate, r, UiColor.White);
        }

        if (live && i.Button(id, r) && CharacterPanelModel.SpendAction(stat) is InventoryHudAction action)
        {
            a.Inventory(action);
        }
    }

    private static void DrawPointsRow(IUiCanvas c, in UiLayout l, UiRect win, int points, float y)
    {
        UiRect box = l.FromUnits(win, RowX, y, RowW, RowH);
        float padX = 2f * l.Scale;
        float textY = box.Y + MathF.Floor((box.H - InventoryWindow.BodyHeight(c)) * 0.5f);
        Span<char> mem = stackalloc char[16];
        UiTextBuilder b = new(mem);
        b.Append(points);
        ReadOnlySpan<char> value = b.Text;
        float width = c.MeasureText(value, UiTextStyle.Body);
        InventoryWindow.DrawText(c, "Points", new Vector2(box.X + padX, textY), ItemTierStyle.Body);
        InventoryWindow.DrawText(c, value, new Vector2(box.Right - padX - width, textY), ItemTierStyle.Bonus);
    }

    private static void DrawRow(IUiCanvas c, in UiLayout l, UiRect win, in CharacterStatRow row, float y, float rowW)
    {
        UiRect box = l.FromUnits(win, RowX, y, rowW, RowH);
        float padX = 2f * l.Scale;
        float textY = box.Y + MathF.Floor((box.H - InventoryWindow.BodyHeight(c)) * 0.5f);

        string label = row.Label ?? string.Empty;
        ReadOnlySpan<char> labelText = label.AsSpan(0, Math.Min(label.Length, 24));
        float inner = box.W - (2f * padX);
        float gap = MathF.Max(6f, 3f * l.Scale);
        float labelWidth = c.MeasureText(labelText, UiTextStyle.Body);

        // "10 +2 = 12", or just "12" when there is no bonus or the label would touch the long form.
        // Built in a stack buffer.
        Span<char> mem = stackalloc char[64];
        UiTextBuilder b = new(mem);
        bool hasBonus = !string.IsNullOrEmpty(row.BonusText);
        if (hasBonus)
        {
            b.Append(row.BaseText ?? string.Empty);
            b.Append(" ");
            b.Append(row.BonusText);
            b.Append(" = ");
            b.Append(row.EffectiveText ?? string.Empty);
            if (labelWidth + gap + c.MeasureText(b.Text, UiTextStyle.Body) > inner)
            {
                // Too long beside this label: show the effective value alone.
                hasBonus = false;
                b = new UiTextBuilder(mem);
                b.Append(row.EffectiveText ?? string.Empty);
            }
        }
        else
        {
            b.Append(row.EffectiveText ?? string.Empty);
        }

        ReadOnlySpan<char> value = b.Text;
        value = value[..PanelText.FitLength(c, value, UiTextStyle.Body, inner)];
        float width = c.MeasureText(value, UiTextStyle.Body);

        // The label gets what the value leaves, with a gap between them.
        labelText = labelText[..PanelText.FitLength(c, labelText, UiTextStyle.Body, MathF.Max(0f, inner - width - gap))];
        InventoryWindow.DrawText(c, labelText, new Vector2(box.X + padX, textY), ItemTierStyle.Body);
        InventoryWindow.DrawText(c, value, new Vector2(box.Right - padX - width, textY), hasBonus ? ItemTierStyle.Bonus : ItemTierStyle.Title);

        if (!string.IsNullOrEmpty(row.Note))
        {
            string note = row.Note;
            InventoryWindow.DrawText(
                c,
                note.AsSpan(0, Math.Min(note.Length, 40)),
                new Vector2(box.X + padX, box.Bottom + l.Scale),
                ItemTierStyle.Dim);
        }
    }
}
